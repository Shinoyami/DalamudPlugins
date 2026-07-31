using System.Collections.Generic;

namespace MitPlan;

public sealed record TimelineEntry(
    string Phase,
    string Mechanic,
    int GlobalSeconds,
    int PhaseSeconds,
    IReadOnlyDictionary<string, string> Assignments);

public sealed record PhasePlan(string Name, int StartSeconds, IReadOnlyList<TimelineEntry> Entries);

public sealed record MitigationReminder(
    string Phase,
    string Mechanic,
    int TimeSeconds,
    string Assignment);
