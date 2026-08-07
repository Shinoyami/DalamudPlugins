using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MitPlanRecorder;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mitrec";
    private static readonly string[] Categories = ["Savage", "Extreme", "Ultimate", "Custom"];
    private static readonly string[] Jobs = ["Any Job", "AST", "BLM", "BLU", "BRD", "DNC", "DRG", "DRK", "GNB", "MCH", "MNK", "NIN", "PCT", "PLD", "RDM", "RPR", "SAM", "SCH", "SGE", "SMN", "VPR", "WAR", "WHM"];
    private static readonly string[] Roles = ["Any Role", "MT", "OT", "Pure Healer (H1)", "Shield Healer (H2)", "Melee 1 (M1) (D1)", "Melee 2 (M2) (D2)", "Phys Ranged (R1) (D3)", "Caster (R2) (D4)"];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly ActionEffectWatcher actionEffectWatcher;
    private readonly FileDialogManager fileDialogs = new();
    private readonly Dictionary<uint, string> actionNames = [];
    private readonly HashSet<(nint Address, uint ActionId)> activeCasts = [];
    private readonly HashSet<uint> currentPhaseActorEntityIds = [];
    private readonly Dictionary<uint, DateTime> missingPhaseActorsSince = [];

    private Configuration configuration;
    private RecordingFile recording = NewRecording();
    private CsvDocument? csv;
    private readonly List<CsvMatch> matches = [];
    private DateTime? pullStartedAt;
    private bool currentPhaseEnded;
    private bool wasInCombat;
    private bool windowOpen;
    private string status = "Waiting for combat.";

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, ICondition condition,
        IObjectTable objectTable, IDataManager dataManager, IClientState clientState,
        IGameInteropProvider interop, ICommandManager commandManager, IChatGui chatGui)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        actionEffectWatcher = new ActionEffectWatcher(interop);
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Migrate();
        SaveConfiguration();
        LoadLatestRecording();

        commandManager.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Open MitPlan Recorder, or use start, stop, phase, clear." });
        framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        pluginInterface.UiBuilder.OpenMainUi += Open;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        commandManager.RemoveHandler(Command);
        actionEffectWatcher.Dispose();
        fileDialogs.Reset();
    }

    private void Open() => windowOpen = true;

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "start": StartRecording(); break;
            case "stop": StopRecording(); break;
            case "phase": MarkPhase(false); break;
            case "clear": ClearRecording(); break;
            default: windowOpen = true; break;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (configuration.AutoRecord && inCombat && !wasInCombat)
            StartRecording();
        else if (configuration.AutoRecord && !inCombat && wasInCombat)
            StopRecording();

        if (pullStartedAt is not null && inCombat)
        {
            CaptureCastStarts();
            CaptureResolvedAbilities();
            DetectCurrentPhaseEnd();
        }
        else
            actionEffectWatcher.Clear();
        wasInCombat = inCombat;
    }

    private unsafe void StartRecording()
    {
        recording = NewRecording();
        recording.RecordedAtUtc = DateTime.UtcNow;
        recording.TerritoryType = (ushort)clientState.TerritoryType;
        var gameMain = GameMain.Instance();
        recording.ContentFinderConditionId = gameMain == null ? 0u : (uint)gameMain->CurrentContentFinderConditionId;
        recording.FightName = DutyName(recording.ContentFinderConditionId);
        recording.Phases.Add(new RecordedPhase { Name = "P1", StartSeconds = 0, AwaitingAnchor = true });
        pullStartedAt = DateTime.UtcNow;
        activeCasts.Clear();
        currentPhaseActorEntityIds.Clear();
        missingPhaseActorsSince.Clear();
        actionEffectWatcher.Clear();
        matches.Clear();
        currentPhaseEnded = false;
        status = $"Recording {recording.FightName}.";
    }

    private void StopRecording()
    {
        if (pullStartedAt is null)
            return;
        pullStartedAt = null;
        activeCasts.Clear();
        actionEffectWatcher.Clear();
        status = $"Pull saved in memory: {recording.Events.Count} events, {recording.Phases.Count} phases.";
        SaveLatestRecording();
    }

    private void ClearRecording()
    {
        pullStartedAt = null;
        recording = NewRecording();
        csv = null;
        matches.Clear();
        status = "Recording cleared.";
    }

    private void CaptureCastStarts()
    {
        var current = new HashSet<(nint Address, uint ActionId)>();
        foreach (var actor in objectTable.OfType<IBattleNpc>())
        {
            try
            {
                if (!actor.IsCasting || actor.CastActionId == 0)
                    continue;
                var key = (actor.Address, actor.CastActionId);
                current.Add(key);
                if (activeCasts.Contains(key))
                    continue;
                RecordEvent(RecordedEventKind.CastStart, actor.CastActionId, actor);
            }
            catch (NullReferenceException) { }
        }
        activeCasts.Clear();
        activeCasts.UnionWith(current);
    }

    private void CaptureResolvedAbilities()
    {
        while (actionEffectWatcher.TryDequeue(out var resolved))
        {
            var actor = objectTable.OfType<IBattleNpc>().FirstOrDefault(item => item.EntityId == resolved.CasterEntityId);
            if (actor is null)
                continue;
            RecordEvent(RecordedEventKind.Ability, resolved.ActionId, actor, resolved.OccurredAtUtc);
        }
    }

    private void RecordEvent(RecordedEventKind kind, uint actionId, IBattleNpc actor, DateTime? occurredAtUtc = null)
    {
        var elapsed = occurredAtUtc is not null && pullStartedAt is not null
            ? Math.Max(0, (occurredAtUtc.Value - pullStartedAt.Value).TotalSeconds)
            : ElapsedSeconds;
        if (currentPhaseEnded)
        {
            var sameActorReturned = currentPhaseActorEntityIds.Contains(actor.EntityId);
            if (sameActorReturned)
            {
                currentPhaseEnded = false;
                missingPhaseActorsSince.Remove(actor.EntityId);
                status = "The previous boss returned; cancelled the automatic phase candidate.";
            }
            else
                BeginNextPhase(elapsed, true);
        }
        currentPhaseActorEntityIds.Add(actor.EntityId);
        var phaseIndex = Math.Max(0, recording.Phases.Count - 1);
        var recentCast = kind == RecordedEventKind.Ability && recording.Events.Any(item =>
            item.Kind == RecordedEventKind.CastStart && item.ActionId == actionId && item.SourceEntityId == actor.EntityId && elapsed - item.TimeSeconds < 30);
        var item = new RecordedEvent
        {
            TimeSeconds = elapsed,
            Kind = kind,
            ActionId = actionId,
            ActionName = ActionName(actionId),
            SourceName = actor.Name.ToString(),
            SourceBaseId = actor.BaseId,
            SourceEntityId = actor.EntityId,
            PhaseIndex = phaseIndex,
            // Mitigation rows use the action-effect timestamp when the mechanic resolves.
            // Cast starts remain available for synchronization and manual inclusion.
            Included = kind == RecordedEventKind.Ability,
            UseAsSyncAnchor = kind == RecordedEventKind.CastStart || !recentCast,
        };
        recording.Events.Add(item);
        var phase = recording.Phases[phaseIndex];
        if (phase.AwaitingAnchor)
        {
            phase.AnchorEventId = item.Id;
            phase.AwaitingAnchor = false;
            status = $"{phase.Name} anchor candidate: {item.ActionName} (0x{actionId:X}).";
        }
    }

    private void DetectCurrentPhaseEnd()
    {
        if (!configuration.AutoCreatePhaseCandidates || currentPhaseEnded || currentPhaseActorEntityIds.Count == 0)
            return;
        var actors = objectTable.OfType<IBattleNpc>().ToDictionary(actor => actor.EntityId);
        var now = DateTime.UtcNow;
        foreach (var entityId in currentPhaseActorEntityIds)
        {
            if (actors.ContainsKey(entityId))
                missingPhaseActorsSince.Remove(entityId);
            else
                missingPhaseActorsSince.TryAdd(entityId, now);
        }
        var allEnded = currentPhaseActorEntityIds.All(entityId =>
        {
            if (actors.TryGetValue(entityId, out var actor))
                return actor.IsDead || (!actor.IsTargetable && actor.CurrentHp <= 1);
            return missingPhaseActorsSince.TryGetValue(entityId, out var missingSince) &&
                   now - missingSince >= TimeSpan.FromSeconds(configuration.PhaseDowntimeSeconds);
        });
        if (!allEnded)
            return;
        currentPhaseEnded = true;
        status = $"{recording.Phases[^1].Name} boss ended; waiting for the next phase's first cast.";
    }

    private void MarkPhase(bool automatic)
    {
        if (pullStartedAt is null)
            return;
        BeginNextPhase(ElapsedSeconds, automatic);
    }

    private void BeginNextPhase(double startSeconds, bool automatic)
    {
        var index = recording.Phases.Count + 1;
        recording.Phases.Add(new RecordedPhase { Name = $"P{index}", StartSeconds = startSeconds, AwaitingAnchor = true });
        currentPhaseActorEntityIds.Clear();
        missingPhaseActorsSince.Clear();
        currentPhaseEnded = false;
        status = automatic ? $"Proposed P{index} after the previous boss ended; waiting for its first cast." : $"Marked P{index}; waiting for its first cast.";
    }

    private double ElapsedSeconds => pullStartedAt is null ? recording.Events.LastOrDefault()?.TimeSeconds ?? 0 : (DateTime.UtcNow - pullStartedAt.Value).TotalSeconds;

    private string ActionName(uint actionId)
    {
        if (actionNames.TryGetValue(actionId, out var cached))
            return cached;
        var name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(row => row.RowId == actionId)
            .Select(row => row.Name.ToString())
            .FirstOrDefault();
        name = string.IsNullOrWhiteSpace(name) ? $"Action 0x{actionId:X}" : name;
        actionNames[actionId] = name;
        return name;
    }

    private string DutyName(uint contentFinderId)
    {
        if (contentFinderId == 0)
            return "New Recorded Fight";
        var name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>()
            .Where(row => row.RowId == contentFinderId)
            .Select(row => row.Name.ToString())
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(name) ? $"Duty {contentFinderId}" : name;
    }

    private void Draw()
    {
        if (windowOpen)
            DrawWindow();
        fileDialogs.Draw();
    }

    private void DrawWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(1120, 760), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MitPlan Recorder##Main", ref windowOpen))
        {
            ImGui.End();
            return;
        }
        DrawRecordingControls();
        if (ImGui.BeginTabBar("RecorderTabs"))
        {
            if (ImGui.BeginTabItem("Recorded timeline")) { DrawTimeline(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Phases and anchors")) { DrawPhases(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Import mitigation CSV")) { DrawCsvImport(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Export to MitPlan")) { DrawExport(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawRecordingControls()
    {
        var auto = configuration.AutoRecord;
        if (ImGui.Checkbox("Record automatically in combat", ref auto)) { configuration.AutoRecord = auto; SaveConfiguration(); }
        ImGui.SameLine();
        var autoPhases = configuration.AutoCreatePhaseCandidates;
        if (ImGui.Checkbox("Propose phases after downtime", ref autoPhases)) { configuration.AutoCreatePhaseCandidates = autoPhases; SaveConfiguration(); }
        ImGui.SameLine();
        if (pullStartedAt is null)
        {
            if (ImGui.Button("Start recording")) StartRecording();
        }
        else if (ImGui.Button("Stop recording")) StopRecording();
        ImGui.SameLine();
        if (ImGui.Button("Mark new phase")) MarkPhase(false);
        ImGui.SameLine();
        if (ImGui.Button("Clear")) ClearRecording();

        ImGui.SetNextItemWidth(420);
        var fightName = recording.FightName;
        if (ImGui.InputText("Fight name", ref fightName, 160)) recording.FightName = fightName;
        var category = Array.IndexOf(Categories, recording.Category);
        if (category < 0) category = Categories.Length - 1;
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Category", ref category, Categories, Categories.Length)) recording.Category = Categories[category];
        ImGui.Text($"CFC ID: {recording.ContentFinderConditionId} | Territory: {recording.TerritoryType} | Events: {recording.Events.Count}");
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f), status);
        ImGui.TextWrapped("Timeline timing is recorded from resolved action effects, when mechanics actually hit. Cast starts are retained for synchronization anchors.");
        ImGui.Separator();
    }

    private void DrawTimeline()
    {
        ImGui.TextWrapped("Resolved abilities are included by default at the moment the mechanic hits. Cast starts remain recorded but unchecked for timeline use, and can be selected as phase or one-shot synchronization anchors. Enter a skill and select its job/role target; semicolons allow multiple instructions with the same target.");
        if (!ImGui.BeginTable("RecordedEvents", 12, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0, 560)))
            return;
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Phase", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 135);
        ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("Mitigation", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 105);
        ImGui.TableSetupColumn("Sync", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Anchor", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableHeadersRow();
        foreach (var item in recording.Events)
        {
            var phase = recording.Phases.ElementAtOrDefault(item.PhaseIndex);
            if (phase?.AnchorEventId == item.Id)
                DrawAnchorMarkerRow(phase);

            ImGui.PushID(item.Id);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); var included = item.Included; if (ImGui.Checkbox("##use", ref included)) item.Included = included;
            ImGui.TableNextColumn(); ImGui.Text(FormatTime(item.TimeSeconds));
            ImGui.TableNextColumn(); ImGui.Text(phase?.Name ?? "P1");
            ImGui.TableNextColumn(); ImGui.Text(item.Kind == RecordedEventKind.CastStart ? "Cast" : "Ability");
            ImGui.TableNextColumn(); ImGui.Text($"0x{item.ActionId:X}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.SourceName);
            ImGui.TableNextColumn(); var mechanic = item.ActionName; ImGui.SetNextItemWidth(-1); if (ImGui.InputText("##mechanic", ref mechanic, 160)) item.ActionName = mechanic;
            ImGui.TableNextColumn(); var mitigation = item.ManualMitigation; ImGui.SetNextItemWidth(-1); if (ImGui.InputText("##mit", ref mitigation, 300)) item.ManualMitigation = mitigation;
            ImGui.TableNextColumn(); DrawStringCombo("##manualjob", Jobs, item.ManualTargetJob, value => item.ManualTargetJob = value);
            ImGui.TableNextColumn(); DrawStringCombo("##manualrole", Roles, item.ManualTargetRole, value => item.ManualTargetRole = value);
            ImGui.TableNextColumn(); var sync = item.UseAsSyncAnchor; if (ImGui.Checkbox("##sync", ref sync)) item.UseAsSyncAnchor = sync;
            ImGui.TableNextColumn();
            if (phase is not null)
            {
                var selected = phase.AnchorEventId == item.Id;
                if (ImGui.SmallButton(selected ? "Anchored" : "Anchor"))
                {
                    phase.AnchorEventId = item.Id;
                    phase.AwaitingAnchor = false;
                    status = $"{phase.Name} anchored to {item.ActionName} (0x{item.ActionId:X}), occurrence {AnchorOccurrence(item)}.";
                }
            }
            else
                ImGui.TextDisabled("-");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawAnchorMarkerRow(RecordedPhase phase)
    {
        var label = phase.Name.Length > 1 && phase.Name[0] == 'P' &&
                    int.TryParse(phase.Name[1..], out var phaseNumber)
            ? $"Phase {phaseNumber} anchor"
            : $"{phase.Name} anchor";
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(2);
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f), label);
        ImGui.TableSetColumnIndex(6);
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f), $"--- {label} ---");
    }

    private int AnchorOccurrence(RecordedEvent anchor) => recording.Events
        .TakeWhile(item => item.Id != anchor.Id)
        .Count(item => item.PhaseIndex == anchor.PhaseIndex && item.Kind == anchor.Kind &&
                       item.ActionId == anchor.ActionId) + 1;

    private void DrawPhases()
    {
        ImGui.TextWrapped("The first recorded cast or ability after a phase marker becomes its proposed phase anchor. You can select a different event here. Checkpoint permits the anchor even when combat starts directly in that phase. Timeline events marked Sync become additional one-shot mechanic anchors.");
        for (var index = 0; index < recording.Phases.Count; index++)
        {
            var phase = recording.Phases[index];
            ImGui.PushID(index);
            var name = phase.Name;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputText("Phase", ref name, 32)) phase.Name = name;
            ImGui.SameLine(); ImGui.Text($"starts {FormatTime(phase.StartSeconds)}");
            var checkpoint = phase.AllowCheckpointStart;
            ImGui.SameLine(); if (ImGui.Checkbox("Checkpoint", ref checkpoint)) phase.AllowCheckpointStart = checkpoint;
            if (index > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove phase"))
                {
                    RemovePhase(index);
                    ImGui.PopID();
                    break;
                }
            }
            var anchor = recording.Events.FirstOrDefault(item => item.Id == phase.AnchorEventId);
            var preview = anchor is null ? "No anchor selected" : $"{FormatTime(anchor.TimeSeconds)} {anchor.ActionName} (0x{anchor.ActionId:X})";
            ImGui.SetNextItemWidth(620);
            if (ImGui.BeginCombo("Anchor", preview))
            {
                foreach (var item in recording.Events.Where(item => item.PhaseIndex == index))
                    if (ImGui.Selectable($"{FormatTime(item.TimeSeconds)} {item.Kind} {item.ActionName} (0x{item.ActionId:X})", item.Id == phase.AnchorEventId))
                    {
                        phase.AnchorEventId = item.Id;
                        phase.AwaitingAnchor = false;
                    }
                ImGui.EndCombo();
            }
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawCsvImport()
    {
        if (ImGui.Button("Import CSV..."))
            fileDialogs.OpenFileDialog("Select mitigation CSV", "CSV files{.csv},All files{.*}",
                (success, paths) => OnCsvSelected(success, paths.FirstOrDefault() ?? string.Empty), 1,
                configuration.LastCsvDirectory, false);
        ImGui.SameLine();
        ImGui.TextDisabled(csv?.Path ?? "No CSV loaded");
        if (csv is null)
            return;

        var headerRow = csv.HeaderRowIndex + 1;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Header row", ref headerRow)) headerRow = Math.Max(1, headerRow);
        ImGui.SameLine();
        if (ImGui.Button("Reload with this header row"))
        {
            try
            {
                csv = CsvImporter.Read(csv.Path, headerRow - 1);
                matches.Clear();
                status = $"Reloaded CSV using row {headerRow} as the header.";
            }
            catch (Exception exception) { status = $"CSV reload failed: {exception.Message}"; }
        }

        var timeColumn = csv.TimeColumn;
        if (DrawCsvColumnCombo("Time column", ref timeColumn, true)) csv.TimeColumn = timeColumn;
        var mechanicColumn = csv.MechanicColumn;
        if (DrawCsvColumnCombo("Mechanic column", ref mechanicColumn, false)) csv.MechanicColumn = mechanicColumn;
        var phaseColumn = csv.PhaseColumn;
        if (DrawCsvColumnCombo("Phase column", ref phaseColumn, true)) csv.PhaseColumn = phaseColumn;
        ImGui.Text("Mitigation columns and targets:");
        if (ImGui.BeginTable("CsvColumns", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, new Vector2(0, 180)))
        {
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Column");
            ImGui.TableSetupColumn("Job");
            ImGui.TableSetupColumn("Role");
            ImGui.TableHeadersRow();
            foreach (var column in csv.MitigationColumns.Where(column => column.Index != csv.TimeColumn && column.Index != csv.MechanicColumn && column.Index != csv.PhaseColumn))
            {
                ImGui.PushID(column.Index);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); var use = column.Included; if (ImGui.Checkbox("##use", ref use)) column.Included = use;
                ImGui.TableNextColumn(); ImGui.TextUnformatted(column.Header);
                ImGui.TableNextColumn(); DrawStringCombo("##job", Jobs, column.TargetJob, value => column.TargetJob = value);
                ImGui.TableNextColumn(); DrawStringCombo("##role", Roles, column.TargetRole, value => column.TargetRole = value);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (ImGui.Button("Match CSV rows to recorded mechanics")) MatchCsv();
        ImGui.SameLine();
        if (ImGui.Button("Apply reviewed matches")) ApplyMatches();
        DrawCsvMatches();
    }

    private bool DrawCsvColumnCombo(string label, ref int selected, bool allowNone)
    {
        if (csv is null) return false;
        var changed = false;
        var preview = selected >= 0 && selected < csv.Headers.Count ? csv.Headers[selected] : "None";
        ImGui.SetNextItemWidth(260);
        if (!ImGui.BeginCombo(label, preview)) return false;
        if (allowNone && ImGui.Selectable("None", selected < 0)) { selected = -1; changed = true; }
        for (var index = 0; index < csv.Headers.Count; index++)
            if (ImGui.Selectable(csv.Headers[index], index == selected)) { selected = index; changed = true; }
        ImGui.EndCombo();
        return changed;
    }

    private static void DrawStringCombo(string label, IReadOnlyList<string> values, string selected, Action<string> set)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, selected)) return;
        foreach (var value in values)
            if (ImGui.Selectable(value, value == selected)) set(value);
        ImGui.EndCombo();
    }

    private void DrawCsvMatches()
    {
        if (matches.Count == 0 || csv is null) return;
        if (!ImGui.BeginTable("CsvMatches", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0, 275))) return;
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("Row", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("CSV time", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("CSV mechanic", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Recorded event", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Confidence", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Mitigation", ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableHeadersRow();
        foreach (var match in matches)
        {
            ImGui.PushID(match.CsvRowIndex);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); var applied = match.Applied; if (ImGui.Checkbox("##apply", ref applied)) match.Applied = applied;
            ImGui.TableNextColumn(); ImGui.Text((match.CsvRowIndex + csv.HeaderRowIndex + 2).ToString());
            ImGui.TableNextColumn(); ImGui.Text(match.CsvTimeSeconds is null ? "-" : FormatTime(match.CsvTimeSeconds.Value));
            ImGui.TableNextColumn(); ImGui.TextWrapped(match.CsvMechanic);
            ImGui.TableNextColumn(); DrawEventMatchCombo(match);
            ImGui.TableNextColumn(); ImGui.TextColored(match.Confidence >= 0.75 ? new Vector4(0.3f, 0.9f, 0.4f, 1) : new Vector4(1f, 0.7f, 0.2f, 1), $"{match.Confidence:P0}");
            ImGui.TableNextColumn(); ImGui.TextWrapped(CsvMitigationSummary(match.CsvRowIndex));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawEventMatchCombo(CsvMatch match)
    {
        var selected = recording.Events.FirstOrDefault(item => item.Id == match.EventId);
        var preview = selected is null ? "Unmatched" : $"{FormatTime(selected.TimeSeconds)} {selected.ActionName}";
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##event", preview)) return;
        if (ImGui.Selectable("Unmatched", selected is null)) match.EventId = null;
        foreach (var item in recording.Events.Where(item => item.Included))
            if (ImGui.Selectable($"{FormatTime(item.TimeSeconds)} {item.ActionName} ({item.SourceName})", item.Id == match.EventId))
            {
                match.EventId = item.Id;
                match.Confidence = CsvImporter.NameSimilarity(match.CsvMechanic, item.ActionName);
            }
        ImGui.EndCombo();
    }

    private void OnCsvSelected(bool success, string path)
    {
        if (!success) return;
        try
        {
            csv = CsvImporter.Read(path);
            configuration.LastCsvDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            SaveConfiguration();
            matches.Clear();
            status = $"Loaded {csv.Rows.Count} CSV rows.";
        }
        catch (Exception exception)
        {
            status = $"CSV import failed: {exception.Message}";
        }
    }

    private void MatchCsv()
    {
        matches.Clear();
        if (csv is null || csv.MechanicColumn < 0 || csv.MechanicColumn >= csv.Headers.Count)
        {
            status = "Select a mechanic column first.";
            return;
        }
        string lastMechanic = string.Empty;
        string lastPhase = string.Empty;
        double? lastTime = null;
        for (var rowIndex = 0; rowIndex < csv.Rows.Count; rowIndex++)
        {
            var row = csv.Rows[rowIndex];
            var mechanic = row[csv.MechanicColumn].Trim();
            var hasMitigation = csv.MitigationColumns.Any(column => column.Included && !string.IsNullOrWhiteSpace(row[column.Index]));
            if (mechanic.Length > 0) lastMechanic = mechanic;
            else if (hasMitigation) mechanic = lastMechanic;
            if (mechanic.Length == 0) continue;
            double? csvTime = null;
            if (csv.TimeColumn >= 0 && CsvImporter.TryParseTime(row[csv.TimeColumn], out var parsed)) csvTime = parsed;
            if (csvTime is not null) lastTime = csvTime;
            else if (hasMitigation) csvTime = lastTime;
            var phase = csv.PhaseColumn >= 0 ? row[csv.PhaseColumn].Trim() : string.Empty;
            if (phase.Length > 0) lastPhase = phase;
            else if (hasMitigation) phase = lastPhase;
            var candidates = recording.Events.Where(item => item.Included).Select(item =>
            {
                var nameScore = CsvImporter.NameSimilarity(mechanic, item.ActionName);
                var timeScore = csvTime is null ? 0.5 : Math.Max(0, 1 - Math.Abs(csvTime.Value - item.TimeSeconds) / 120d);
                var phaseName = recording.Phases.ElementAtOrDefault(item.PhaseIndex)?.Name ?? string.Empty;
                var phaseScore = phase.Length == 0 ? 0.5 : CsvImporter.NameSimilarity(phase, phaseName);
                return new { Item = item, Score = nameScore * 0.75 + timeScore * 0.2 + phaseScore * 0.05 };
            }).OrderByDescending(candidate => candidate.Score).FirstOrDefault();
            matches.Add(new CsvMatch
            {
                CsvRowIndex = rowIndex,
                CsvTimeSeconds = csvTime,
                CsvMechanic = mechanic,
                CsvPhase = phase,
                EventId = candidates?.Score >= 0.35 ? candidates.Item.Id : null,
                Confidence = candidates?.Score ?? 0,
                Applied = candidates?.Score >= 0.55,
            });
        }
        status = $"Matched {matches.Count(match => match.EventId is not null)} of {matches.Count} mechanic rows; review low-confidence rows.";
    }

    private void ApplyMatches()
    {
        if (csv is null) return;
        foreach (var item in recording.Events) item.Assignments.Clear();
        var added = 0;
        foreach (var match in matches.Where(match => match.Applied && match.EventId is not null))
        {
            var item = recording.Events.FirstOrDefault(item => item.Id == match.EventId);
            if (item is null) continue;
            var row = csv.Rows[match.CsvRowIndex];
            foreach (var column in csv.MitigationColumns.Where(column => column.Included))
            {
                var skill = row[column.Index].Trim();
                if (skill.Length == 0) continue;
                item.Assignments.Add(new MitigationAssignment { Skill = skill, TargetJob = column.TargetJob, TargetRole = column.TargetRole, SourceColumn = column.Header });
                added++;
            }
        }
        status = $"Applied {added} mitigation assignments to the recorded timeline.";
    }

    private string CsvMitigationSummary(int rowIndex)
    {
        if (csv is null) return string.Empty;
        var row = csv.Rows[rowIndex];
        return string.Join("; ", csv.MitigationColumns.Where(column => column.Included && !string.IsNullOrWhiteSpace(row[column.Index]))
            .Select(column => $"{column.Header}: {row[column.Index].Trim()}"));
    }

    private void DrawExport()
    {
        var assignedEvents = recording.Events.Count(item => item.Assignments.Count > 0 || !string.IsNullOrWhiteSpace(item.ManualMitigation));
        var phaseAnchors = recording.Phases.Count(phase => phase.AnchorEventId is not null);
        var mechanicAnchors = recording.Events.Count(item => item.UseAsSyncAnchor &&
            recording.Phases.All(phase => phase.AnchorEventId != item.Id));
        var timelineEvents = recording.Events.Count(item => item.Included);
        ImGui.TextWrapped($"The exported plan contains {recording.Phases.Count} phases, {timelineEvents} encounter-timeline entries, {phaseAnchors} phase anchors, {mechanicAnchors} one-shot mechanic anchors, and mitigation reminders on {assignedEvents} recorded mechanics. Empty mitigation rows stay in the encounter timeline but do not create mitigation alerts in MitPlan.");
        if (ImGui.Button("Copy MitPlan JSON to clipboard"))
        {
            ImGui.SetClipboardText(MitPlanExporter.BuildJson(recording));
            status = "MitPlan JSON copied to clipboard.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Send reviewed plan to MitPlan"))
        {
            try
            {
                var accepted = pluginInterface.GetIpcSubscriber<string, bool>("MitPlan.ImportFightJson").InvokeFunc(MitPlanExporter.BuildJson(recording));
                status = accepted ? "MitPlan imported the reviewed fight." : "MitPlan rejected the import.";
            }
            catch (Exception exception)
            {
                status = $"MitPlan IPC unavailable: {exception.Message}";
            }
        }
        if (ImGui.Button("Save recorder JSON..."))
            fileDialogs.SaveFileDialog("Save recording", ".json", $"{recording.FightName}.json", ".json", OnRecordingSave);
    }

    private void OnRecordingSave(bool success, string path)
    {
        if (!success) return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(recording, new JsonSerializerOptions { WriteIndented = true }));
            status = $"Saved recording to {path}.";
        }
        catch (Exception exception) { status = $"Save failed: {exception.Message}"; }
    }

    private void SaveLatestRecording()
    {
        try
        {
            var directory = pluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "latest-recording.json"), JsonSerializer.Serialize(recording, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void LoadLatestRecording()
    {
        try
        {
            var path = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "latest-recording.json");
            if (!File.Exists(path))
                return;
            var loaded = JsonSerializer.Deserialize<RecordingFile>(File.ReadAllText(path));
            if (loaded is null)
                return;
            loaded.Phases ??= [];
            loaded.Events ??= [];
            foreach (var item in loaded.Events)
            {
                item.Assignments ??= [];
                item.ManualTargetJob ??= "Any Job";
                item.ManualTargetRole ??= "Any Role";
            }
            recording = loaded;
            status = $"Loaded the latest recording: {recording.Events.Count} events.";
        }
        catch { }
    }

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);

    private void RemovePhase(int index)
    {
        if (index <= 0 || index >= recording.Phases.Count)
            return;
        recording.Phases.RemoveAt(index);
        foreach (var item in recording.Events)
        {
            if (item.PhaseIndex == index) item.PhaseIndex = index - 1;
            else if (item.PhaseIndex > index) item.PhaseIndex--;
        }
        status = "Phase candidate removed; its events were moved to the preceding phase.";
    }

    private static RecordingFile NewRecording() => new() { RecordedAtUtc = DateTime.UtcNow };

    private static string FormatTime(double seconds)
    {
        var minutes = (int)(seconds / 60);
        var remainder = seconds - minutes * 60;
        return $"{minutes}:{remainder:00.0}";
    }
}
