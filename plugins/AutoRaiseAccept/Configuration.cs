using Dalamud.Configuration;

namespace AutoRaiseAccept;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int DelayMilliseconds { get; set; } = 250;
}
