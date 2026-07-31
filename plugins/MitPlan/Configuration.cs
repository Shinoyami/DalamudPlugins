using Dalamud.Configuration;

namespace MitPlan;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string SheetUrl { get; set; } = "https://docs.google.com/spreadsheets/d/10C3ytfH3irHqkb45rchIq5oqdAs-v_OKTj57M-Twi3k/";
    public string SelectedJob { get; set; } = "WAR";
    public string SelectedRole { get; set; } = "MT";
    public bool AutoStartWithCombat { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public int LeadSeconds { get; set; } = 8;
    public int KeepSeconds { get; set; } = 4;
}
