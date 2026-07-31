using System;
using Dalamud.Game.Command;
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
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    private DateTime? raiseDetectedAt;
    private bool handledCurrentRaise;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IGameGui gameGui,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Auto Raise Accept: on, off, status, or delay <milliseconds>."
        });
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        commandManager.RemoveHandler(Command);
    }

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus();
            return;
        }

        if (parts[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            configuration.Enabled = true;
            Save();
            chatGui.Print("[Auto Raise Accept] Enabled.");
            return;
        }

        if (parts[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            configuration.Enabled = false;
            ResetRaiseState();
            Save();
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

        chatGui.Print("[Auto Raise Accept] Usage: /ara [on|off|status|delay <0-10000>]");
    }

    private void PrintStatus()
    {
        var state = configuration.Enabled ? "enabled" : "disabled";
        chatGui.Print($"[Auto Raise Accept] {state}; delay {configuration.DelayMilliseconds} ms.");
    }

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!configuration.Enabled)
            return;

        try
        {
            var reviveAgent = AgentRevive.Instance();
            var raiseIsActive = reviveAgent != null &&
                                reviveAgent->Revive != null &&
                                reviveAgent->IsAddonShown();

            if (!raiseIsActive)
            {
                ResetRaiseState();
                return;
            }

            if (handledCurrentRaise)
                return;

            raiseDetectedAt ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - raiseDetectedAt.Value).TotalMilliseconds < configuration.DelayMilliseconds)
                return;

            var addonAddress = gameGui.GetAddonByName("SelectYesno");
            if (addonAddress == nint.Zero)
                return;

            var addon = (AddonSelectYesno*)addonAddress.Address;
            if (!addon->AtkUnitBase.IsVisible)
                return;

            var acceptButton = addon->YesButton;
            if (acceptButton == null || !acceptButton->IsEnabled ||
                !acceptButton->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
                return;

            ClickButton(acceptButton, &addon->AtkUnitBase);
            handledCurrentRaise = true;
            log.Information("Accepted incoming Raise using the dialog's first (Accept) button.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to accept incoming Raise.");
            handledCurrentRaise = true;
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

    private void ResetRaiseState()
    {
        raiseDetectedAt = null;
        handledCurrentRaise = false;
    }
}
