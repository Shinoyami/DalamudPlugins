using System;
using Dalamud.Configuration;

namespace MitPlanRecorder;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool AutoRecord { get; set; } = true;
    public bool RecordResolvedAbilities { get; set; } = true;
    public bool AutoCreatePhaseCandidates { get; set; } = true;
    public float PhaseDowntimeSeconds { get; set; } = 2f;
    public string LastCsvDirectory { get; set; } = string.Empty;

    public void Migrate()
    {
        PhaseDowntimeSeconds = Math.Clamp(PhaseDowntimeSeconds, 0.5f, 30f);
        LastCsvDirectory ??= string.Empty;
        Version = 1;
    }
}
