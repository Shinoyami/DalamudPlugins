using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MitPlan;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 16;
    public string SelectedFightId { get; set; } = "dmu";
    public List<FightPlan> Fights { get; set; } = [FightPlan.CreateDefault()];
    public string SelectedJob { get; set; } = "WAR";
    public string SelectedRole { get; set; } = "MT";
    public string SelectedCoTankJob { get; set; } = "DRK";
    public bool ShowOverlay { get; set; } = true;
    public float OverlayOpacity { get; set; } = 1f;
    public float OverlayBackgroundOpacity { get; set; } = 1f;
    public float[] OverlayTextColor { get; set; } = [1f, 1f, 1f, 1f];
    public bool GlowText { get; set; }
    public float[] OverlayGlowColor { get; set; } = [1f, 0.72f, 0.08f, 1f];
    public int LeadSeconds { get; set; } = 2;
    public int KeepSeconds { get; set; } = 4;
    public bool EnableAudioAlert { get; set; }
    public bool EnablePersonalTankMitAlerts { get; set; } = true;
    public AudioAlertMode AudioAlertMode { get; set; } = AudioAlertMode.Sound;
    public string TtsText { get; set; } = "Use {skills}";
    public AlertDisplayMode AlertDisplay { get; set; } = AlertDisplayMode.NameAndIcon;
    public bool TestOverlay { get; set; }
    public bool EnableFightTimeline { get; set; }
    public bool TestFightTimeline { get; set; }
    public float FightTimelineOpacity { get; set; } = 1f;
    public float FightTimelineBackgroundOpacity { get; set; } = 1f;
    public bool EnableDiagnosticLog { get; set; }

    public void Migrate()
    {
        Fights ??= [];
        BuiltInPresets.MergeInto(this);
        foreach (var fight in Fights)
        {
            fight.Phases ??= [];
            fight.StateTransitions ??= [];
            fight.SyncTriggers ??= [];
            fight.Timeline ??= [];
            fight.EncounterTimeline ??= [];
            EncounterTimelineLinker.LinkFight(fight);
        }
        if (string.IsNullOrWhiteSpace(SelectedFightId) || Fights.TrueForAll(fight => fight.Id != SelectedFightId))
            SelectedFightId = Fights[0].Id;
        if (Version < 5 && LeadSeconds == 8)
            LeadSeconds = 6;
        if (Version < 6)
        {
            SelectedRole = RenameHealerRole(SelectedRole);
            foreach (var fight in Fights)
            foreach (var item in fight.Timeline)
                item.TargetRole = RenameHealerRole(item.TargetRole);
        }
        OverlayOpacity = Math.Clamp(OverlayOpacity, 0.1f, 1f);
        if (Version < 9)
            OverlayBackgroundOpacity = OverlayOpacity;
        if (Version < 10 && LeadSeconds == 6)
            LeadSeconds = 8;
        if (Version < 11 && LeadSeconds == 8)
            LeadSeconds = 4;
        if (Version < 12 && LeadSeconds == 4)
            LeadSeconds = 2;
        OverlayBackgroundOpacity = Math.Clamp(OverlayBackgroundOpacity, 0f, 1f);
        if (OverlayTextColor is not { Length: 4 })
            OverlayTextColor = [1f, 1f, 1f, 1f];
        if (OverlayGlowColor is not { Length: 4 })
            OverlayGlowColor = [1f, 0.72f, 0.08f, 1f];
        LeadSeconds = Math.Clamp(LeadSeconds, 0, 60);
        KeepSeconds = Math.Clamp(KeepSeconds, 0, 60);
        TtsText ??= "Use {skills}";
        if (Version < 15)
            FightTimelineBackgroundOpacity = FightTimelineOpacity;
        FightTimelineOpacity = Math.Clamp(FightTimelineOpacity, 0.1f, 1f);
        FightTimelineBackgroundOpacity = Math.Clamp(FightTimelineBackgroundOpacity, 0f, 1f);
        if (SelectedCoTankJob is not ("WAR" or "PLD" or "DRK" or "GNB") || SelectedCoTankJob == SelectedJob)
            SelectedCoTankJob = SelectedJob == "DRK" ? "WAR" : "DRK";
        for (var index = 0; index < 4; index++)
        {
            OverlayTextColor[index] = Math.Clamp(OverlayTextColor[index], 0f, 1f);
            OverlayGlowColor[index] = Math.Clamp(OverlayGlowColor[index], 0f, 1f);
        }
        Version = 16;
    }

    private static string RenameHealerRole(string role) => role switch
    {
        "Pure Healer" => "Pure Healer (H1)",
        "Shield Healer" => "Shield Healer (H2)",
        _ => role,
    };
}

