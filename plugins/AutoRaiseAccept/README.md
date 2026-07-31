# Auto Raise Accept

A minimal Dalamud API 15 plugin that automatically clicks the first physical
button (`Accept`) on an active incoming Raise dialog.

Commands:

- `/ara` or `/ara status`
- `/ara on`
- `/ara off`
- `/ara delay 250`

Disable any Yes Already rule that matches `Accept Raise from`, since its generic
dialog handling may race this plugin.
