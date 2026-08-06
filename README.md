# Shinoyami's Dalamud Plugins

## Installation

1. Open Dalamud Settings with `/xlsettings`.
2. Open the **Experimental** tab.
3. Add this custom plugin repository URL:

   `https://raw.githubusercontent.com/Shinoyami/DalamudPlugins/main/pluginmaster.json`

4. Save, then open `/xlplugins` and install the desired plugin.

## Plugins

### Auto Raise Accept

Automatically clicks **Accept** on an incoming Raise dialog. It identifies the
game's resurrection agent and selects the first physical button, so it does not
depend on the raising player's name.

Commands: `/ara`, `/ara on`, `/ara off`, and `/ara delay <milliseconds>`.

### MitPlan

Displays editable fight timelines, filters assignments by selected job and party
slot, shows advance mitigation warnings, and optionally displays the encounter's
full cactbot-style mechanic timeline. Default mitigation plans are based
on PF / Ikuya / NAUR mitigation strategies where available. Use `/mitplan` to configure it.

### MitPlan Recorder

Records enemy casts and abilities into an editable encounter timeline, supports
phase-specific cast anchors and CSV mitigation imports, and sends reviewed plans
directly to MitPlan with the recorded encounter timeline included. Use `/mitrec` to open it.

## Disclaimer

Dalamud plugins are third-party tools. Use them at your own risk.
