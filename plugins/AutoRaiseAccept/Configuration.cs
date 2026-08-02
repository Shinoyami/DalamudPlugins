using Dalamud.Configuration;

namespace AutoRaiseAccept;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    public bool ShowDtrBar { get; set; } = true;
    public int ReturnDelaySeconds { get; set; } = 120;

    public void Migrate()
    {
        ReturnDelaySeconds = System.Math.Clamp(ReturnDelaySeconds, 0, 3600);
        Version = 3;
    }
}
