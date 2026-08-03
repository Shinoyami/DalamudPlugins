using Dalamud.Configuration;

namespace AutoRaiseAccept;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;
    public bool Enabled { get; set; } = true;
    public bool ShowDtrBar { get; set; } = true;
    public int RaiseAcceptDelayMilliseconds { get; set; }
    public int ReturnDelaySeconds { get; set; } = 120;

    public void Migrate()
    {
        RaiseAcceptDelayMilliseconds = System.Math.Clamp(RaiseAcceptDelayMilliseconds, 0, 60000);
        ReturnDelaySeconds = System.Math.Clamp(ReturnDelaySeconds, 0, 3600);
        Version = 4;
    }
}
