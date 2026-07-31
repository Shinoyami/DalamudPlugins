using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRaiseAccept;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/ara";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IDtrBar dtrBar;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly IDtrBarEntry dtrEntry;

    private DateTime? deadDetectedAt;
    private DateTime? playerRaiseDetectedAt;
    private RevivePromptKind currentPromptKind;
    private bool handledCurrentPrompt;
    private bool settingsWindowOpen;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        IDtrBar dtrBar,
        IGameGui gameGui,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
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
            HelpMessage = "Open Auto Raise Accept settings, or use: on, off, status, delay <milliseconds>."
        });
        framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += DrawSettings;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += OpenSettings;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= DrawSettings;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi -= OpenSettings;
        commandManager.RemoveHandler(Command);
        dtrBar.Remove("Auto Raise Accept");
    }

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 0)
                settingsWindowOpen = true;
            else
                PrintStatus();
            return;
        }

        if (parts[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            configuration.Enabled = true;
            Save();
            UpdateDtrEntry();
            chatGui.Print("[Auto Raise Accept] Enabled.");
            return;
        }

        if (parts[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            configuration.Enabled = false;
            ResetDeathState();
            Save();
            UpdateDtrEntry();
            chatGui.Print("[Auto Raise Accept] Disabled.");
            return;
        }

        if (parts[0].Equals("delay", StringComparison.OrdinalIgnoreCase) &&
            parts.Length >= 2 && int.TryParse(parts[1], out var delay))
        {
            configuration.DelayMilliseconds = Math.Clamp(delay, 0, 10000);
            Save();
            chatGui.Print($"[Auto Raise Accept] Delay: {configuration.DelayMilliseconds} ms.");
            return;
        }

        chatGui.Print("[Auto Raise Accept] Usage: /ara [on|off|status|delay <0-10000>]. Use /ara with no argument for settings.");
    }

    private void PrintStatus()
    {
        var state = configuration.Enabled ? "enabled" : "disabled";
        chatGui.Print($"[Auto Raise Accept] {state}; player Raise delay {configuration.DelayMilliseconds} ms; return delay {configuration.ReturnDelaySeconds} seconds.");
    }

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    private void OnFrameworkUpdate(IFramework _)
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
            var reviveAgent = AgentRevive.Instance();
            var revivePromptIsActive = reviveAgent != null &&
                                      reviveAgent->Revive != null &&
                                      reviveAgent->IsAddonShown();

            if (!revivePromptIsActive)
            {
                ResetPromptState();
                return;
            }

            var addonAddress = gameGui.GetAddonByName("SelectYesno");
            if (addonAddress == nint.Zero)
                return;

            var addon = (AddonSelectYesno*)addonAddress.Address;
            if (!addon->AtkUnitBase.IsVisible)
                return;

            // AgentRevive is used by both player Raises and the duty return prompt.
            // A non-zero resurrecting player ID identifies a Raise without depending on the caster's name.
            var promptKind = reviveAgent->ResurrectingPlayerId != 0
                ? RevivePromptKind.PlayerRaise
                : RevivePromptKind.ReturnToStart;
            if (promptKind != currentPromptKind)
            {
                currentPromptKind = promptKind;
                handledCurrentPrompt = false;
                playerRaiseDetectedAt = promptKind == RevivePromptKind.PlayerRaise ? DateTime.UtcNow : null;
            }

            if (handledCurrentPrompt)
                return;

            if (promptKind == RevivePromptKind.PlayerRaise)
            {
                playerRaiseDetectedAt ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - playerRaiseDetectedAt.Value).TotalMilliseconds < configuration.DelayMilliseconds)
                    return;
            }
            else if (DateTime.UtcNow - deadDetectedAt.Value < TimeSpan.FromSeconds(configuration.ReturnDelaySeconds))
            {
                return;
            }

            var acceptButton = addon->YesButton;
            if (acceptButton == null || !acceptButton->IsEnabled ||
                !acceptButton->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
                return;

            ClickButton(acceptButton, &addon->AtkUnitBase);
            handledCurrentPrompt = true;
            log.Information(promptKind == RevivePromptKind.PlayerRaise
                ? "Accepted incoming player Raise."
                : $"Accepted return to the starting point after {configuration.ReturnDelaySeconds} seconds without a player Raise.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to handle revive prompt.");
            handledCurrentPrompt = true;
        }
    }

    private static void ClickButton(AtkComponentButton* button, AtkUnitBase* addon)
    {
        var buttonNode = button->AtkComponentBase.OwnerNode;
        var eventPointer = buttonNode->AtkResNode.AtkEventManager.Event;
        if (eventPointer == null)
            return;

        var clickEvent = (AtkEvent*)eventPointer;
        addon->ReceiveEvent(clickEvent->State.EventType, (int)clickEvent->Param, clickEvent);
    }

    private void ResetPromptState()
    {
        playerRaiseDetectedAt = null;
        currentPromptKind = RevivePromptKind.None;
        handledCurrentPrompt = false;
    }

    private void ResetDeathState()
    {
        deadDetectedAt = null;
        ResetPromptState();
    }

    private void OpenSettings() => settingsWindowOpen = true;

    private void ToggleEnabled()
    {
        configuration.Enabled = !configuration.Enabled;
        if (!configuration.Enabled)
            ResetDeathState();
        Save();
        UpdateDtrEntry();
        chatGui.Print($"[Auto Raise Accept] {(configuration.Enabled ? "Enabled" : "Disabled")}.");
    }

    private void UpdateDtrEntry()
    {
        dtrEntry.Text = $"Auto Raise: {(configuration.Enabled ? "On" : "Off")}";
        dtrEntry.Tooltip = "Click to turn Auto Raise Accept on or off.";
        dtrEntry.Shown = configuration.ShowDtrBar;
    }

    private void DrawSettings()
    {
        if (!settingsWindowOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(430, 210), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Auto Raise Accept settings", ref settingsWindowOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable Auto Raise Accept", ref enabled))
        {
            configuration.Enabled = enabled;
            if (!enabled)
                ResetDeathState();
            Save();
            UpdateDtrEntry();
        }

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

        var raiseDelay = configuration.DelayMilliseconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Player Raise click delay (milliseconds)", ref raiseDelay))
        {
            configuration.DelayMilliseconds = Math.Clamp(raiseDelay, 0, 10000);
            Save();
        }

        ImGui.End();
    }

    private enum RevivePromptKind
    {
        None,
        PlayerRaise,
        ReturnToStart,
    }
}