public enum AlertDisplayMode
{
    NameOnly,
    IconOnly,
    NameAndIcon,
}

public sealed class FightPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Fight";
    public string Category { get; set; } = "Custom";
    public uint ContentFinderConditionId { get; set; }
    public bool IsBuiltIn { get; set; }
    public int PresetRevision { get; set; }
    public float ScheduleOffsetSeconds { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string PresetStatus { get; set; } = string.Empty;
    public List<FightPhase> Phases { get; set; } = [];
    public List<ActorStateTransition> StateTransitions { get; set; } = [];
    public List<TimelineSyncTrigger> SyncTriggers { get; set; } = [];
    public List<TimelineItem> Timeline { get; set; } = [];
    public List<EncounterTimelineEvent> EncounterTimeline { get; set; } = [];

    public static FightPlan CreateDefault() => new()
    {
        Id = "dmu",
        Name = "Dancing Mad Ultimate (DMU)",
        Category = "Ultimate"
    };
}

public enum ActorStateCondition
{
    Untargetable,
    UntargetableBelowFullHp,
    UntargetableAtOneHp,
    AtOneHpNotCasting,
    Targetable,
    DeadOrDestroyed,
    AnyDeadOrDestroyed,
    AllDeadOrDestroyed,
    AllUntargetableAtOneHp,
    AllUntargetableWithAnyBelowFullHp,
}

public sealed class ActorStateTransition
{
    public string RequiredPhase { get; set; } = string.Empty;
    public string ResultPhase { get; set; } = string.Empty;
    public int TimelineSeconds { get; set; }
    public ActorStateCondition Condition { get; set; }
    public List<uint> ActorDataIds { get; set; } = [];
    public string Name { get; set; } = string.Empty;
}

public sealed class FightPhase
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int StartSeconds { get; set; }
    public float EncounterTimelineStartSeconds { get; set; }
}

public sealed class EncounterTimelineEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public float TimeSeconds { get; set; }
    public float SyncToSeconds { get; set; }
    public float DurationSeconds { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public TimelineSyncEventType? EventType { get; set; }
    public List<uint> EventIds { get; set; } = [];
    public float WindowBeforeSeconds { get; set; } = 2.5f;
    public float WindowAfterSeconds { get; set; } = 2.5f;
}

public enum TimelineSyncEventType
{
    CastStart,
    Ability,
    StatusGain,
}

public sealed class TimelineSyncTrigger
{
    public TimelineSyncEventType EventType { get; set; } = TimelineSyncEventType.CastStart;
    public uint EventId { get; set; }
    public int Occurrence { get; set; } = 1;
    public float TimelineSeconds { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RequiredPhase { get; set; } = string.Empty;
    public string ResultPhase { get; set; } = string.Empty;
    public int SuppressSeconds { get; set; }
    public int MatchWindowSeconds { get; set; }
    public bool PhaseOnly { get; set; }
    public bool AllowNonCastSync { get; set; }
}

public sealed class TimelineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int TimeSeconds { get; set; }
    public string Skill { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string TargetJob { get; set; } = "Any Job";
    public string TargetRole { get; set; } = "Any Role";
    public string TargetCoTankJob { get; set; } = "Any Tank";
    public string EncounterEventId { get; set; } = string.Empty;
}

public enum AudioAlertMode
{
    Sound,
    SkillNames,
    Custom,
}
