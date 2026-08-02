# Auto Raise Accept

A Dalamud API 15 plugin that accepts incoming player Raises automatically. It
keeps the duty return-to-start prompt open until the configured timeout expires.

The settings window controls whether the plugin is enabled, whether its clickable
On/Off toggle appears in the DTR bar, and the return timeout. Player Raises are
accepted immediately.

Commands:

- `/ara` opens settings
- `/ara status`
- `/ara on`
- `/ara off`

Disable any Yes Already rule that matches `Accept Raise from`, since its generic
dialog handling may race this plugin.
