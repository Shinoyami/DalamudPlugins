using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MitPlanRecorder;

public enum RecordedEventKind
{
    CastStart,
    Ability,
}

public sealed class RecordedEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double TimeSeconds { get; set; }
    public RecordedEventKind Kind { get; set; }
    public uint ActionId { get; set; }
    public string ActionName { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public uint SourceBaseId { get; set; }
    public uint SourceEntityId { get; set; }
    public int PhaseIndex { get; set; }
    public bool Included { get; set; } = true;
    public bool UseAsSyncAnchor { get; set; } = true;
    public string ManualMitigation { get; set; } = string.Empty;
    public string ManualTargetJob { get; set; } = "Any Job";
    public string ManualTargetRole { get; set; } = "Any Role";
    public List<MitigationAssignment> Assignments { get; set; } = [];
}

public sealed class RecordedPhase
{
    public string Name { get; set; } = "P1";
    public double StartSeconds { get; set; }
    public string? AnchorEventId { get; set; }
    public bool AllowCheckpointStart { get; set; }
    [JsonIgnore] public bool AwaitingAnchor { get; set; }
}

public sealed class MitigationAssignment
{
    public string Skill { get; set; } = string.Empty;
    public string TargetJob { get; set; } = "Any Job";
    public string TargetRole { get; set; } = "Any Role";
    public string SourceColumn { get; set; } = string.Empty;
}

public sealed class CsvDocument
{
    public string Path { get; set; } = string.Empty;
    public int HeaderRowIndex { get; set; }
    public List<string> Headers { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
    public int TimeColumn { get; set; } = -1;
    public int MechanicColumn { get; set; } = -1;
    public int PhaseColumn { get; set; } = -1;
    public List<CsvMitigationColumn> MitigationColumns { get; set; } = [];
}

public sealed class CsvMitigationColumn
{
    public int Index { get; set; }
    public string Header { get; set; } = string.Empty;
    public bool Included { get; set; }
    public string TargetJob { get; set; } = "Any Job";
    public string TargetRole { get; set; } = "Any Role";
}

public sealed class CsvMatch
{
    public int CsvRowIndex { get; set; }
    public double? CsvTimeSeconds { get; set; }
    public string CsvMechanic { get; set; } = string.Empty;
    public string CsvPhase { get; set; } = string.Empty;
    public string? EventId { get; set; }
    public double Confidence { get; set; }
    public bool Applied { get; set; }
}

public sealed class RecordingFile
{
    public string FightName { get; set; } = "New Fight";
    public string Category { get; set; } = "Custom";
    public uint ContentFinderConditionId { get; set; }
    public ushort TerritoryType { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public List<RecordedPhase> Phases { get; set; } = [];
    public List<RecordedEvent> Events { get; set; } = [];
}
