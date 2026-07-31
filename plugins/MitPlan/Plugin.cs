using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MitPlan;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mitplan";

    internal static readonly string[] Jobs =
    [
        "AST", "BLM", "BLU", "BRD", "DNC", "DRG", "DRK", "GNB", "MCH", "MNK", "NIN", "PCT",
        "PLD", "RDM", "RPR", "SAM", "SCH", "SGE", "SMN", "VPR", "WAR", "WHM"
    ];

    internal static readonly string[] Roles =
    [
        "MT", "OT", "Pure Healer", "Shield Healer", "Melee 1 (M1) (D1)",
        "Melee 2 (M2) (D2)", "Phys Ranged (R1) (D3)", "Caster (R2) (D4)"
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly ActionEffectWatcher actionEffectWatcher;
    private Configuration configuration;

    private DateTime? pullStartedAt;
    private bool wasInCombat;
    private bool mainWindowOpen = true;
    private string newFightName = string.Empty;
    private string newFightCategory = "Custom";
    private string entryTime = string.Empty;
    private string entrySkill = string.Empty;
    private string entryNote = string.Empty;
    private string entryTargetJob = "Any Job";
    private string entryTargetRole = "Any Role";
    private string? editingEntryId;
    private string editorStatus = string.Empty;
    private readonly HashSet<(nint Address, uint ActionId)> activeCasts = [];
    private readonly HashSet<string> firedSyncTriggers = [];
    private readonly Dictionary<string, DateTime> lastTriggerTimes = [];
    private readonly HashSet<(nint Address, uint StatusId)> activeStatuses = [];
    private readonly HashSet<uint> seenActorDataIds = [];
    private readonly HashSet<string> firedStateTransitions = [];
    private string currentPhase = string.Empty;
    private string lastSyncStatus = string.Empty;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        IGameInteropProvider gameInteropProvider,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        actionEffectWatcher = new ActionEffectWatcher(gameInteropProvider);
        this.commandManager = commandManager;

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Migrate();
        Save();

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open MitPlan. Arguments: start, stop, reset."
        });
        framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfig;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        commandManager.RemoveHandler(Command);
        actionEffectWatcher.Dispose();
    }

    private void OpenConfig() => mainWindowOpen = true;

    private FightPlan SelectedFight
    {
        get
        {
            var fight = configuration.Fights.FirstOrDefault(item => item.Id == configuration.SelectedFightId);
            if (fight is not null)
                return fight;
            configuration.SelectedFightId = configuration.Fights[0].Id;
            return configuration.Fights[0];
        }
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "start":
            case "reset":
                StartTimer(0);
                break;
            case "stop":
                pullStartedAt = null;
                break;
            default:
                mainWindowOpen = true;
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (configuration.AutoStartWithCombat && inCombat && !wasInCombat)
            StartTimer(0);
        else if (configuration.AutoStartWithCombat && !inCombat && wasInCombat)
            pullStartedAt = null;
        if (inCombat)
        {
            CheckActorStateTransitions();
            CheckTimelineSyncTriggers();
            CheckResolvedAbilities();
            CheckStatusTriggers();
        }
        else
        {
            activeCasts.Clear();
            firedSyncTriggers.Clear();
            lastTriggerTimes.Clear();
            activeStatuses.Clear();
            actionEffectWatcher.Clear();
            currentPhase = string.Empty;
            seenActorDataIds.Clear();
            firedStateTransitions.Clear();
        }
        wasInCombat = inCombat;
    }

    private void CheckActorStateTransitions()
    {
        var actors = objectTable.OfType<IBattleChara>().ToList();
        foreach (var actor in actors)
            seenActorDataIds.Add(actor.BaseId);

        foreach (var transition in SelectedFight.StateTransitions.Where(item =>
                     string.IsNullOrEmpty(item.RequiredPhase) || item.RequiredPhase == currentPhase))
        {
            var key = $"{transition.RequiredPhase}:{transition.ResultPhase}:{transition.TimelineSeconds}";
            if (firedStateTransitions.Contains(key) || transition.ActorDataIds.Count == 0)
                continue;
            var matching = transition.ActorDataIds
                .Select(id => actors.FirstOrDefault(actor => actor.BaseId == id))
                .ToList();
            bool ActorWasSeen(int index) => seenActorDataIds.Contains(transition.ActorDataIds[index]);
            var matched = transition.Condition switch
            {
                ActorStateCondition.Untargetable => matching[0] is { IsTargetable: false },
                ActorStateCondition.UntargetableBelowFullHp => matching[0] is { IsTargetable: false } actor && actor.CurrentHp < actor.MaxHp,
                ActorStateCondition.UntargetableAtOneHp => matching[0] is { IsTargetable: false, CurrentHp: <= 1 },
                ActorStateCondition.AtOneHpNotCasting => matching[0] is { CurrentHp: <= 1, IsCasting: false },
                ActorStateCondition.Targetable => matching[0] is { IsTargetable: true },
                ActorStateCondition.DeadOrDestroyed => ActorWasSeen(0) && (matching[0] is null || matching[0]!.IsDead),
                ActorStateCondition.AnyDeadOrDestroyed => matching.Select((actor, index) => ActorWasSeen(index) && (actor is null || actor.IsDead)).Any(value => value),
                ActorStateCondition.AllDeadOrDestroyed => matching.Select((actor, index) => ActorWasSeen(index) && (actor is null || actor.IsDead)).All(value => value),
                ActorStateCondition.AllUntargetableAtOneHp => matching.All(actor => actor is { IsTargetable: false, CurrentHp: <= 1 }),
                ActorStateCondition.AllUntargetableWithAnyBelowFullHp => matching.All(actor => actor is { IsTargetable: false }) && matching.Any(actor => actor!.CurrentHp < actor.MaxHp),
                _ => false,
            };
            if (!matched)
                continue;
            StartTimer(transition.TimelineSeconds);
            currentPhase = transition.ResultPhase;
            firedStateTransitions.Add(key);
            lastSyncStatus = $"Phase changed to {transition.ResultPhase}: {transition.Name}.";
        }
    }

    private void CheckTimelineSyncTriggers()
    {
        var current = new HashSet<(nint Address, uint ActionId)>();
        foreach (var battleChara in objectTable.OfType<IBattleChara>())
        {
            if (!battleChara.IsCasting || battleChara.CastActionId == 0)
                continue;
            var key = (battleChara.Address, battleChara.CastActionId);
            current.Add(key);
            if (activeCasts.Contains(key))
                continue;
            ProcessSyncEvent(TimelineSyncEventType.CastStart, battleChara.CastActionId);
        }
        activeCasts.Clear();
        activeCasts.UnionWith(current);
    }

    private void CheckResolvedAbilities()
    {
        while (actionEffectWatcher.TryDequeue(out var actionId))
            ProcessSyncEvent(TimelineSyncEventType.Ability, actionId);
    }

    private void CheckStatusTriggers()
    {
        var current = new HashSet<(nint Address, uint StatusId)>();
        foreach (var battleChara in objectTable.OfType<IBattleChara>())
        foreach (var status in battleChara.StatusList)
        {
            if (status.StatusId == 0)
                continue;
            var key = (battleChara.Address, status.StatusId);
            current.Add(key);
            if (!activeStatuses.Contains(key))
                ProcessSyncEvent(TimelineSyncEventType.StatusGain, status.StatusId);
        }
        activeStatuses.Clear();
        activeStatuses.UnionWith(current);
    }

    private void ProcessSyncEvent(TimelineSyncEventType eventType, uint eventId)
    {
        foreach (var trigger in SelectedFight.SyncTriggers.Where(item => item.EventType == eventType && item.EventId == eventId))
        {
            var key = $"{eventType}:{eventId:X}:{trigger.RequiredPhase}:{trigger.ResultPhase}:{trigger.TimelineSeconds}";
            if (!string.IsNullOrEmpty(trigger.RequiredPhase) && currentPhase != trigger.RequiredPhase)
                continue;
            if (firedSyncTriggers.Contains(key))
                continue;
            if (trigger.SuppressSeconds > 0 && lastTriggerTimes.TryGetValue(key, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromSeconds(trigger.SuppressSeconds))
                continue;

            StartTimer(trigger.TimelineSeconds);
            firedSyncTriggers.Add(key);
            lastTriggerTimes[key] = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(trigger.ResultPhase))
                currentPhase = trigger.ResultPhase;
            lastSyncStatus = $"Auto-synced {currentPhase} at {FormatTime(trigger.TimelineSeconds)} from {trigger.Name} ({eventType} 0x{eventId:X}).";
        }
    }

    private void StartTimer(int elapsedSeconds)
    {
        pullStartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        currentPhase = SelectedFight.Phases.LastOrDefault(phase => phase.StartSeconds <= elapsedSeconds)?.Key ??
                       SelectedFight.Phases.FirstOrDefault()?.Key ?? string.Empty;
    }

    private int ElapsedSeconds => pullStartedAt is null
        ? 0
        : Math.Max(0, (int)(DateTime.UtcNow - pullStartedAt.Value).TotalSeconds);

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    private void Draw()
    {
        if (mainWindowOpen)
            DrawMainWindow();
        if (configuration.ShowOverlay && (configuration.TestOverlay ||
            pullStartedAt is not null && condition[ConditionFlag.InCombat] && IsSelectedFightActive()))
            DrawOverlay();
    }

    private void DrawMainWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(820, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MitPlan##Main", ref mainWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("Choose a category and fight. Timed reminders alert automatically; untimed source assignments can be given a time with Edit.");
        DrawSectionHeader("Fight");
        DrawFightSelector();

        DrawSectionHeader("Player");
        var selectedJob = configuration.SelectedJob;
        var selectedRole = configuration.SelectedRole;
        if (DrawCombo("Job", Jobs, ref selectedJob) | DrawCombo("Role / slot", Roles, ref selectedRole))
        {
            configuration.SelectedJob = selectedJob;
            configuration.SelectedRole = selectedRole;
            Save();
        }

        DrawSectionHeader("Add or edit timeline reminder");
        DrawEntryEditor();

        DrawSectionHeader($"{SelectedFight.Name} timeline");
        if (configuration.SelectedRole is "MT" or "OT" && configuration.SelectedJob is "WAR" or "PLD" or "DRK" or "GNB")
        {
            if (ImGui.BeginTabBar("MitPlanTimelineTabs"))
            {
                if (ImGui.BeginTabItem("Party Mit"))
                {
                    DrawTimelineEditor(false);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Personal Mit"))
                {
                    DrawTimelineEditor(true);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        else
            DrawTimelineEditor(null);

        DrawSectionHeader("Timer and overlay");
        DrawTimerControls();
        ImGui.End();
    }

    private void DrawFightSelector()
    {
        var currentFight = SelectedFight;
        var categories = configuration.Fights.Select(fight => fight.Category).Distinct().OrderBy(value => value).ToArray();
        var selectedCategory = currentFight.Category;
        ImGui.SetNextItemWidth(220);
        if (DrawCombo("Category", categories, ref selectedCategory))
        {
            var first = configuration.Fights.First(fight => fight.Category == selectedCategory);
            configuration.SelectedFightId = first.Id;
            CancelEdit();
            pullStartedAt = null;
            Save();
            currentFight = first;
        }

        ImGui.SetNextItemWidth(420);
        if (ImGui.BeginCombo("Selected fight", currentFight.Name))
        {
            foreach (var fight in configuration.Fights.Where(item => item.Category == currentFight.Category).OrderBy(item => item.Name))
            {
                var selected = fight.Id == currentFight.Id;
                if (ImGui.Selectable(fight.Name, selected))
                {
                    configuration.SelectedFightId = fight.Id;
                    CancelEdit();
                    pullStartedAt = null;
                    activeCasts.Clear();
                    firedSyncTriggers.Clear();
                    lastTriggerTimes.Clear();
                    activeStatuses.Clear();
                    actionEffectWatcher.Clear();
                    currentPhase = string.Empty;
                    Save();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("New fight name", ref newFightName, 160);
        var customCategories = new[] { "Savage", "Extreme", "Ultimate", "Custom" };
        DrawCombo("New fight category", customCategories, ref newFightCategory);
        ImGui.SameLine();
        if (ImGui.Button("Add fight") && !string.IsNullOrWhiteSpace(newFightName))
        {
            var fight = new FightPlan { Name = newFightName.Trim(), Category = newFightCategory };
            configuration.Fights.Add(fight);
            configuration.SelectedFightId = fight.Id;
            newFightName = string.Empty;
            CancelEdit();
            Save();
        }

        var rename = currentFight.Name;
        ImGui.SetNextItemWidth(420);
        if (ImGui.InputText("Rename selected fight", ref rename, 160) && !string.IsNullOrWhiteSpace(rename))
        {
            currentFight.Name = rename;
            Save();
        }

        if (currentFight.IsBuiltIn)
            ImGui.TextWrapped("Default mitigation assignments are based on PF / Ikuya / NAUR mitigation strategies where available.");
        if (!string.IsNullOrWhiteSpace(currentFight.PresetStatus))
            ImGui.TextWrapped(currentFight.PresetStatus);
        if (!string.IsNullOrWhiteSpace(currentFight.SourceUrl))
            ImGui.TextDisabled($"Source: {currentFight.SourceUrl}");

        if (configuration.Fights.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete selected fight"))
                ImGui.OpenPopup("DeleteFightConfirm");
        }

        if (ImGui.BeginPopupModal("DeleteFightConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Delete '{currentFight.Name}' and all {currentFight.Timeline.Count} timeline entries?");
            if (ImGui.Button("Delete"))
            {
                configuration.Fights.Remove(currentFight);
                configuration.SelectedFightId = configuration.Fights[0].Id;
                CancelEdit();
                pullStartedAt = null;
                Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawEntryEditor()
    {
        var jobTargets = Jobs.Prepend("Any Job").ToArray();
        var roleTargets = Roles.Prepend("Any Role").ToArray();
        DrawCombo("Reminder job", jobTargets, ref entryTargetJob);
        DrawCombo("Reminder role / slot", roleTargets, ref entryTargetRole);
        ImGui.TextDisabled("Example: Any Job + MT applies to every main tank; WAR + MT applies only to a WAR main tank.");
        ImGui.SetNextItemWidth(100);
        ImGui.InputText("Time (MM:SS)", ref entryTime, 16);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Skill / instruction", ref entrySkill, 300);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Mechanic / note (optional)", ref entryNote, 300);

        var buttonText = editingEntryId is null ? "Add to timeline" : "Save changes";
        if (ImGui.Button(buttonText))
            SaveEntry();
        if (editingEntryId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit"))
                CancelEdit();
        }
        if (!string.IsNullOrWhiteSpace(editorStatus))
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), editorStatus);
    }

    private void SaveEntry()
    {
        if (!TryParseTime(entryTime, out var seconds))
        {
            editorStatus = "Enter a valid time such as 2:12 or 00:38.";
            return;
        }
        if (string.IsNullOrWhiteSpace(entrySkill))
        {
            editorStatus = "Enter a skill or instruction.";
            return;
        }

        var fight = SelectedFight;
        var existing = editingEntryId is null ? null : fight.Timeline.FirstOrDefault(item => item.Id == editingEntryId);
        if (existing is null)
        {
            fight.Timeline.Add(new TimelineItem
            {
                TimeSeconds = seconds,
                Skill = entrySkill.Trim(),
                Note = entryNote.Trim(),
                TargetJob = entryTargetJob,
                TargetRole = entryTargetRole
            });
        }
        else
        {
            existing.TimeSeconds = seconds;
            existing.Skill = entrySkill.Trim();
            existing.Note = entryNote.Trim();
            existing.TargetJob = entryTargetJob;
            existing.TargetRole = entryTargetRole;
        }

        fight.Timeline = fight.Timeline.OrderBy(item => item.TimeSeconds).ThenBy(item => item.Skill).ToList();
        Save();
        CancelEdit();
    }

    private void DrawTimelineEditor(bool? personalOnly)
    {
        var fight = SelectedFight;
        if (fight.Timeline.Count == 0)
        {
                ImGui.TextDisabled("No preset is available for this fight yet. Add reminders manually above.");
            return;
        }

        string? deleteId = null;
        var tableId = personalOnly == true ? "PersonalMitTimeline" : "FightTimeline";
        if (ImGui.BeginTable(tableId, 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, 260)))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Skill / instruction", ImGuiTableColumnFlags.WidthStretch, 0.45f);
            ImGui.TableSetupColumn("Mechanic / note", ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn("Edit", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Delete", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableHeadersRow();

            var orderedItems = ApplicableTimeline()
                .Where(item => personalOnly is null ||
                    (personalOnly.Value
                        ? IsTankPersonalMit(item)
                        : ResolveSkills(item.Skill).Any(skill => !IsResolvedTankPersonalSkill(skill))))
                .ToList();
            var phases = fight.Phases.OrderBy(phase => phase.StartSeconds).ToList();
            var nextPhase = 0;
            foreach (var item in orderedItems)
            {
                while (nextPhase < phases.Count && phases[nextPhase].StartSeconds <= item.TimeSeconds)
                {
                    DrawPhaseDivider(phases[nextPhase]);
                    nextPhase++;
                }
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(item.TimeSeconds < 0 ? "Untimed" : FormatTime(item.TimeSeconds));
                ImGui.TableNextColumn(); ImGui.Text(item.TargetJob == "Any Job" ? "Any" : item.TargetJob);
                ImGui.TableNextColumn(); ImGui.Text(item.TargetRole == "Any Role" ? "Any" : ShortRole(item.TargetRole));
                ImGui.TableNextColumn(); ImGui.TextWrapped(ResolveInstruction(item.Skill, personalOnly));
                ImGui.TableNextColumn(); ImGui.TextWrapped(item.Note);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Edit##{item.Id}"))
                    BeginEdit(item);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"X##{item.Id}"))
                    deleteId = item.Id;
            }
            ImGui.EndTable();
        }

        if (deleteId is not null)
        {
            fight.Timeline.RemoveAll(item => item.Id == deleteId);
            if (editingEntryId == deleteId)
                CancelEdit();
            Save();
        }
    }

    private static void DrawPhaseDivider(FightPhase phase)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(new Vector4(0.35f, 0.8f, 1f, 1f), FormatTime(phase.StartSeconds));
        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(new Vector4(0.35f, 0.8f, 1f, 1f), phase.Name.ToUpperInvariant());
    }

    private void BeginEdit(TimelineItem item)
    {
        editingEntryId = item.Id;
        entryTime = item.TimeSeconds < 0 ? string.Empty : FormatTime(item.TimeSeconds);
        entrySkill = item.Skill;
        entryNote = item.Note;
        entryTargetJob = item.TargetJob;
        entryTargetRole = item.TargetRole;
        editorStatus = string.Empty;
    }

    private void CancelEdit()
    {
        editingEntryId = null;
        entryTime = string.Empty;
        entrySkill = string.Empty;
        entryNote = string.Empty;
        entryTargetJob = "Any Job";
        entryTargetRole = "Any Role";
        editorStatus = string.Empty;
    }

    private void DrawTimerControls()
    {
        var autoStart = configuration.AutoStartWithCombat;
        if (ImGui.Checkbox("Automatically start at combat entry", ref autoStart))
        {
            configuration.AutoStartWithCombat = autoStart;
            Save();
        }
        var showOverlay = configuration.ShowOverlay;
        if (ImGui.Checkbox("Show alert overlay while timer is running", ref showOverlay))
        {
            configuration.ShowOverlay = showOverlay;
            Save();
        }
        var lead = configuration.LeadSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Show mitigation this many seconds early", ref lead))
        {
            configuration.LeadSeconds = Math.Clamp(lead, 0, 60);
            Save();
        }
        var displayModes = new[] { "Skill name", "Skill icon", "Name + icon" };
        var displayMode = (int)configuration.AlertDisplay;
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Overlay content", ref displayMode, displayModes, displayModes.Length))
        {
            configuration.AlertDisplay = (AlertDisplayMode)displayMode;
            Save();
        }
        var testOverlay = configuration.TestOverlay;
        if (ImGui.Checkbox("Test overlay (move it anywhere)", ref testOverlay))
        {
            configuration.TestOverlay = testOverlay;
            Save();
        }

        if (ImGui.Button(pullStartedAt is null ? "Start timeline" : "Reset timeline"))
            StartTimer(0);
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            pullStartedAt = null;
        ImGui.SameLine();
        if (ImGui.Button("-1 sec") && pullStartedAt is not null)
            pullStartedAt = pullStartedAt.Value.AddSeconds(1);
        ImGui.SameLine();
        if (ImGui.Button("+1 sec") && pullStartedAt is not null)
            pullStartedAt = pullStartedAt.Value.AddSeconds(-1);
        ImGui.Text($"Encounter time: {FormatTime(ElapsedSeconds)}");
        if (!string.IsNullOrWhiteSpace(lastSyncStatus))
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.45f, 1f), lastSyncStatus);

        if (SelectedFight.Phases.Count > 0)
        {
            ImGui.Text("Phase sync:");
            foreach (var phase in SelectedFight.Phases)
            {
                if (ImGui.Button($"{phase.Name}##phase-{phase.StartSeconds}"))
                    StartTimer(phase.StartSeconds);
                ImGui.SameLine();
            }
            ImGui.NewLine();
            ImGui.TextDisabled("Click as the named phase begins to remove timing drift from earlier phase pushes.");
        }
    }

    private void DrawOverlay()
    {
        var elapsed = ElapsedSeconds;
        var active = ApplicableTimeline()
            .Where(item => item.TimeSeconds - elapsed <= configuration.LeadSeconds &&
                           item.TimeSeconds - elapsed >= -configuration.KeepSeconds)
            .Take(5)
            .ToList();
        if (configuration.TestOverlay)
            active = [new TimelineItem { Skill = "Reprisal" }, new TimelineItem { Skill = "Shake It Off" }];
        if (active.Count == 0)
            return;

        ImGui.SetNextWindowSize(new Vector2(260, 0), ImGuiCond.FirstUseEver);
        var overlayOpen = true;
        if (ImGui.Begin("MitPlan##Overlay", ref overlayOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            foreach (var skill in active
                         .SelectMany(item => ResolveSkills(item.Skill))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                DrawSkillAlert(skill);
        }
        ImGui.End();

        if (!overlayOpen)
        {
            configuration.ShowOverlay = false;
            Save();
        }
    }

    private void DrawSkillAlert(string skill)
    {
        var showName = configuration.AlertDisplay != AlertDisplayMode.IconOnly;
        var showIcon = configuration.AlertDisplay != AlertDisplayMode.NameOnly;
        if (showName)
        {
            ImGui.Text($"Use: {skill}");
            if (showIcon)
                ImGui.SameLine();
        }
        if (showIcon && TryFindActionIcon(skill, out var iconId))
        {
            var texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId, false, true, null)).GetWrapOrEmpty();
            ImGui.Image(texture.Handle, new Vector2(36, 36));
        }
        else if (showIcon && !showName)
            ImGui.TextDisabled("?");
    }

    private bool TryFindActionIcon(string skill, out uint iconId)
    {
        var lookup = skill.Trim();
        if (lookup.Equals("Spreadlo", StringComparison.OrdinalIgnoreCase))
            lookup = "Adloquium";
        var actions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        var action = actions.FirstOrDefault(row => row.IsPlayerAction && !row.IsPvP &&
            row.Name.ToString().Equals(lookup, StringComparison.OrdinalIgnoreCase));
        if (action.Icon == 0)
            action = actions
                .Where(row => row.Icon != 0 && row.IsPlayerAction && !row.IsPvP && row.Name.ToString().Length >= 4 &&
                              lookup.Contains(row.Name.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.Name.ToString().Length)
                .FirstOrDefault();
        iconId = action.Icon;
        return iconId != 0;
    }

    private static IEnumerable<string> SplitSkills(string value) => value
        .Replace("â†’", "+").Replace("→", "+")
        .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private string ResolveSkillName(string instruction)
    {
        var cleaned = instruction.Trim().TrimStart('→').Trim();
        if (!cleaned.Contains("Party Mit", StringComparison.OrdinalIgnoreCase))
            return cleaned;

        return configuration.SelectedJob switch
        {
            "WAR" => "Shake It Off",
            "PLD" => "Divine Veil",
            "DRK" => "Dark Missionary",
            "GNB" => "Heart of Light",
            "BRD" => "Troubadour",
            "MCH" => "Tactician",
            "DNC" => "Shield Samba",
            "WHM" => "Temperance",
            "AST" => "Sun Sign",
            "SCH" => "Expedient",
            "SGE" => "Kerachole",
            "RDM" => "Magick Barrier",
            "BLM" or "SMN" or "PCT" => "Addle",
            "DRG" or "MNK" or "NIN" or "RPR" or "SAM" or "VPR" => "Feint",
            _ => "Party Mit",
        };
    }

    private IEnumerable<string> ResolveSkills(string instruction) =>
        SplitSkills(instruction).SelectMany(ResolveSkillNames);

    private IEnumerable<string> ResolveSkillNames(string instruction)
    {
        var cleaned = instruction.Trim();
        if (cleaned.Contains("Kitchen Sink", StringComparison.OrdinalIgnoreCase))
            return ["Rampart", TankMajorCooldown(), TankShortCooldown()];
        if (ContainsAny(cleaned, "90s", "90 sec", "90-second", "thrill", "bulwark", "dark mind", "camouflage", "camo"))
            return [TankNinetySecondCooldown()];
        if (ContainsAny(cleaned, "2min", "2 min", "2m", "120s", "120 sec", "30%", "big cd"))
            return [TankMajorCooldown()];
        if (ContainsAny(cleaned, "fast cd", "small cd", "short cd", "short", "25s", "25 sec"))
            return [TankShortCooldown()];
        if (ContainsAny(cleaned, "invuln", "holmgang", "hallowed ground", "living dead", "superbolide", "bolide"))
            return [TankInvulnerability()];
        if (ContainsAny(cleaned, "thrill of battle", "bulwark", "dark mind", "camouflage"))
            return [TankNinetySecondCooldown()];
        if (ContainsAny(cleaned, "vengeance", "damnation", "sentinel", "guardian", "shadow wall", "shadowed vigil", "nebula", "great nebula"))
            return [TankMajorCooldown()];
        if (ContainsAny(cleaned, "raw intuition", "bloodwhetting", "sheltron", "the blackest night", "tbn", "heart of stone", "heart of corundum", "hoc"))
            return [TankShortCooldown()];
        if (cleaned.Contains("Buddy Mit", StringComparison.OrdinalIgnoreCase))
            return [TankBuddyCooldown()];
        if (cleaned.Contains("Nascent", StringComparison.OrdinalIgnoreCase))
            return ["Nascent Flash"];
        if (cleaned.Contains("Oblation", StringComparison.OrdinalIgnoreCase))
            return ["Oblation"];
        if (cleaned.Contains("Intervention", StringComparison.OrdinalIgnoreCase))
            return ["Intervention"];
        if (cleaned.Contains("Equilibrium", StringComparison.OrdinalIgnoreCase))
            return ["Equilibrium"];
        if (cleaned.Contains("Aurora", StringComparison.OrdinalIgnoreCase))
            return ["Aurora"];
        if (cleaned.Contains("Rampart", StringComparison.OrdinalIgnoreCase))
            return ["Rampart"];
        return [ResolveSkillName(cleaned)];
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private string TankNinetySecondCooldown() => configuration.SelectedJob switch
    {
        "WAR" => "Thrill of Battle",
        "PLD" => "Bulwark",
        "DRK" => "Dark Mind",
        "GNB" => "Camouflage",
        _ => "90s Personal Mit",
    };

    private bool UsesLevelHundredTankActions() => SelectedFight.Id is "fru" or "dmu" or "m9s" or "m10s" or "m11s" or "m12s";

    private bool UsesEndwalkerTankActions() => SelectedFight.Id is "dsr" or "top" or "fru" or "dmu" or "m9s" or "m10s" or "m11s" or "m12s";

    private string TankMajorCooldown() => configuration.SelectedJob switch
    {
        "WAR" => UsesLevelHundredTankActions() ? "Damnation" : "Vengeance",
        "PLD" => UsesLevelHundredTankActions() ? "Guardian" : "Sentinel",
        "DRK" => UsesLevelHundredTankActions() ? "Shadowed Vigil" : "Shadow Wall",
        "GNB" => UsesLevelHundredTankActions() ? "Great Nebula" : "Nebula",
        _ => "2min Personal Mit",
    };

    private string TankShortCooldown() => configuration.SelectedJob switch
    {
        "WAR" => UsesEndwalkerTankActions() ? "Bloodwhetting" : "Raw Intuition",
        "PLD" => UsesEndwalkerTankActions() ? "Holy Sheltron" : "Sheltron",
        "DRK" => "The Blackest Night",
        "GNB" => UsesEndwalkerTankActions() ? "Heart of Corundum" : "Heart of Stone",
        _ => "Short Personal Mit",
    };

    private string TankInvulnerability() => configuration.SelectedJob switch
    {
        "WAR" => "Holmgang",
        "PLD" => "Hallowed Ground",
        "DRK" => "Living Dead",
        "GNB" => "Superbolide",
        _ => "Invulnerability",
    };

    private string TankBuddyCooldown() => configuration.SelectedJob switch
    {
        "WAR" => "Nascent Flash",
        "PLD" => "Intervention",
        "DRK" => "The Blackest Night",
        "GNB" => TankShortCooldown(),
        _ => "Buddy Mit",
    };

    private string ResolveInstruction(string instruction) => string.Join(" + ",
        ResolveSkills(instruction)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private string ResolveInstruction(string instruction, bool? personalOnly)
    {
        var skills = ResolveSkills(instruction);
        if (personalOnly is not null)
            skills = skills.Where(skill => IsResolvedTankPersonalSkill(skill) == personalOnly.Value);
        return string.Join(" + ", skills.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private bool IsTankPersonalMit(TimelineItem item)
    {
        if (item.TargetRole is not ("MT" or "OT"))
            return false;
        return ResolveSkills(item.Skill).Any(IsResolvedTankPersonalSkill);
    }

    private bool IsResolvedTankPersonalSkill(string skill) => ContainsAny(skill,
        "Rampart", TankNinetySecondCooldown(), TankMajorCooldown(), TankShortCooldown(), TankInvulnerability(),
        TankBuddyCooldown(), "Nascent Flash", "Oblation", "Intervention", "Equilibrium", "Aurora");

    private IEnumerable<TimelineItem> ApplicableTimeline() =>
        SelectedFight.Timeline
            .Where(item =>
                item.TimeSeconds >= 0 &&
                (item.TargetJob == "Any Job" || item.TargetJob == configuration.SelectedJob) &&
                (item.TargetRole == "Any Role" || item.TargetRole == configuration.SelectedRole))
            .OrderBy(item => item.TimeSeconds);

    private unsafe bool IsSelectedFightActive()
    {
        if (SelectedFight.ContentFinderConditionId == 0)
            return true;
        var gameMain = GameMain.Instance();
        return gameMain != null && gameMain->CurrentContentFinderConditionId == SelectedFight.ContentFinderConditionId;
    }

    private static bool DrawCombo(string label, IReadOnlyList<string> values, ref string selected)
    {
        var changed = false;
        ImGui.SetNextItemWidth(280);
        if (ImGui.BeginCombo(label, selected))
        {
            foreach (var value in values)
            {
                var isSelected = value == selected;
                if (ImGui.Selectable(value, isSelected))
                {
                    selected = value;
                    changed = true;
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private static bool TryParseTime(string value, out int seconds)
    {
        seconds = 0;
        var parts = value.Trim().Split(':');
        if (parts.Length == 1 && int.TryParse(parts[0], out var rawSeconds) && rawSeconds >= 0)
        {
            seconds = rawSeconds;
            return true;
        }
        if (parts.Length != 2 || !int.TryParse(parts[0], out var minutes) ||
            !int.TryParse(parts[1], out var remainder) || minutes < 0 || remainder is < 0 or > 59)
            return false;
        seconds = minutes * 60 + remainder;
        return true;
    }

    private static void DrawSectionHeader(string text)
    {
        ImGui.Separator();
        ImGui.Text(text);
        ImGui.Separator();
    }

    private static string ShortRole(string role)
    {
        if (role.Contains("D1")) return "D1";
        if (role.Contains("D2")) return "D2";
        if (role.Contains("D3")) return "D3";
        if (role.Contains("D4")) return "D4";
        return role;
    }

    private static string FormatTime(int totalSeconds) => $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    private static string SingleLine(string text) => text.Replace("\r", string.Empty).Replace("\n", " → ");
}
