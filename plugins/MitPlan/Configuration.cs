using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MitPlan;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;
    public string SelectedFightId { get; set; } = "dmu";
    public List<FightPlan> Fights { get; set; } = [FightPlan.CreateDefault()];
    public string SelectedJob { get; set; } = "WAR";
    public string SelectedRole { get; set; } = "MT";
    public bool AutoStartWithCombat { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public int LeadSeconds { get; set; } = 6;
    public int KeepSeconds { get; set; } = 4;
    public AlertDisplayMode AlertDisplay { get; set; } = AlertDisplayMode.NameAndIcon;
    public bool TestOverlay { get; set; }

    public void Migrate()
    {
        Fights ??= [];
        BuiltInPresets.MergeInto(this);
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
        Version = 6;
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
    public string SourceUrl { get; set; } = string.Empty;
    public string PresetStatus { get; set; } = string.Empty;
    public List<FightPhase> Phases { get; set; } = [];
    public List<ActorStateTransition> StateTransitions { get; set; } = [];
    public List<TimelineSyncTrigger> SyncTriggers { get; set; } = [];
    public List<TimelineItem> Timeline { get; set; } = [];

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
    public int TimelineSeconds { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RequiredPhase { get; set; } = string.Empty;
    public string ResultPhase { get; set; } = string.Empty;
    public int SuppressSeconds { get; set; }
}

public sealed class TimelineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int TimeSeconds { get; set; }
    public string Skill { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string TargetJob { get; set; } = "Any Job";
    public string TargetRole { get; set; } = "Any Role";
}
