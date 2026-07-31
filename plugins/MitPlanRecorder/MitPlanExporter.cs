using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MitPlanRecorder;

internal static class MitPlanExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string BuildJson(RecordingFile recording)
    {
        var phases = recording.Phases.Select((phase, index) => new ExportPhase
        {
            Name = phase.Name,
            Key = phase.Name,
            StartSeconds = (int)Math.Round(phase.StartSeconds),
        }).ToList();

        var triggers = new List<ExportTrigger>();
        for (var index = 0; index < recording.Phases.Count; index++)
        {
            var phase = recording.Phases[index];
            var anchor = recording.Events.FirstOrDefault(item => item.Id == phase.AnchorEventId);
            if (anchor is null)
                continue;
            var occurrence = recording.Events
                .TakeWhile(item => item.Id != anchor.Id)
                .Count(item => item.PhaseIndex == anchor.PhaseIndex && item.Kind == anchor.Kind &&
                               item.ActionId == anchor.ActionId) + 1;
            triggers.Add(new ExportTrigger
            {
                EventType = anchor.Kind == RecordedEventKind.CastStart ? 0 : 1,
                EventId = anchor.ActionId,
                Occurrence = occurrence,
                TimelineSeconds = (int)Math.Round(anchor.TimeSeconds),
                Name = $"{phase.Name} {anchor.ActionName}",
                RequiredPhase = index == 0 || phase.AllowCheckpointStart ? string.Empty : recording.Phases[index - 1].Name,
                ResultPhase = index == 0 ? string.Empty : phase.Name,
                SuppressSeconds = 3,
            });
        }

        var timeline = new List<ExportTimelineItem>();
        foreach (var item in recording.Events.Where(item => item.Included))
        {
            var phaseName = recording.Phases.ElementAtOrDefault(item.PhaseIndex)?.Name ?? "P1";
            var assignments = item.Assignments.Where(assignment => !string.IsNullOrWhiteSpace(assignment.Skill)).ToList();
            if (!string.IsNullOrWhiteSpace(item.ManualMitigation))
                assignments.AddRange(item.ManualMitigation.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(text => ParseManualAssignment(text, item.ManualTargetJob, item.ManualTargetRole)));
            foreach (var assignment in assignments)
                timeline.Add(new ExportTimelineItem
                {
                    TimeSeconds = (int)Math.Round(item.TimeSeconds),
                    Skill = assignment.Skill.Trim(),
                    Note = $"{phaseName} | {item.SourceName}: {item.ActionName}",
                    TargetJob = assignment.TargetJob,
                    TargetRole = assignment.TargetRole,
                    TargetCoTankJob = "Any Tank",
                });
        }

        var id = Slug(recording.FightName);
        var fight = new ExportFight
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : $"recorded-{id}",
            Name = recording.FightName,
            Category = recording.Category,
            ContentFinderConditionId = recording.ContentFinderConditionId,
            IsBuiltIn = false,
            PresetRevision = 0,
            PresetStatus = "Recorded encounter timeline with reviewed CSV/manual mitigation assignments.",
            Phases = phases,
            SyncTriggers = triggers,
            Timeline = timeline.OrderBy(item => item.TimeSeconds).ToList(),
        };
        return JsonSerializer.Serialize(fight, Options);
    }

    private static MitigationAssignment ParseManualAssignment(string text, string defaultJob, string defaultRole)
    {
        var separator = text.IndexOf(':');
        if (separator <= 0)
            return new MitigationAssignment { Skill = text, TargetJob = defaultJob, TargetRole = defaultRole };
        var target = CsvImporter.InferTarget(text[..separator]);
        return new MitigationAssignment
        {
            Skill = text[(separator + 1)..].Trim(),
            TargetJob = target.Job,
            TargetRole = target.Role,
        };
    }

    private static string Slug(string value) => string.Concat(value.ToLowerInvariant().Select(character =>
        char.IsLetterOrDigit(character) ? character : '-')).Trim('-');

    private sealed class ExportFight
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Custom";
        public uint ContentFinderConditionId { get; set; }
        public bool IsBuiltIn { get; set; }
        public int PresetRevision { get; set; }
        public string SourceUrl { get; set; } = string.Empty;
        public string PresetStatus { get; set; } = string.Empty;
        public List<ExportPhase> Phases { get; set; } = [];
        public List<object> StateTransitions { get; set; } = [];
        public List<ExportTrigger> SyncTriggers { get; set; } = [];
        public List<ExportTimelineItem> Timeline { get; set; } = [];
    }

    private sealed class ExportPhase
    {
        public string Name { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public int StartSeconds { get; set; }
    }

    private sealed class ExportTrigger
    {
        public int EventType { get; set; }
        public uint EventId { get; set; }
        public int Occurrence { get; set; } = 1;
        public int TimelineSeconds { get; set; }
        public string Name { get; set; } = string.Empty;
        public string RequiredPhase { get; set; } = string.Empty;
        public string ResultPhase { get; set; } = string.Empty;
        public int SuppressSeconds { get; set; }
    }

    private sealed class ExportTimelineItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int TimeSeconds { get; set; }
        public string Skill { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string TargetJob { get; set; } = "Any Job";
        public string TargetRole { get; set; } = "Any Role";
        public string TargetCoTankJob { get; set; } = "Any Tank";
    }
}
