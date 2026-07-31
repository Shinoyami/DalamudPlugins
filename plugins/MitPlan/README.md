# MitPlan

MitPlan reads mitigation timelines from a public Google Sheet and displays the
assignments for the player's selected job and party slot.

## Features

- Downloads P1-P5 through Google Sheets' CSV export endpoint.
- Preserves multiline mitigation assignments.
- Supports all requested combat jobs and MT, OT, healer, D1-D4 slots.
- Automatically starts at combat entry or can be started/synchronized manually.
- Configurable advance warning and alert persistence.
- `/mitplan` opens the window; `/mitplan start|stop|reset|reload` controls it.

The source spreadsheet must be publicly readable. No Google sign-in or API key
is required.
