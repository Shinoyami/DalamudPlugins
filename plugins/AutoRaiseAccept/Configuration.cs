using Dalamud.Configuration;

namespace AutoRaiseAccept;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public bool ShowDtrBar { get; set; } = true;
    public int DelayMilliseconds { get; set; } = 250;
    public int ReturnDelaySeconds { get; set; } = 120;

    public void Migrate()
    {
        DelayMilliseconds = System.Math.Clamp(DelayMilliseconds, 0, 10000);
        ReturnDelaySeconds = System.Math.Clamp(ReturnDelaySeconds, 0, 3600);
        Version = 2;
    }
}
