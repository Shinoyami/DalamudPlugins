using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRaiseAccept;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/ara";
    private const string PlayerRaisePattern = "Accept Raise from";
    private const string ReturnPattern = "Return to the starting point";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IObjectTable objectTable;
    private readonly IDtrBar dtrBar;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly IDtrBarEntry dtrEntry;

    private DateTime? deadDetectedAt;
    private nint activeDialogAddress;
    private string activeDialogText = string.Empty;
    private ReviveDialogKind activeDialogKind;
    private bool activeDialogHandled;
    private bool playerRaiseSeenThisDeath;
    private bool settingsWindowOpen;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IAddonLifecycle addonLifecycle,
        IObjectTable objectTable,
        IDtrBar dtrBar,
        IGameGui gameGui,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.addonLifecycle = addonLifecycle;
        this.objectTable = objectTable;
        this.dtrBar = dtrBar;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Migrate();
        Save();

        dtrEntry = dtrBar.Get("Auto Raise Accept");
        dtrEntry.OnClick = _ => ToggleEnabled();
        UpdateDtrEntry();

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Auto Raise Accept settings, or use: on, off, status."
        });
        framework.Update += OnFrameworkUpdate;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoChanged);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "SelectYesno", OnSelectYesnoChanged);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoFinalized);
        pluginInterface.UiBuilder.Draw += DrawSettings;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += OpenSettings;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "SelectYesno", OnSelectYesnoChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoFinalized);
        pluginInterface.UiBuilder.Draw -= DrawSettings;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi -= OpenSettings;
        commandManager.RemoveHandler(Command);
        dtrBar.Remove("Auto Raise Accept");
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                settingsWindowOpen = true;
                break;
            case "on":
                SetEnabled(true);
                break;
            case "off":
                SetEnabled(false);
                break;
            case "status":
                PrintStatus();
                break;
            default:
                chatGui.Print("[Auto Raise Accept] Usage: /ara [on|off|status]. Use /ara with no argument for settings.");
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework frameworkService)
    {
        if (!configuration.Enabled)
            return;

        try
        {
            if (objectTable.LocalPlayer is not { IsDead: true })
            {
                ResetDeathState();
                return;
            }

            deadDetectedAt ??= DateTime.UtcNow;
            RefreshOrDiscoverDialog();
            if (activeDialogAddress == nint.Zero || activeDialogHandled)
                return;

            if (activeDialogKind == ReviveDialogKind.PlayerRaise)
            {
                playerRaiseSeenThisDeath = true;
                if (ClickFirstButton(activeDialogAddress))
                {
                    activeDialogHandled = true;
                    log.Information("Clicked Accept for incoming player Raise matched by '{Pattern}'.", PlayerRaisePattern);
                }
                return;
            }

            if (activeDialogKind != ReviveDialogKind.ReturnToStart || playerRaiseSeenThisDeath ||
                DateTime.UtcNow - deadDetectedAt.Value < TimeSpan.FromSeconds(configuration.ReturnDelaySeconds))
                return;

            if (ClickFirstButton(activeDialogAddress))
            {
                activeDialogHandled = true;
                log.Information("Clicked OK to return after {Seconds} seconds without a player Raise.",
                    configuration.ReturnDelaySeconds);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to process SelectYesno revive dialog.");
        }
    }

    private void OnSelectYesnoChanged(AddonEvent eventType, AddonArgs args)
    {
        if (!configuration.Enabled)
            return;
        CaptureDialog(args.Addon.Address, eventType.ToString());
    }

    private void OnSelectYesnoFinalized(AddonEvent _, AddonArgs args)
    {
        if (args.Addon.Address == activeDialogAddress)
            ClearDialog();
    }

    private void RefreshOrDiscoverDialog()
    {
        if (activeDialogAddress != nint.Zero)
        {
            CaptureDialog(activeDialogAddress, "framework refresh");
            return;
        }

        var addon = gameGui.GetAddonByName("SelectYesno");
        if (addon != nint.Zero)
            CaptureDialog(addon.Address, "framework discovery");
    }

    private void CaptureDialog(nint address, string source)
    {
        var addon = (AddonSelectYesno*)address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return;

        var text = ReadDialogText(&addon->AtkUnitBase);
        var kind = MatchDialog(text);
        if (kind == ReviveDialogKind.None)
            return;

        var changed = address != activeDialogAddress || kind != activeDialogKind || text != activeDialogText;
        activeDialogAddress = address;
        activeDialogText = text;
        activeDialogKind = kind;
        if (kind == ReviveDialogKind.PlayerRaise)
            playerRaiseSeenThisDeath = true;
        if (!changed)
            return;

        activeDialogHandled = false;
        log.Information("Matched {Kind} SelectYesno from {Source}: '{Text}'.", kind, source, text);
    }

    // This is the same legacy SelectYesno prompt path used by YesAlready/ECommons.
    private static string ReadDialogText(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0 ||
            addon->AtkValues[0].String.Value == null)
            return string.Empty;

        var seString = MemoryHelper.ReadSeStringNullTerminated((nint)addon->AtkValues[0].String.Value);
        return string.Join(string.Empty, seString.Payloads.OfType<TextPayload>().Select(payload => payload.Text))
            .Replace('\n', ' ').Trim();
    }

    private static ReviveDialogKind MatchDialog(string text)
    {
        if (ContainsIgnoringWhitespace(text, PlayerRaisePattern))
            return ReviveDialogKind.PlayerRaise;
        if (ContainsIgnoringWhitespace(text, ReturnPattern))
            return ReviveDialogKind.ReturnToStart;
        return ReviveDialogKind.None;
    }

    private static bool ContainsIgnoringWhitespace(string text, string pattern)
    {
        var normalizedText = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        var normalizedPattern = string.Concat(pattern.Where(character => !char.IsWhiteSpace(character)));
        return normalizedText.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    // Equivalent to YesAlready's AddonMaster.SelectYesno.Yes(): click the first physical button.
    private static bool ClickFirstButton(nint address)
    {
        var addon = (AddonSelectYesno*)address;
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->YesButton == null)
            return false;

        var button = addon->YesButton;
        if (!button->IsEnabled || !button->AtkResNode->IsVisible())
            return false;

        var buttonNode = button->AtkComponentBase.OwnerNode;
        if (buttonNode == null)
            return false;
        var eventPointer = buttonNode->AtkResNode.AtkEventManager.Event;
        if (eventPointer == null)
            return false;

        var clickEvent = (AtkEvent*)eventPointer;
        addon->AtkUnitBase.ReceiveEvent(clickEvent->State.EventType, (int)clickEvent->Param, eventPointer);
        return true;
    }

    private void ResetDeathState()
    {
        deadDetectedAt = null;
        playerRaiseSeenThisDeath = false;
        ClearDialog();
    }

    private void ClearDialog()
    {
        activeDialogAddress = nint.Zero;
        activeDialogText = string.Empty;
        activeDialogKind = ReviveDialogKind.None;
        activeDialogHandled = false;
    }

    private void SetEnabled(bool enabled)
    {
        configuration.Enabled = enabled;
        ResetDeathState();
        Save();
        UpdateDtrEntry();
        chatGui.Print($"[Auto Raise Accept] {(enabled ? "Enabled" : "Disabled")}.");
    }

    private void ToggleEnabled() => SetEnabled(!configuration.Enabled);

    private void PrintStatus()
    {
        var state = configuration.Enabled ? "enabled" : "disabled";
        chatGui.Print($"[Auto Raise Accept] {state}; return delay {configuration.ReturnDelaySeconds} seconds.");
    }

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    private void UpdateDtrEntry()
    {
        dtrEntry.Text = $"Auto Raise: {(configuration.Enabled ? "On" : "Off")}";
        dtrEntry.Tooltip = "Click to turn Auto Raise Accept on or off.";
        dtrEntry.Shown = configuration.ShowDtrBar;
    }

    private void OpenSettings() => settingsWindowOpen = true;

    private void DrawSettings()
    {
        if (!settingsWindowOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(430, 180), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Auto Raise Accept settings", ref settingsWindowOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable Auto Raise Accept", ref enabled))
            SetEnabled(enabled);

        var showDtrBar = configuration.ShowDtrBar;
        if (ImGui.Checkbox("Show DTR bar On/Off toggle", ref showDtrBar))
        {
            configuration.ShowDtrBar = showDtrBar;
            Save();
            UpdateDtrEntry();
        }

        var returnDelay = configuration.ReturnDelaySeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Return wait after death (seconds)", ref returnDelay))
        {
            configuration.ReturnDelaySeconds = Math.Clamp(returnDelay, 0, 3600);
            Save();
        }
        ImGui.TextDisabled($"Current return wait: {configuration.ReturnDelaySeconds / 60}:{configuration.ReturnDelaySeconds % 60:00}");
        ImGui.End();
    }

    private enum ReviveDialogKind
    {
        None,
        PlayerRaise,
        ReturnToStart,
    }
}
