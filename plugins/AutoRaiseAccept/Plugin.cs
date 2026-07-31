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
    private static readonly TimeSpan ReturnToStartDelay = TimeSpan.FromMinutes(2);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    private DateTime? deadDetectedAt;
    private DateTime? playerRaiseDetectedAt;
    private RevivePromptKind currentPromptKind;
    private bool handledCurrentPrompt;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        IGameGui gameGui,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
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
            ResetDeathState();
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
        chatGui.Print($"[Auto Raise Accept] {state}; player Raise delay {configuration.DelayMilliseconds} ms; return delay 2 minutes.");
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
            else if (DateTime.UtcNow - deadDetectedAt.Value < ReturnToStartDelay)
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
                : "Accepted return to the starting point after two minutes without a player Raise.");
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

    private enum RevivePromptKind
    {
        None,
        PlayerRaise,
        ReturnToStart,
    }
}
