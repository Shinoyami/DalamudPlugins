using System;
using System.Collections.Generic;
using System.Linq;

namespace MitPlan;

internal static class EncounterTimelineLinker
{
    public static void LinkFight(FightPlan fight)
    {
        if (fight.EncounterTimeline.Count == 0)
            return;
        fight.EncounterTimeline.RemoveAll(item => item.Id.StartsWith("mit-", StringComparison.Ordinal));
        foreach (var item in fight.Timeline.Where(item => item.EncounterEventId.StartsWith("mit-", StringComparison.Ordinal)))
            item.EncounterEventId = string.Empty;
        var eventIds = fight.EncounterTimeline.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var linkedPlannerTimes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in fight.Timeline.OrderBy(item => item.TimeSeconds))
        {
            if (!string.IsNullOrEmpty(item.EncounterEventId) && eventIds.Contains(item.EncounterEventId))
            {
                if (!linkedPlannerTimes.TryGetValue(item.EncounterEventId, out var existingTime) ||
                    existingTime == item.TimeSeconds)
                {
                    linkedPlannerTimes.TryAdd(item.EncounterEventId, item.TimeSeconds);
                    continue;
                }
                item.EncounterEventId = string.Empty;
            }
            var linked = FindBestEvent(fight, item);
            if (linked is not null && linkedPlannerTimes.TryGetValue(linked.Id, out var linkedTime) &&
                linkedTime != item.TimeSeconds)
                linked = null;
            linked ??= AddSyntheticEvent(fight, item);
            item.EncounterEventId = linked?.Id ?? string.Empty;
            if (linked is not null)
            {
                eventIds.Add(linked.Id);
                linkedPlannerTimes.TryAdd(linked.Id, item.TimeSeconds);
            }
        }
    }

    public static EncounterTimelineEvent? FindBestEvent(FightPlan fight, TimelineItem item)
    {
        var phase = TimelinePhase(fight, item);
        var mechanic = MechanicName(item.Note);
        var expectedTime = ExpectedTime(fight, item);
        var candidates = fight.EncounterTimeline
            .Where(candidate => string.IsNullOrEmpty(phase) || candidate.Phase == phase)
            .Select(candidate => new
            {
                Event = candidate,
                NameScore = NameScore(candidate.Name, mechanic),
                Distance = Math.Abs(EventTime(fight, candidate) - expectedTime),
            })
            .OrderByDescending(candidate => candidate.NameScore)
            .ThenBy(candidate => candidate.Distance)
            .ToList();
        var best = candidates.FirstOrDefault();
        return best is not null && (best.NameScore >= 70 || best.Distance <= 8) ? best.Event : null;
    }

    public static float EventTime(FightPlan fight, EncounterTimelineEvent item, bool syncTarget = false)
    {
        var phase = fight.Phases.FirstOrDefault(candidate => candidate.Key == item.Phase);
        if (phase is null)
            return syncTarget ? item.SyncToSeconds : item.TimeSeconds;
        var rawPhaseStart = RawPhaseStart(fight, phase);
        var rawTime = syncTarget ? item.SyncToSeconds : item.TimeSeconds;
        return phase.StartSeconds + rawTime - rawPhaseStart;
    }

    public static float ExpectedTime(FightPlan fight, TimelineItem item)
    {
        var phaseKey = TimelinePhase(fight, item);
        var phase = fight.Phases.FirstOrDefault(candidate => candidate.Key == phaseKey);
        if (phase is null)
            return item.TimeSeconds;
        var anchors = new List<(float PlannerTime, float EncounterTime)>
        {
            (phase.StartSeconds, phase.StartSeconds),
        };
        foreach (var trigger in fight.SyncTriggers)
        {
            var triggerPhase = !string.IsNullOrEmpty(trigger.ResultPhase)
                ? trigger.ResultPhase
                : !string.IsNullOrEmpty(trigger.RequiredPhase)
                    ? trigger.RequiredPhase
                    : fight.Phases.Where(candidate => candidate.StartSeconds <= trigger.TimelineSeconds)
                        .OrderByDescending(candidate => candidate.StartSeconds).FirstOrDefault()?.Key;
            if (triggerPhase != phaseKey)
                continue;
            var matching = fight.EncounterTimeline
                .Where(candidate => candidate.Phase == phaseKey && candidate.EventType == trigger.EventType &&
                                    candidate.EventIds.Contains(trigger.EventId))
                .OrderBy(candidate => EventTime(fight, candidate))
                .ToList();
            if (matching.Count == 0)
                continue;
            var occurrence = Math.Clamp(Math.Max(1, trigger.Occurrence) - 1, 0, matching.Count - 1);
            anchors.Add((trigger.TimelineSeconds, EventTime(fight, matching[occurrence])));
        }
        var nearest = anchors.OrderBy(anchor => Math.Abs(anchor.PlannerTime - item.TimeSeconds)).First();
        return item.TimeSeconds + nearest.EncounterTime - nearest.PlannerTime;
    }

    public static string TimelinePhase(FightPlan fight, TimelineItem item)
    {
        if (!string.IsNullOrEmpty(item.EncounterEventId))
        {
            var linked = fight.EncounterTimeline.FirstOrDefault(candidate => candidate.Id == item.EncounterEventId);
            if (linked is not null)
                return linked.Phase;
        }

        var separator = item.Note.IndexOf('|');
        if (separator > 0)
        {
            var label = item.Note[..separator].Trim();
            var labeled = fight.Phases.FirstOrDefault(phase =>
                phase.Key.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                phase.Key.StartsWith($"{label} ", StringComparison.OrdinalIgnoreCase));
            if (labeled is not null)
                return labeled.Key;
        }
        return fight.Phases.Where(phase => phase.StartSeconds <= item.TimeSeconds)
            .OrderByDescending(phase => phase.StartSeconds)
            .FirstOrDefault()?.Key ?? fight.Phases.FirstOrDefault()?.Key ?? string.Empty;
    }

    private static float RawPhaseStart(FightPlan fight, FightPhase phase)
    {
        if (phase.EncounterTimelineStartSeconds > 0)
            return phase.EncounterTimelineStartSeconds;
        return EncounterTimelines.PhaseStart(fight.Id, phase.Key) ?? fight.EncounterTimeline
            .Where(item => item.Phase == phase.Key)
            .Select(item => (float?)item.TimeSeconds)
            .Min() ?? phase.StartSeconds;
    }

    private static EncounterTimelineEvent? AddSyntheticEvent(FightPlan fight, TimelineItem item)
    {
        var phaseKey = TimelinePhase(fight, item);
        var phase = fight.Phases.FirstOrDefault(candidate => candidate.Key == phaseKey);
        if (phase is null)
            return null;
        var rawPhaseStart = RawPhaseStart(fight, phase);
        var mechanic = MechanicLabel(item.Note);
        var id = $"mit-{Slug(phaseKey)}-{item.TimeSeconds}-{Slug(mechanic)}";
        var existing = fight.EncounterTimeline.FirstOrDefault(candidate => candidate.Id == id);
        if (existing is not null)
            return existing;
        var expectedTime = ExpectedTime(fight, item);
        var rawTime = rawPhaseStart + expectedTime - phase.StartSeconds;
        var created = new EncounterTimelineEvent
        {
            Id = id,
            TimeSeconds = rawTime,
            SyncToSeconds = rawTime,
            Name = string.IsNullOrWhiteSpace(mechanic) ? item.Skill : mechanic,
            Phase = phaseKey,
        };
        fight.EncounterTimeline.Add(created);
        return created;
    }

    private static HashSet<string> MechanicName(string note)
    {
        return Words(MechanicLabel(note));
    }

    private static string MechanicLabel(string note)
    {
        var colon = note.LastIndexOf(':');
        return (colon >= 0 ? note[(colon + 1)..] : note).Trim();
    }

    private static int NameScore(string candidateName, HashSet<string> mechanic)
    {
        if (mechanic.Count == 0)
            return 0;
        var isCastbar = candidateName.Contains("castbar", StringComparison.OrdinalIgnoreCase);
        var candidate = Words(candidateName.Replace("castbar", string.Empty, StringComparison.OrdinalIgnoreCase));
        if (candidate.SetEquals(mechanic))
            return isCastbar ? 99 : 100;
        var overlap = candidate.Count(mechanic.Contains);
        if (overlap == 0)
            return 0;
        var coverage = (double)overlap / Math.Max(candidate.Count, mechanic.Count);
        var numberMismatch = candidate.Where(IsNumber).Any(word => !mechanic.Contains(word)) ||
                             mechanic.Where(IsNumber).Any(word => !candidate.Contains(word));
        return (int)Math.Round(coverage * 80) - (numberMismatch ? 25 : 0) - (isCastbar ? 1 : 0);
    }

    private static bool IsNumber(string value) => int.TryParse(value, out _);

    private static string Slug(string value) => string.Join('-', Words(value));

    private static HashSet<string> Words(string value) => value
        .Split([' ', '/', '+', '-', ':', '(', ')', '[', ']', ',', '.', '\'', '’'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.ToLowerInvariant() switch
        {
            "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5",
            "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", "x" => "10",
            var normalized => normalized.TrimStart('#'),
        })
        .Where(word => word.Length >= 3 || IsNumber(word))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
