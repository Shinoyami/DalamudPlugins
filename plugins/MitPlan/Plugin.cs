using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MitPlan;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mitplan";
    private const int DuplicateSequenceWindowSeconds = 15;

    internal static readonly string[] Jobs =
    [
        "AST", "BLM", "BLU", "BRD", "DNC", "DRG", "DRK", "GNB", "MCH", "MNK", "NIN", "PCT",
        "PLD", "RDM", "RPR", "SAM", "SCH", "SGE", "SMN", "VPR", "WAR", "WHM"
    ];

    internal static readonly string[] Roles =
    [
        "MT", "OT", "Pure Healer (H1)", "Shield Healer (H2)", "Melee 1 (M1) (D1)",
        "Melee 2 (M2) (D2)", "Phys Ranged (R1) (D3)", "Caster (R2) (D4)"
    ];

    private static readonly string[] TankJobs = ["WAR", "PLD", "DRK", "GNB"];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly ActionEffectWatcher actionEffectWatcher;
    private readonly ICallGateProvider<string, bool> importFightProvider;
    private Configuration configuration;

    private DateTime? encounterTimelineStartedAt;
    private bool debugTimelineRunning;
    private bool wasInCombat;
    private bool mainWindowOpen;
    private bool encounterSetupWindowOpen;
    private uint lastContentFinderConditionId;
    private string encounterSetupFightId = string.Empty;
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
    private readonly HashSet<string> firedEncounterTimelineSyncs = [];
    private readonly HashSet<string> syncedPhaseAnchors = [];
    private readonly Dictionary<string, DateTime> lastTriggerTimes = [];
    private readonly Dictionary<string, int> observedSyncOccurrences = [];
    private readonly HashSet<(nint Address, uint StatusId)> activeStatuses = [];
    private readonly HashSet<uint> seenActorDataIds = [];
    private readonly Dictionary<uint, DateTime> missingActorSince = [];
    private readonly HashSet<string> firedStateTransitions = [];
    private readonly HashSet<string> playedAudioAlerts = [];
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

        importFightProvider = pluginInterface.GetIpcProvider<string, bool>("MitPlan.ImportFightJson");
        importFightProvider.RegisterFunc(ImportFightJson);

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open MitPlan. Arguments: p (player setup), tl (toggle fight timeline)."
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
        importFightProvider.UnregisterFunc();
        actionEffectWatcher.Dispose();
    }

    private void OpenConfig() => mainWindowOpen = true;

    private bool ImportFightJson(string json)
    {
        try
        {
            var imported = JsonSerializer.Deserialize<FightPlan>(json);
            if (imported is null || string.IsNullOrWhiteSpace(imported.Id) || string.IsNullOrWhiteSpace(imported.Name))
                return false;
            imported.IsBuiltIn = false;
            imported.PresetRevision = 0;
            imported.Phases ??= [];
            imported.SyncTriggers ??= [];
            imported.StateTransitions ??= [];
            imported.Timeline ??= [];
            imported.EncounterTimeline ??= [];
            if (imported.Phases.Count == 0)
                imported.Phases.Add(new FightPhase { Name = "P1", Key = "P1", StartSeconds = 0 });
            EncounterTimelineLinker.LinkFight(imported);
            var existing = configuration.Fights.FirstOrDefault(fight => fight.Id == imported.Id && !fight.IsBuiltIn);
            if (existing is not null)
                configuration.Fights.Remove(existing);
            else if (configuration.Fights.Any(fight => fight.Id == imported.Id))
                imported.Id = $"{imported.Id}-{Guid.NewGuid():N}";
            configuration.Fights.Add(imported);
            configuration.SelectedFightId = imported.Id;
            StopTimer();
            currentPhase = string.Empty;
            Save();
            mainWindowOpen = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

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
            case "p":
                RequestEncounterSetupPopup();
                break;
            case "tl":
                configuration.EnableFightTimeline = !configuration.EnableFightTimeline;
                Save();
                break;
            default:
                mainWindowOpen = true;
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        CheckForEncounterEntry();
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat && IsSelectedFightActive())
        {
            StopTimer();
            ClearEncounterRuntimeState();
            StartTimer(0);
            if (!string.IsNullOrEmpty(currentPhase))
                syncedPhaseAnchors.Add(currentPhase);
            lastSyncStatus = "P1 encounter clock synchronized to combat start.";
        }
        else if (!inCombat && wasInCombat && !debugTimelineRunning)
            StopTimer();
        if (debugTimelineRunning)
        {
            UpdateDebugTimelinePhase();
        }
        else if (inCombat)
        {
            AdvanceDmuP2AtTimelineBoundary();
            CheckActorStateTransitions();
            CheckTimelineSyncTriggers();
            CheckResolvedAbilities();
            CheckStatusTriggers();
        }
        else
        {
            activeCasts.Clear();
            firedSyncTriggers.Clear();
            firedEncounterTimelineSyncs.Clear();
            syncedPhaseAnchors.Clear();
            lastTriggerTimes.Clear();
            observedSyncOccurrences.Clear();
            activeStatuses.Clear();
            actionEffectWatcher.Clear();
            currentPhase = string.Empty;
            seenActorDataIds.Clear();
            missingActorSince.Clear();
            firedStateTransitions.Clear();
            playedAudioAlerts.Clear();
        }
        wasInCombat = inCombat;
    }

    private void StartDebugTimeline()
    {
        ClearEncounterRuntimeState();
        StartTimer(0);
        debugTimelineRunning = true;
        lastSyncStatus = $"Debugging {SelectedFight.Name} from 00:00.";
    }

    private void StopDebugTimeline()
    {
        StopTimer();
        ClearEncounterRuntimeState();
        lastSyncStatus = "Debug timeline stopped.";
    }

    private void UpdateDebugTimelinePhase()
    {
        var elapsed = EncounterTimelineElapsedSeconds;
        var phase = SelectedFight.Phases.LastOrDefault(item => item.StartSeconds <= elapsed) ??
                    SelectedFight.Phases.FirstOrDefault();
        if (phase is not null && phase.Key != currentPhase)
        {
            currentPhase = phase.Key;
            lastSyncStatus = $"Debug timeline advanced to {currentPhase}.";
        }
    }

    private void ClearEncounterRuntimeState()
    {
        activeCasts.Clear();
        firedSyncTriggers.Clear();
        firedEncounterTimelineSyncs.Clear();
        syncedPhaseAnchors.Clear();
        lastTriggerTimes.Clear();
        observedSyncOccurrences.Clear();
        activeStatuses.Clear();
        actionEffectWatcher.Clear();
        currentPhase = string.Empty;
        seenActorDataIds.Clear();
        missingActorSince.Clear();
        firedStateTransitions.Clear();
        playedAudioAlerts.Clear();
    }

    private void AdvanceDmuP2AtTimelineBoundary()
    {
        if (SelectedFight.Id != "dmu" || currentPhase != "P1 Kefka")
            return;
        var p2 = SelectedFight.Phases.FirstOrDefault(phase => phase.Key == "P2 Forsaken Kefka");
        if (p2 is null || EncounterTimelineElapsedSeconds < p2.StartSeconds)
            return;
        currentPhase = p2.Key;
        lastSyncStatus = "DMU P2 timeline activated at the P1 untargetable boundary; waiting for its precision anchor.";
    }

    private void CheckActorStateTransitions()
    {
        var actors = objectTable.OfType<IBattleChara>().ToList();
        foreach (var actor in actors)
            seenActorDataIds.Add(actor.BaseId);
        var now = DateTime.UtcNow;
        foreach (var actorId in SelectedFight.StateTransitions.SelectMany(item => item.ActorDataIds).Distinct())
        {
            if (actors.Any(actor => actor.BaseId == actorId))
                missingActorSince.Remove(actorId);
            else if (seenActorDataIds.Contains(actorId))
                missingActorSince.TryAdd(actorId, now);
        }

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
            bool ActorHasDisappeared(int index) =>
                ActorWasSeen(index) &&
                missingActorSince.TryGetValue(transition.ActorDataIds[index], out var missingSince) &&
                now - missingSince >= TimeSpan.FromMilliseconds(500);
            var matched = transition.Condition switch
            {
                ActorStateCondition.Untargetable => ActorHasDisappeared(0) ||
                    matching[0] is { IsTargetable: false },
                ActorStateCondition.UntargetableBelowFullHp => ActorWasSeen(0) &&
                    (ActorHasDisappeared(0) || matching[0] is { IsTargetable: false } actor && actor.CurrentHp < actor.MaxHp),
                ActorStateCondition.UntargetableAtOneHp => ActorWasSeen(0) &&
                    (ActorHasDisappeared(0) || matching[0] is { IsTargetable: false, CurrentHp: <= 1 }),
                ActorStateCondition.AtOneHpNotCasting => matching[0] is { CurrentHp: <= 1, IsCasting: false },
                ActorStateCondition.Targetable => matching[0] is { IsTargetable: true },
                ActorStateCondition.DeadOrDestroyed => ActorHasDisappeared(0) || matching[0] is { IsDead: true },
                ActorStateCondition.AnyDeadOrDestroyed => matching.Select((actor, index) =>
                    ActorHasDisappeared(index) || actor is { IsDead: true }).Any(value => value),
                ActorStateCondition.AllDeadOrDestroyed => matching.Select((actor, index) =>
                    ActorHasDisappeared(index) || actor is { IsDead: true }).All(value => value),
                ActorStateCondition.AllUntargetableAtOneHp => matching.Select((actor, index) =>
                    ActorHasDisappeared(index) || actor is { IsTargetable: false, CurrentHp: <= 1 }).All(value => value),
                ActorStateCondition.AllUntargetableWithAnyBelowFullHp =>
                    matching.Select((actor, index) => ActorHasDisappeared(index) || actor is { IsTargetable: false }).All(value => value) &&
                    matching.Select((actor, index) => ActorHasDisappeared(index) || actor is { } present && present.CurrentHp < present.MaxHp).Any(value => value),
                _ => false,
            };
            if (!matched)
                continue;
            currentPhase = transition.ResultPhase;
            firedStateTransitions.Add(key);
            lastSyncStatus = $"Detected {transition.ResultPhase}; waiting for its first phase anchor.";
        }
    }

    private void CheckTimelineSyncTriggers()
    {
        var current = new HashSet<(nint Address, uint ActionId)>();
        foreach (var battleChara in objectTable.OfType<IBattleChara>())
        {
            try
            {
                if (!battleChara.IsCasting || battleChara.CastActionId == 0)
                    continue;
                var key = (battleChara.Address, battleChara.CastActionId);
                current.Add(key);
                if (activeCasts.Contains(key))
                    continue;
                ProcessSyncEvent(TimelineSyncEventType.CastStart, battleChara.CastActionId);
            }
            catch (NullReferenceException)
            {
                // Dalamud objects can disappear between ObjectTable enumeration and property access.
            }
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
        {
            try
            {
                foreach (var status in battleChara.StatusList)
                {
                    if (status.StatusId == 0)
                        continue;
                    var key = (battleChara.Address, status.StatusId);
                    current.Add(key);
                    if (!activeStatuses.Contains(key))
                        ProcessSyncEvent(TimelineSyncEventType.StatusGain, status.StatusId);
                }
            }
            catch (NullReferenceException)
            {
                // Skip actors that despawn while their status list is being read.
            }
        }
        activeStatuses.Clear();
        activeStatuses.UnionWith(current);
    }

    private void ProcessSyncEvent(TimelineSyncEventType eventType, uint eventId)
    {
        var encounterTimelineSynced = eventType == TimelineSyncEventType.CastStart &&
                                      ProcessEncounterTimelineSyncEvent(eventType, eventId);
        var candidates = SelectedFight.SyncTriggers
            .Where(item => item.EventType == eventType && item.EventId == eventId)
            .Where(item => string.IsNullOrEmpty(item.RequiredPhase) ||
                           currentPhase == item.RequiredPhase || currentPhase == item.ResultPhase)
            .Where(item => item.MatchWindowSeconds <= 0 || encounterTimelineStartedAt is not null &&
                           Math.Abs(ElapsedSeconds - item.TimelineSeconds) <= item.MatchWindowSeconds)
            .OrderBy(item => string.IsNullOrEmpty(item.ResultPhase) ? 1 : 0)
            // cactbot consumes the first active sync in timeline order.  Choosing the
            // nearest point can skip forward when one action ID repeats rapidly.
            .ThenBy(item => item.TimelineSeconds)
            .ToList();

        foreach (var trigger in candidates)
        {
            var key = $"{eventType}:{eventId:X}:{trigger.RequiredPhase}:{trigger.ResultPhase}:{trigger.TimelineSeconds}:{Math.Max(1, trigger.Occurrence)}";
            var anchorPhase = !string.IsNullOrEmpty(trigger.ResultPhase)
                ? trigger.ResultPhase
                : !string.IsNullOrEmpty(trigger.RequiredPhase)
                    ? trigger.RequiredPhase
                    : currentPhase;
            if (firedSyncTriggers.Contains(key) || syncedPhaseAnchors.Contains(anchorPhase))
                continue;
            var observedOccurrence = observedSyncOccurrences.GetValueOrDefault(key) + 1;
            observedSyncOccurrences[key] = observedOccurrence;
            if (observedOccurrence < Math.Max(1, trigger.Occurrence))
                continue;
            if (trigger.SuppressSeconds > 0 && lastTriggerTimes.TryGetValue(key, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromSeconds(trigger.SuppressSeconds))
                continue;

            // Phase-only and non-cast transition signals may tell us which phase
            // is active without correcting its clock. A small number of explicitly
            // configured phase anchors (such as DMU P3's first Decisive Battle)
            // resolve as abilities rather than cast starts and opt in separately.
            var canSyncClock = eventType == TimelineSyncEventType.CastStart || trigger.AllowNonCastSync ||
                               !string.IsNullOrEmpty(trigger.ResultPhase);
            if (trigger.PhaseOnly || !canSyncClock)
            {
                if (string.IsNullOrEmpty(trigger.ResultPhase))
                    continue;
                firedSyncTriggers.Add(key);
                lastTriggerTimes[key] = DateTime.UtcNow;
                currentPhase = trigger.ResultPhase;
                lastSyncStatus = $"Detected {currentPhase}; waiting for its first cast anchor.";
                continue;
            }

            firedSyncTriggers.Add(key);
            syncedPhaseAnchors.Add(anchorPhase);
            var observedAt = DateTime.UtcNow;
            lastTriggerTimes[key] = observedAt;
            ApplyTimelineSync(trigger.TimelineSeconds, observedAt, trigger.ResultPhase, trigger.Name, eventType, eventId,
                encounterTimelineSynced);
            if (trigger.MatchWindowSeconds > 0)
                break;
        }
    }

    private void ApplyTimelineSync(float timelineSeconds, DateTime observedAt, string resultPhase,
        string name, TimelineSyncEventType eventType, uint eventId, bool encounterTimelineSynced)
    {
        var elapsedSinceObservation = Math.Max(0, (int)(DateTime.UtcNow - observedAt).TotalSeconds);
        if (!string.IsNullOrEmpty(resultPhase))
            currentPhase = resultPhase;
        if (!encounterTimelineSynced || !string.IsNullOrEmpty(resultPhase))
            encounterTimelineStartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(timelineSeconds + elapsedSinceObservation);
        lastSyncStatus = $"Auto-synced {currentPhase} encounter clock at {FormatTime(ElapsedSeconds)} from {name} ({eventType} 0x{eventId:X}).";
    }

    private void StartTimer(int elapsedSeconds)
    {
        encounterTimelineStartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        currentPhase = SelectedFight.Phases.LastOrDefault(phase => phase.StartSeconds <= elapsedSeconds)?.Key ??
                       SelectedFight.Phases.FirstOrDefault()?.Key ?? string.Empty;
        firedEncounterTimelineSyncs.Clear();
    }

    private void StopTimer()
    {
        encounterTimelineStartedAt = null;
        debugTimelineRunning = false;
    }

    private float EncounterTimelineElapsedSeconds => encounterTimelineStartedAt is null
        ? 0
        : Math.Max(0, (float)(DateTime.UtcNow - encounterTimelineStartedAt.Value).TotalSeconds);

    private bool ProcessEncounterTimelineSyncEvent(TimelineSyncEventType eventType, uint eventId)
    {
        if (encounterTimelineStartedAt is null || SelectedFight.EncounterTimeline.Count == 0)
            return false;
        if (!string.IsNullOrEmpty(currentPhase) && syncedPhaseAnchors.Contains(currentPhase))
            return false;
        var elapsed = EncounterTimelineElapsedSeconds;
        var candidate = SelectedFight.EncounterTimeline
            .Where(item => item.EventType == eventType && item.EventIds.Contains(eventId))
            .Where(item => string.IsNullOrEmpty(item.Phase) || item.Phase == currentPhase)
            .Where(item => elapsed >= EncounterTimelineLinker.EventTime(SelectedFight, item) - item.WindowBeforeSeconds &&
                           elapsed <= EncounterTimelineLinker.EventTime(SelectedFight, item) + item.WindowAfterSeconds)
            .OrderBy(item => EncounterTimelineLinker.EventTime(SelectedFight, item))
            .FirstOrDefault(item => !firedEncounterTimelineSyncs.Contains(item.Id));
        if (candidate is null)
            return false;
        firedEncounterTimelineSyncs.Add(candidate.Id);
        if (!string.IsNullOrEmpty(currentPhase))
            syncedPhaseAnchors.Add(currentPhase);
        encounterTimelineStartedAt = DateTime.UtcNow -
                                     TimeSpan.FromSeconds(EncounterTimelineLinker.EventTime(SelectedFight, candidate, syncTarget: true));
        return true;
    }

    private int ElapsedSeconds => encounterTimelineStartedAt is null
        ? 0
        : Math.Max(0, (int)(DateTime.UtcNow - encounterTimelineStartedAt.Value).TotalSeconds);

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    private void Draw()
    {
        if (mainWindowOpen)
            DrawMainWindow();
        DrawEncounterSetupWindow();
        if (configuration.TestOverlay || configuration.ShowOverlay && (debugTimelineRunning ||
            encounterTimelineStartedAt is not null && condition[ConditionFlag.InCombat] && IsSelectedFightActive()))
            DrawOverlay();
        if ((configuration.EnableFightTimeline && (debugTimelineRunning || encounterTimelineStartedAt is not null &&
             condition[ConditionFlag.InCombat] && IsSelectedFightActive())) || configuration.TestFightTimeline)
            DrawFightTimelineOverlay();
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
        DrawPlayerSelectors();

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

    private void DrawPlayerSelectors()
    {
        var selectedJob = configuration.SelectedJob;
        var selectedRole = configuration.SelectedRole;
        var jobChanged = DrawCombo("Job", Jobs, ref selectedJob);
        var availableRoles = RolesForJob(selectedJob);
        var healerRoleAdjusted = jobChanged && selectedJob is "WHM" or "AST" or "SCH" or "SGE";
        var roleAdjusted = healerRoleAdjusted || !availableRoles.Contains(selectedRole);
        if (roleAdjusted)
            selectedRole = DefaultRoleForJob(selectedJob);
        var roleChanged = DrawCombo("Role / slot", availableRoles, ref selectedRole);
        var coTankChanged = false;
        if (TankJobs.Contains(selectedJob))
        {
            var selectedCoTankJob = configuration.SelectedCoTankJob;
            var availableCoTanks = TankJobs.Where(job => job != selectedJob).ToArray();
            if (!availableCoTanks.Contains(selectedCoTankJob))
                selectedCoTankJob = availableCoTanks[0];
            coTankChanged = DrawCombo("Co-tank job", availableCoTanks, ref selectedCoTankJob);
            configuration.SelectedCoTankJob = selectedCoTankJob;
            ImGui.TextDisabled($"Co-tank role: {(selectedRole == "MT" ? "OT" : "MT")} (automatic)");
        }
        if (jobChanged || roleChanged || roleAdjusted || coTankChanged)
        {
            configuration.SelectedJob = selectedJob;
            configuration.SelectedRole = selectedRole;
            Save();
        }
    }

    private void CheckForEncounterEntry()
    {
        var contentFinderConditionId = CurrentContentFinderConditionId();
        if (contentFinderConditionId == lastContentFinderConditionId)
            return;

        lastContentFinderConditionId = contentFinderConditionId;
        if (contentFinderConditionId == 0)
            return;

        var fight = FindSupportedFight(contentFinderConditionId);
        if (fight is null)
            return;

        SelectEncounterFight(fight);
        encounterSetupWindowOpen = true;
    }

    private void RequestEncounterSetupPopup()
    {
        var currentFight = FindSupportedFight(CurrentContentFinderConditionId());
        if (currentFight is not null)
            SelectEncounterFight(currentFight);
        else
            encounterSetupFightId = configuration.SelectedFightId;

        encounterSetupWindowOpen = true;
    }

    private void SelectEncounterFight(FightPlan fight)
    {
        if (configuration.SelectedFightId != fight.Id)
        {
            CancelEdit();
            StopTimer();
            currentPhase = string.Empty;
        }

        configuration.SelectedFightId = fight.Id;
        encounterSetupFightId = fight.Id;
        Save();
    }

    private FightPlan? FindSupportedFight(uint contentFinderConditionId)
    {
        if (contentFinderConditionId == 0)
            return null;

        var matches = configuration.Fights.Where(fight =>
            fight.ContentFinderConditionId == contentFinderConditionId &&
            fight.Category is "Savage" or "Ultimate").ToList();
        return matches.FirstOrDefault(fight => fight.Id == configuration.SelectedFightId) ??
               matches.FirstOrDefault(fight => fight.IsBuiltIn) ??
               matches.FirstOrDefault();
    }

    private void DrawEncounterSetupWindow()
    {
        if (!encounterSetupWindowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(430, 240), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MitPlan encounter setup##EncounterSetup", ref encounterSetupWindowOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var fight = configuration.Fights.FirstOrDefault(item => item.Id == encounterSetupFightId) ?? SelectedFight;
        ImGui.TextUnformatted(fight.Name);
        ImGui.TextDisabled("Choose the job and party slot MitPlan should use for this encounter.");
        ImGui.Separator();
        DrawPlayerSelectors();
        ImGui.Spacing();
        if (ImGui.Button("Confirm and close", new Vector2(160, 0)))
            encounterSetupWindowOpen = false;
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
            StopTimer();
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
                    StopTimer();
                    activeCasts.Clear();
                    firedSyncTriggers.Clear();
                    syncedPhaseAnchors.Clear();
                    lastTriggerTimes.Clear();
                    observedSyncOccurrences.Clear();
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
        {
            ImGui.TextDisabled("Source:");
            ImGui.SameLine();
            var sourceUrl = currentFight.SourceUrl;
            ImGui.SetNextItemWidth(-150);
            ImGui.InputText("##FightSourceUrl", ref sourceUrl, 1024, ImGuiInputTextFlags.ReadOnly);
            ImGui.SameLine();
            if (ImGui.Button("Copy to clipboard"))
                ImGui.SetClipboardText(currentFight.SourceUrl);
        }

        if (configuration.Fights.Count > 1)
        {
            var deleteUnlocked = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            if (!deleteUnlocked)
                ImGui.BeginDisabled();
            if (ImGui.Button("Delete selected fight"))
                ImGui.OpenPopup("DeleteFightConfirm");
            if (!deleteUnlocked)
                ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled("Hold Ctrl+Shift to unlock");
        }

        if (ImGui.BeginPopupModal("DeleteFightConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Delete '{currentFight.Name}' and all {currentFight.Timeline.Count} timeline entries?");
            var deleteUnlocked = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
            if (!deleteUnlocked)
                ImGui.BeginDisabled();
            if (ImGui.Button("Delete"))
            {
                configuration.Fights.Remove(currentFight);
                configuration.SelectedFightId = configuration.Fights[0].Id;
                CancelEdit();
                StopTimer();
                Save();
                ImGui.CloseCurrentPopup();
            }
            if (!deleteUnlocked)
                ImGui.EndDisabled();
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
            existing.EncounterEventId = string.Empty;
        }

        EncounterTimelineLinker.LinkFight(fight);
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

            var orderedItems = ApplicableSkillAlerts()
                .Where(alert => personalOnly is null ||
                    IsResolvedTankPersonalSkill(alert.Skill) == personalOnly.Value)
                .GroupBy(alert => alert.Item.Id)
                .Select(group => new TimelineDisplayRow(group.First().Item,
                    group.Select(alert => alert.Skill).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
                .OrderBy(row => fight.Phases.FindIndex(phase => phase.Key == TimelinePhase(row.Item)))
                .ThenBy(row => MitigationTime(row.Item))
                .ToList();
            var displayedPhase = string.Empty;
            foreach (var row in orderedItems)
            {
                var item = row.Item;
                var mitigationTime = MitigationTime(item);
                var itemPhase = TimelinePhase(item);
                if (itemPhase != displayedPhase)
                {
                    var phase = fight.Phases.FirstOrDefault(candidate => candidate.Key == itemPhase);
                    if (phase is not null)
                        DrawPhaseDivider(phase);
                    displayedPhase = itemPhase;
                }
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(item.TimeSeconds < 0 ? "Untimed" : FormatTime((int)MathF.Round(mitigationTime)));
                ImGui.TableNextColumn(); ImGui.Text(item.TargetJob == "Any Job" ? "Any" : item.TargetJob);
                ImGui.TableNextColumn(); ImGui.Text(item.TargetRole == "Any Role" ? "Any" : ShortRole(item.TargetRole));
                ImGui.TableNextColumn(); ImGui.TextWrapped(string.Join(" + ", row.Skills));
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
            EncounterTimelineLinker.LinkFight(fight);
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
        ImGui.TextDisabled("The encounter clock starts automatically when combat begins and stops when combat ends.");
        if (debugTimelineRunning)
        {
            if (ImGui.Button("Stop debug timeline"))
                StopDebugTimeline();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.2f, 1f),
                $"Simulating {SelectedFight.Name} at {FormatTime(ElapsedSeconds)}");
        }
        else
        {
            var inCombat = condition[ConditionFlag.InCombat];
            if (inCombat)
                ImGui.BeginDisabled();
            if (ImGui.Button("Debug timeline from 00:00"))
                StartDebugTimeline();
            if (inCombat)
                ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(inCombat
                    ? "Leave combat before starting the debug timeline."
                    : "Simulate the selected fight and mitigation callouts without entering the duty.");
        }
        ImGui.TextDisabled("Debug mode uses the selected job, role, mitigation timing, overlay, and audio settings.");

        ImGui.Separator();
        ImGui.TextUnformatted("Overlay positioning tests");
        if (ImGui.Button("Test mitigation alert"))
        {
            if (debugTimelineRunning)
                StopDebugTimeline();
            configuration.TestOverlay = !configuration.TestOverlay;
            Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Test timeline"))
        {
            if (debugTimelineRunning)
                StopDebugTimeline();
            configuration.TestFightTimeline = !configuration.TestFightTimeline;
            Save();
        }
        if (configuration.TestOverlay || configuration.TestFightTimeline)
        {
            var activeTests = configuration.TestOverlay && configuration.TestFightTimeline
                ? "Mitigation alert and timeline tests active"
                : configuration.TestOverlay
                    ? "Mitigation alert test active"
                    : "Timeline test active";
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.2f, 1f), $"{activeTests}; click again to close.");
        }
        else
        {
            ImGui.TextDisabled("Open a preview, then drag its overlay anywhere on the screen.");
        }

        var showOverlay = configuration.ShowOverlay;
        if (ImGui.Checkbox("Show alert overlay while timer is running", ref showOverlay))
        {
            configuration.ShowOverlay = showOverlay;
            Save();
        }
        var opacityPercent = (int)MathF.Round(configuration.OverlayOpacity * 100f);
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Text / icon opacity", ref opacityPercent, 10, 100, "%d%%"))
        {
            configuration.OverlayOpacity = opacityPercent / 100f;
            Save();
        }
        var backgroundOpacityPercent = (int)MathF.Round(configuration.OverlayBackgroundOpacity * 100f);
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Black background opacity", ref backgroundOpacityPercent, 0, 100, "%d%%"))
        {
            configuration.OverlayBackgroundOpacity = backgroundOpacityPercent / 100f;
            Save();
        }
        var textColor = ColorFromConfig(configuration.OverlayTextColor);
        if (ImGui.ColorEdit4("Text color", ref textColor, ImGuiColorEditFlags.NoAlpha))
        {
            configuration.OverlayTextColor = ColorToConfig(textColor);
            Save();
        }
        var glowText = configuration.GlowText;
        if (ImGui.Checkbox("Glow text", ref glowText))
        {
            configuration.GlowText = glowText;
            Save();
        }
        var glowColor = ColorFromConfig(configuration.OverlayGlowColor);
        if (ImGui.ColorEdit4("Glow color", ref glowColor, ImGuiColorEditFlags.NoAlpha))
        {
            configuration.OverlayGlowColor = ColorToConfig(glowColor);
            Save();
        }
        ImGui.TextDisabled("Known mitigation skills use their individual effect-duration timing.");
        var lead = configuration.LeadSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Fallback warning for uncatalogued skills (seconds)", ref lead))
        {
            configuration.LeadSeconds = Math.Clamp(lead, 0, 60);
            Save();
        }
        var keep = configuration.KeepSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Keep mitigation on screen after its timing", ref keep))
        {
            configuration.KeepSeconds = Math.Clamp(keep, 0, 60);
            Save();
        }
        var enablePersonalTankMitAlerts = configuration.EnablePersonalTankMitAlerts;
        if (ImGui.Checkbox("Personal Tank Mits", ref enablePersonalTankMitAlerts))
        {
            configuration.EnablePersonalTankMitAlerts = enablePersonalTankMitAlerts;
            Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show personal mitigation callouts while playing a tank. The personal-mit timeline remains visible when disabled.");
        var enableAudioAlert = configuration.EnableAudioAlert;
        if (ImGui.Checkbox("Enable audio alert", ref enableAudioAlert))
        {
            configuration.EnableAudioAlert = enableAudioAlert;
            Save();
        }
        if (configuration.EnableAudioAlert)
        {
            var audioModes = new[] { "Sound", "Skill names", "Custom" };
            var audioMode = (int)configuration.AudioAlertMode;
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Audio type", ref audioMode, audioModes, audioModes.Length))
            {
                configuration.AudioAlertMode = (AudioAlertMode)audioMode;
                Save();
            }
            if (configuration.AudioAlertMode == AudioAlertMode.Custom)
            {
                var ttsText = configuration.TtsText;
                ImGui.SetNextItemWidth(360);
                if (ImGui.InputText("Custom spoken text", ref ttsText, 256))
                {
                    configuration.TtsText = ttsText;
                    Save();
                }
                ImGui.TextDisabled("Use {skills} to insert the skill names into the spoken text.");
            }
            if (ImGui.Button("Test audio alert"))
                PlayConfiguredAudio(["Reprisal"]);
        }
        var displayModes = new[] { "Skill name", "Skill icon", "Name + icon" };
        var displayMode = (int)configuration.AlertDisplay;
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Overlay content", ref displayMode, displayModes, displayModes.Length))
        {
            configuration.AlertDisplay = (AlertDisplayMode)displayMode;
            Save();
        }
        ImGui.Separator();
        if (ImGui.Button(configuration.EnableFightTimeline ? "Close timeline" : "View timeline"))
        {
            configuration.EnableFightTimeline = !configuration.EnableFightTimeline;
            Save();
        }
        ImGui.TextDisabled("Visibility only; the encounter clock and mitigation callouts always remain active in the fight.");
        var fightTimelineOpacityPercent = (int)MathF.Round(configuration.FightTimelineOpacity * 100f);
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Timeline opacity", ref fightTimelineOpacityPercent, 10, 100, "%d%%"))
        {
            configuration.FightTimelineOpacity = fightTimelineOpacityPercent / 100f;
            Save();
        }
        var cactbotTimelineEntries = SelectedFight.EncounterTimeline.Count(item => item.Id.StartsWith("cactbot-", StringComparison.Ordinal));
        ImGui.TextDisabled(SelectedFight.EncounterTimeline.Count == 0
            ? "No encounter timeline is installed for this fight."
            : $"{cactbotTimelineEntries} cactbot entries; mitigations are linked to this encounter clock.");

        ImGui.Text($"Encounter time: {FormatTime(ElapsedSeconds)}");
        if (!string.IsNullOrWhiteSpace(lastSyncStatus))
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.45f, 1f), lastSyncStatus);
    }

    private void DrawFightTimelineOverlay()
    {
        var elapsed = EncounterTimelineElapsedSeconds;
        var entries = configuration.TestFightTimeline && !debugTimelineRunning
            ? new List<(float Time, float Duration, string Name)>
            {
                (elapsed + 2, 4, "Raidwide"),
                (elapsed + 9, 5, "Tankbusters"),
                (elapsed + 17, 0, "Movement mechanic"),
            }
            : SelectedFight.EncounterTimeline
                .Where(item => string.IsNullOrEmpty(item.Phase) || item.Phase == currentPhase)
                .Select(item => (Item: item, Time: EncounterTimelineLinker.EventTime(SelectedFight, item)))
                .Where(entry => entry.Time - elapsed is >= -3 and <= 30)
                .OrderBy(entry => entry.Time)
                .Take(8)
                .Select(entry => (entry.Time, Duration: entry.Item.DurationSeconds, Name: entry.Item.Name))
                .ToList();
        if (entries.Count == 0)
            return;

        ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.82f * configuration.FightTimelineOpacity);
        var open = true;
        if (ImGui.Begin("MitPlan Fight Timeline##FightTimeline", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, configuration.FightTimelineOpacity);
            if (ImGui.BeginTable("##FightTimelineRows", 2, ImGuiTableFlags.SizingFixedFit,
                    new Vector2(340, 0)))
            {
                foreach (var entry in entries)
                {
                    var remaining = entry.Time - elapsed;
                    var color = remaining <= 0
                        ? new Vector4(1f, 0.42f, 0.25f, 1f)
                        : remaining <= 5
                            ? new Vector4(1f, 0.78f, 0.2f, 1f)
                            : Vector4.One;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(color, remaining <= 0 ? "NOW" : $"{remaining:0.0}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(color, entry.Name);
                    if (entry.Duration > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"({entry.Duration:0.#}s)");
                    }
                }
                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
        }
        ImGui.End();

        if (!open)
        {
            configuration.EnableFightTimeline = false;
            configuration.TestFightTimeline = false;
            Save();
        }
    }

    private void DrawOverlay()
    {
        var elapsed = EncounterTimelineElapsedSeconds;
        var active = ApplicableSkillAlerts(activePhaseOnly: true)
            .Where(alert => configuration.EnablePersonalTankMitAlerts || !IsResolvedTankPersonalSkill(alert.Skill))
            .Where(alert => MitigationTime(alert.Item) - elapsed <=
                            MitigationTimings.LeadSeconds(alert.Skill, configuration.LeadSeconds) &&
                            MitigationTime(alert.Item) - elapsed >= -configuration.KeepSeconds)
            .Take(5)
            .ToList();
        if (configuration.TestOverlay && !debugTimelineRunning)
            active =
            [
                new SkillAlert(new TimelineItem { Id = "test-reprisal", Skill = "Reprisal" }, "Reprisal"),
                new SkillAlert(new TimelineItem { Id = "test-party-mit", Skill = "Shake It Off" }, "Shake It Off"),
            ];
        if (active.Count == 0)
            return;

        // The positioning/style preview is visual-only. Debug timeline playback
        // and live encounter alerts still use the configured sound or TTS mode.
        if (!configuration.TestOverlay)
            TriggerAlertAudio(active);

        ImGui.SetNextWindowSize(new Vector2(260, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(configuration.OverlayBackgroundOpacity);
        var overlayOpen = true;
        if (ImGui.Begin("MitPlan##Overlay", ref overlayOpen,
                 ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, configuration.OverlayOpacity);
            foreach (var skill in active.Select(alert => alert.Skill).Distinct(StringComparer.OrdinalIgnoreCase))
                DrawSkillAlert(skill);
            ImGui.PopStyleVar();
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
            ImGui.TextColored(CurrentAlertTextColor(), skill);
            if (showIcon)
                ImGui.SameLine();
        }
        if (showIcon && TryFindActionIcon(skill, out var iconId))
        {
            var texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId, false, true, null)).GetWrapOrEmpty();
            ImGui.Image(texture.Handle, new Vector2(36, 36));
            DrawComboHighlight(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }
        else if (showIcon && !showName)
            ImGui.TextDisabled("?");
    }

    private void TriggerAlertAudio(IEnumerable<SkillAlert> active)
    {
        if (!configuration.EnableAudioAlert)
            return;

        var newAlerts = active.Where(alert => playedAudioAlerts.Add(
            $"{SelectedFight.Id}:{alert.Item.Id}:{MitigationTime(alert.Item):0.###}:{alert.Skill}")).ToList();
        if (newAlerts.Count == 0)
            return;

        var skills = newAlerts
            .Select(alert => alert.Skill)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PlayConfiguredAudio(skills);
    }

    private void PlayConfiguredAudio(IReadOnlyCollection<string> skills)
    {
        if (configuration.AudioAlertMode == AudioAlertMode.Sound)
        {
            MessageBeep(0x30);
            return;
        }

        var skillText = skills.Count == 0 ? "mitigation" : string.Join(", ", skills);
        var speech = configuration.AudioAlertMode == AudioAlertMode.SkillNames
            ? skillText
            : (string.IsNullOrWhiteSpace(configuration.TtsText) ? "Use {skills}" : configuration.TtsText)
                .Replace("{skills}", skillText, StringComparison.OrdinalIgnoreCase)
                .Replace("{skill}", skillText, StringComparison.OrdinalIgnoreCase);
        _ = Task.Run(() => SpeakWithWindowsVoice(speech));
    }

    private static void SpeakWithWindowsVoice(string text)
    {
        object? voice = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType is null)
                return;
            voice = Activator.CreateInstance(voiceType);
            voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, 0]);
        }
        catch
        {
            // Windows SAPI can be unavailable when no voice is installed; visual alerts must continue normally.
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice))
                Marshal.FinalReleaseComObject(voice);
        }
    }

    private sealed record SkillAlert(TimelineItem Item, string Skill);
    private sealed record TimelineDisplayRow(TimelineItem Item, IReadOnlyList<string> Skills);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    private Vector4 CurrentAlertTextColor()
    {
        var textColor = ColorFromConfig(configuration.OverlayTextColor);
        if (!configuration.GlowText)
            return textColor;

        var glowColor = ColorFromConfig(configuration.OverlayGlowColor);
        var phase = MathF.Sin((float)ImGui.GetTime() * 2.8f) * 0.5f + 0.5f;
        var smoothPhase = phase * phase * (3f - 2f * phase);
        return Vector4.Lerp(textColor, glowColor, smoothPhase);
    }

    private static Vector4 ColorFromConfig(float[] color) =>
        color is { Length: 4 } ? new Vector4(color[0], color[1], color[2], color[3]) : Vector4.One;

    private static float[] ColorToConfig(Vector4 color) =>
        [color.X, color.Y, color.Z, 1f];

    private static void DrawComboHighlight(Vector2 min, Vector2 max)
    {
        var drawList = ImGui.GetWindowDrawList();
        min += Vector2.One;
        max -= Vector2.One;
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        var perimeter = 2f * (width + height);
        var animation = (float)ImGui.GetTime();
        var pulse = 0.72f + 0.28f * (MathF.Sin(animation * 1.7f) * 0.5f + 0.5f);
        var glowColor = ImGui.GetColorU32(new Vector4(1f, 0.72f, 0.08f, 0.30f * pulse));
        var brightColor = ImGui.GetColorU32(new Vector4(1f, 0.88f, 0.22f, 0.95f * pulse));

        drawList.AddLine(min, new Vector2(max.X, min.Y), glowColor, 4.5f);
        drawList.AddLine(new Vector2(max.X, min.Y), max, glowColor, 4.5f);
        drawList.AddLine(max, new Vector2(min.X, max.Y), glowColor, 4.5f);
        drawList.AddLine(new Vector2(min.X, max.Y), min, glowColor, 4.5f);

        const int movingSegments = 8;
        const float segmentLength = 6f;
        var travel = animation * 48f;
        for (var segment = 0; segment < movingSegments; segment++)
        {
            var start = travel + segment * perimeter / movingSegments;
            for (var point = 0; point <= 8; point++)
                drawList.PathLineTo(PointOnSquare(min, max, start + segmentLength * point / 8f));
            drawList.PathStroke(brightColor, ImDrawFlags.None, 2.2f);
        }
    }

    private static Vector2 PointOnSquare(Vector2 min, Vector2 max, float distance)
    {
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        var perimeter = 2f * (width + height);
        distance = (distance % perimeter + perimeter) % perimeter;
        if (distance <= width)
            return new Vector2(min.X + distance, min.Y);
        distance -= width;
        if (distance <= height)
            return new Vector2(max.X, min.Y + distance);
        distance -= height;
        if (distance <= width)
            return new Vector2(max.X - distance, max.Y);
        distance -= width;
        return new Vector2(min.X, max.Y - distance);
    }

    private bool TryFindActionIcon(string skill, out uint iconId)
    {
        var lookup = skill.Trim();
        if (lookup.EndsWith(" (Buddy)", StringComparison.OrdinalIgnoreCase))
            lookup = lookup[..^8].TrimEnd();
        if (lookup.Equals("Spreadlo", StringComparison.OrdinalIgnoreCase))
            lookup = "Adloquium";
        var actions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (IsLimitBreakInstruction(lookup))
        {
            iconId = dataManager.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>()
                .Where(row => row.Name.ToString().Equals("Limit Break", StringComparison.OrdinalIgnoreCase))
                .Select(row => (uint)Math.Max(0, row.Icon))
                .FirstOrDefault();
            if (iconId != 0)
                return true;
        }
        var exact = actions
            .Where(row => row.IsPlayerAction && !row.IsPvP &&
                          row.Name.ToString().Equals(lookup, StringComparison.OrdinalIgnoreCase))
            .Select(row => new { row.Icon })
            .FirstOrDefault();
        iconId = exact?.Icon ?? 0;
        if (iconId == 0)
        {
            var fallback = actions
                .Where(row => row.Icon != 0 && row.IsPlayerAction && !row.IsPvP && row.Name.ToString().Length >= 4 &&
                              lookup.Contains(row.Name.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(row => new { row.Icon, Name = row.Name.ToString() })
                .OrderByDescending(row => row.Name.Length)
                .FirstOrDefault();
            iconId = fallback?.Icon ?? 0;
        }
        return iconId != 0;
    }

    private static bool IsLimitBreakInstruction(string value) =>
        value.Contains("Limit Break", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(value, @"\bLB(?:\s*[123])?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    private IEnumerable<string> ResolveSkills(string instruction)
    {
        foreach (var component in SplitSkills(instruction))
        {
            var buddyTarget = IsExplicitBuddyInstruction(component);
            foreach (var skill in ResolveSkillNames(component))
            {
                if (IsSkillAvailableForSelectedJob(skill))
                    yield return buddyTarget ? $"{skill} (Buddy)" : skill;
            }
        }
    }

    private bool IsSkillAvailableForSelectedJob(string skill) =>
        !skill.Contains("Passage of Arms", StringComparison.OrdinalIgnoreCase) ||
        configuration.SelectedJob == "PLD";

    private static bool IsExplicitBuddyInstruction(string instruction) =>
        ContainsAny(instruction, "Buddy Mit", "Buddy", "Co-tank", "Cotank", "Co tank") ||
        System.Text.RegularExpressions.Regex.IsMatch(instruction,
            @"\b(?:on|to|assist)\s+(?:MT|OT)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private IEnumerable<string> ResolveSkillNames(string instruction)
    {
        var cleaned = instruction.Trim();
        if (ContainsAny(cleaned, "Zoe Shields", "Zoe Shield", "ZoeEProg", "Zoe Eukrasian Prognosis"))
            return ["Zoe", "Eukrasian Prognosis"];
        if (cleaned.Contains("Kitchen Sink", StringComparison.OrdinalIgnoreCase))
            return ["Rampart", TankNinetySecondCooldown(), TankMajorCooldown(), TankShortCooldown()];
        if (ContainsAny(cleaned, "90s", "90 sec", "90-second", "thrill", "bulwark", "dark mind", "camouflage", "camo"))
            return [TankNinetySecondCooldown()];
        if (ContainsAny(cleaned, "2min", "2 min", "2m", "120s", "120 sec", "30%", "40%", "big cd"))
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

    private bool IsResolvedTankPersonalSkill(string skill) => ContainsAny(skill,
        "Rampart", TankNinetySecondCooldown(), TankMajorCooldown(), TankShortCooldown(), TankInvulnerability(),
        TankBuddyCooldown(), "Nascent Flash", "Oblation", "Intervention", "Equilibrium", "Aurora");

    private IEnumerable<TimelineItem> ApplicableTimeline(bool activePhaseOnly = false)
    {
        var applicable = SelectedFight.Timeline
            .Where(item =>
                item.TimeSeconds >= 0 &&
                (!activePhaseOnly || TimelinePhase(item) == currentPhase) &&
                (item.TargetJob == "Any Job" || item.TargetJob == configuration.SelectedJob) &&
                (item.TargetRole == "Any Role" || NormalizeRole(item.TargetRole) == NormalizeRole(configuration.SelectedRole)) &&
                (item.TargetCoTankJob == "Any Tank" || item.TargetCoTankJob == configuration.SelectedCoTankJob) &&
                ResolveSkills(item.Skill).Any())
            .OrderBy(MitigationTime);
        return applicable;
    }

    private EncounterTimelineEvent? LinkedEncounterEvent(TimelineItem item) =>
        string.IsNullOrEmpty(item.EncounterEventId)
            ? null
            : SelectedFight.EncounterTimeline.FirstOrDefault(candidate => candidate.Id == item.EncounterEventId);

    private float MitigationTime(TimelineItem item)
    {
        var linked = LinkedEncounterEvent(item);
        return linked is null ? item.TimeSeconds : EncounterTimelineLinker.EventTime(SelectedFight, linked);
    }

    private string TimelinePhase(TimelineItem item) => EncounterTimelineLinker.TimelinePhase(SelectedFight, item);

    private IEnumerable<SkillAlert> ApplicableSkillAlerts(bool activePhaseOnly = false) =>
        CollapseRepeatedSkillAlerts(ApplicableTimeline(activePhaseOnly)
            .SelectMany(item => ResolveSkills(item.Skill)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(skill => new SkillAlert(item, skill))));

    private IEnumerable<SkillAlert> CollapseRepeatedSkillAlerts(IEnumerable<SkillAlert> alerts)
    {
        string Signature(SkillAlert alert) => $"{TimelinePhase(alert.Item)}|{alert.Skill}";

        var retained = new List<SkillAlert>();
        foreach (var group in alerts
                     .OrderBy(alert => MitigationTime(alert.Item))
                     .GroupBy(Signature, StringComparer.OrdinalIgnoreCase))
        {
            SkillAlert? firstInCluster = null;
            SkillAlert? lastInCluster = null;
            foreach (var alert in group)
            {
                if (lastInCluster is not null &&
                    MitigationTime(alert.Item) - MitigationTime(lastInCluster.Item) > DuplicateSequenceWindowSeconds)
                {
                    retained.Add(KeepFirstRepeatedOccurrence(lastInCluster.Skill)
                        ? firstInCluster!
                        : lastInCluster);
                    firstInCluster = alert;
                }

                firstInCluster ??= alert;
                lastInCluster = alert;
            }

            if (lastInCluster is not null)
                retained.Add(KeepFirstRepeatedOccurrence(lastInCluster.Skill)
                    ? firstInCluster!
                    : lastInCluster);
        }

        foreach (var alert in retained.OrderBy(alert => MitigationTime(alert.Item)))
            yield return alert;
    }

    private static bool KeepFirstRepeatedOccurrence(string skill) =>
        skill.Equals("Panhaima", StringComparison.OrdinalIgnoreCase);

    private static string DefaultRoleForJob(string job) => job switch
    {
        "WAR" or "PLD" or "DRK" or "GNB" => "MT",
        "WHM" or "AST" => "Pure Healer (H1)",
        "SCH" or "SGE" => "Shield Healer (H2)",
        "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" => "Melee 1 (M1) (D1)",
        "BRD" or "MCH" or "DNC" => "Phys Ranged (R1) (D3)",
        "BLM" or "SMN" or "RDM" or "PCT" or "BLU" => "Caster (R2) (D4)",
        _ => "Melee 1 (M1) (D1)",
    };

    private static string[] RolesForJob(string job) => job switch
    {
        "WAR" or "PLD" or "DRK" or "GNB" => ["MT", "OT"],
        "WHM" or "AST" or "SCH" or "SGE" => ["Pure Healer (H1)", "Shield Healer (H2)"],
        "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" =>
            ["Melee 1 (M1) (D1)", "Melee 2 (M2) (D2)"],
        "BRD" or "MCH" or "DNC" or "BLM" or "SMN" or "RDM" or "PCT" or "BLU" =>
            ["Melee 1 (M1) (D1)", "Melee 2 (M2) (D2)", "Phys Ranged (R1) (D3)", "Caster (R2) (D4)"],
        _ => Roles,
    };

    private static string NormalizeRole(string role) => role switch
    {
        "Pure Healer" => "Pure Healer (H1)",
        "Shield Healer" => "Shield Healer (H2)",
        _ => role,
    };

    private unsafe bool IsSelectedFightActive()
    {
        if (SelectedFight.ContentFinderConditionId == 0)
            return true;
        var gameMain = GameMain.Instance();
        return gameMain != null && gameMain->CurrentContentFinderConditionId == SelectedFight.ContentFinderConditionId;
    }

    private static unsafe uint CurrentContentFinderConditionId()
    {
        var gameMain = GameMain.Instance();
        return gameMain == null ? 0u : gameMain->CurrentContentFinderConditionId;
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
