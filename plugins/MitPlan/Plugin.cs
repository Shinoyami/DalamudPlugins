using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

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
    private readonly IPluginLog log;
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly GoogleSheetLoader loader;
    private readonly object dataLock = new();

    private Configuration configuration;
    private IReadOnlyList<PhasePlan> phases = [];
    private IReadOnlyList<MitigationReminder> reminders = [];
    private CancellationTokenSource? loadCancellation;
    private DateTime? pullStartedAt;
    private bool wasInCombat;
    private bool mainWindowOpen = true;
    private string loadStatus = "Not loaded";
    private bool loading;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICondition condition,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.condition = condition;
        this.commandManager = commandManager;
        this.log = log;

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        loader = new GoogleSheetLoader(httpClient);

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open MitPlan. Arguments: start, stop, reset, reload."
        });
        framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        _ = ReloadAsync();
    }

    public void Dispose()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        commandManager.RemoveHandler(Command);
        httpClient.Dispose();
    }

    private void OpenConfig() => mainWindowOpen = true;

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
            case "reload":
                _ = ReloadAsync();
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

        wasInCombat = inCombat;
    }

    private void StartTimer(int elapsedSeconds) =>
        pullStartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));

    private int ElapsedSeconds => pullStartedAt is null
        ? 0
        : Math.Max(0, (int)(DateTime.UtcNow - pullStartedAt.Value).TotalSeconds);

    private async Task ReloadAsync()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        loading = true;
        loadStatus = "Downloading P1-P5...";

        try
        {
            var loadedPhases = await loader.LoadAsync(configuration.SheetUrl, loadCancellation.Token);
            var loadedReminders = BuildReminders(loadedPhases);
            lock (dataLock)
            {
                phases = loadedPhases;
                reminders = loadedReminders;
            }

            loadStatus = $"Loaded {loadedPhases.Sum(phase => phase.Entries.Count)} timeline rows; " +
                         $"{loadedReminders.Count} assignments for {configuration.SelectedJob}/{ShortRole(configuration.SelectedRole)}.";
        }
        catch (OperationCanceledException)
        {
            loadStatus = "Reload cancelled.";
        }
        catch (Exception ex)
        {
            loadStatus = $"Load failed: {ex.Message}";
            log.Error(ex, "Failed to load the mitigation sheet.");
        }
        finally
        {
            loading = false;
        }
    }

    private IReadOnlyList<MitigationReminder> BuildReminders(IReadOnlyList<PhasePlan> loadedPhases)
    {
        var assignmentKey = AssignmentKey(configuration.SelectedJob, configuration.SelectedRole);
        return loadedPhases
            .SelectMany(phase => phase.Entries)
            .Where(entry => entry.Assignments.TryGetValue(assignmentKey, out var text) && !string.IsNullOrWhiteSpace(text))
            .Select(entry => new MitigationReminder(
                entry.Phase,
                entry.Mechanic,
                entry.GlobalSeconds,
                entry.Assignments[assignmentKey]))
            .OrderBy(reminder => reminder.TimeSeconds)
            .ToList();
    }

    private void Refilter()
    {
        lock (dataLock)
            reminders = BuildReminders(phases);
        Save();
        loadStatus = $"Filtered {reminders.Count} assignments for {configuration.SelectedJob}/{ShortRole(configuration.SelectedRole)}.";
    }

    private static string AssignmentKey(string job, string role)
    {
        if (role.Contains("Healer", StringComparison.OrdinalIgnoreCase) && job is "WHM" or "AST" or "SCH" or "SGE")
            return job;
        if (role == "MT" || role == "OT")
            return role;
        if (role.Contains("D1", StringComparison.OrdinalIgnoreCase))
            return "D1";
        if (role.Contains("D2", StringComparison.OrdinalIgnoreCase))
            return "D2";
        if (role.Contains("D3", StringComparison.OrdinalIgnoreCase))
            return "D3";
        if (role.Contains("D4", StringComparison.OrdinalIgnoreCase))
            return "D4";
        return job;
    }

    private static string ShortRole(string role)
    {
        if (role.Contains("D1")) return "D1";
        if (role.Contains("D2")) return "D2";
        if (role.Contains("D3")) return "D3";
        if (role.Contains("D4")) return "D4";
        return role;
    }

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    private void Draw()
    {
        if (mainWindowOpen)
            DrawMainWindow();
        if (configuration.ShowOverlay && pullStartedAt is not null)
            DrawOverlay();
    }

    private void DrawMainWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(760, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MitPlan##Main", ref mainWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("Load a public Google Sheets mitigation plan, choose your job and party slot, then start or sync the encounter timer.");
        ImGui.Separator();

        var sheetUrl = configuration.SheetUrl;
        ImGui.SetNextItemWidth(-110);
        if (ImGui.InputText("##SheetUrl", ref sheetUrl, 512))
            configuration.SheetUrl = sheetUrl;
        ImGui.SameLine();
        if (ImGui.Button(loading ? "Loading..." : "Reload Sheet") && !loading)
        {
            Save();
            _ = ReloadAsync();
        }
        ImGui.TextWrapped(loadStatus);

        DrawSectionHeader("Player assignment");
        var selectedJob = configuration.SelectedJob;
        var selectedRole = configuration.SelectedRole;
        if (DrawCombo("Job", Jobs, ref selectedJob) |
            DrawCombo("Role / slot", Roles, ref selectedRole))
        {
            configuration.SelectedJob = selectedJob;
            configuration.SelectedRole = selectedRole;
            Refilter();
        }

        var compatibility = CompatibilityWarning(configuration.SelectedJob, configuration.SelectedRole);
        if (compatibility is not null)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), compatibility);

        DrawSectionHeader("Timer and alerts");
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
        if (ImGui.SliderInt("Advance warning", ref lead, 1, 20, "%d seconds"))
        {
            configuration.LeadSeconds = lead;
            Save();
        }

        var keep = configuration.KeepSeconds;
        if (ImGui.SliderInt("Keep after due time", ref keep, 0, 15, "%d seconds"))
        {
            configuration.KeepSeconds = keep;
            Save();
        }

        if (ImGui.Button(pullStartedAt is null ? "Start / reset pull" : "Reset pull"))
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

        IReadOnlyList<PhasePlan> phaseSnapshot;
        IReadOnlyList<MitigationReminder> reminderSnapshot;
        lock (dataLock)
        {
            phaseSnapshot = phases;
            reminderSnapshot = reminders;
        }

        if (phaseSnapshot.Count > 0)
        {
            ImGui.Text("Sync phase start now:");
            foreach (var phase in phaseSnapshot)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"{phase.Name.Split('|')[0].Trim()}##sync-{phase.Name}"))
                    StartTimer(phase.StartSeconds);
            }
        }

        DrawSectionHeader("Upcoming assignments");
        DrawUpcomingTable(reminderSnapshot, ElapsedSeconds, 12);
        ImGui.End();
    }

    private void DrawOverlay()
    {
        var elapsed = ElapsedSeconds;
        IReadOnlyList<MitigationReminder> snapshot;
        lock (dataLock)
            snapshot = reminders;

        var active = snapshot
            .Where(reminder => reminder.TimeSeconds - elapsed <= configuration.LeadSeconds &&
                               reminder.TimeSeconds - elapsed >= -configuration.KeepSeconds)
            .Take(4)
            .ToList();
        var next = snapshot.FirstOrDefault(reminder => reminder.TimeSeconds - elapsed > configuration.LeadSeconds);

        ImGui.SetNextWindowSize(new Vector2(480, 0), ImGuiCond.FirstUseEver);
        var overlayOpen = true;
        if (ImGui.Begin("MitPlan Alerts##Overlay", ref overlayOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.Text($"{configuration.SelectedJob} / {ShortRole(configuration.SelectedRole)}    {FormatTime(elapsed)}");
            ImGui.Separator();

            if (active.Count == 0)
            {
                ImGui.TextDisabled("No mitigation due now.");
            }
            else
            {
                foreach (var reminder in active)
                {
                    var delta = reminder.TimeSeconds - elapsed;
                    var color = delta <= 0
                        ? new Vector4(1f, 0.25f, 0.2f, 1f)
                        : new Vector4(1f, 0.85f, 0.15f, 1f);
                    ImGui.TextColored(color, $"{(delta >= 0 ? $"IN {delta}s" : "NOW")} — {reminder.Assignment}");
                    ImGui.TextWrapped($"{FormatTime(reminder.TimeSeconds)}  {reminder.Mechanic}  ({reminder.Phase.Split('|')[0].Trim()})");
                }
            }

            if (next is not null)
            {
                ImGui.Separator();
                ImGui.TextDisabled($"Next: {FormatTime(next.TimeSeconds)} {next.Mechanic} — {SingleLine(next.Assignment)}");
            }
        }
        ImGui.End();

        if (!overlayOpen)
        {
            configuration.ShowOverlay = false;
            Save();
        }
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

    private static void DrawSectionHeader(string text)
    {
        ImGui.Separator();
        ImGui.Text(text);
        ImGui.Separator();
    }

    private static void DrawUpcomingTable(IReadOnlyList<MitigationReminder> reminders, int elapsed, int count)
    {
        var upcoming = reminders.Where(reminder => reminder.TimeSeconds >= elapsed - 2).Take(count).ToList();
        if (ImGui.BeginTable("Upcoming", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Phase", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthStretch, 0.45f);
            ImGui.TableSetupColumn("Your assignment", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            ImGui.TableHeadersRow();
            foreach (var reminder in upcoming)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(FormatTime(reminder.TimeSeconds));
                ImGui.TableNextColumn(); ImGui.Text(reminder.Phase.Split('|')[0].Trim());
                ImGui.TableNextColumn(); ImGui.TextWrapped(reminder.Mechanic);
                ImGui.TableNextColumn(); ImGui.TextWrapped(reminder.Assignment);
            }
            ImGui.EndTable();
        }
    }

    private static string? CompatibilityWarning(string job, string role)
    {
        var healer = job is "WHM" or "AST" or "SCH" or "SGE";
        var tank = job is "PLD" or "WAR" or "DRK" or "GNB";
        var physRanged = job is "BRD" or "MCH" or "DNC";
        var caster = job is "BLM" or "SMN" or "RDM" or "PCT" or "BLU";
        var melee = job is "DRG" or "MNK" or "NIN" or "RPR" or "SAM" or "VPR";

        var compatible = role switch
        {
            "MT" or "OT" => tank,
            "Pure Healer" => job is "WHM" or "AST",
            "Shield Healer" => job is "SCH" or "SGE",
            _ when role.Contains("D1") || role.Contains("D2") => melee,
            _ when role.Contains("D3") => physRanged,
            _ when role.Contains("D4") => caster,
            _ => healer
        };
        return compatible ? null : "The selected job is unusual for this party slot; the slot's assignments will still be shown.";
    }

    private static string FormatTime(int totalSeconds) => $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    private static string SingleLine(string text) => text.Replace("\r", string.Empty).Replace("\n", " → ");
}
