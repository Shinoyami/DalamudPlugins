# MitPlan

MitPlan displays editable fight timelines and mitigation assignments for the
player's selected job and party slot. The built-in/default mitigation plans are
based on PF / Ikuya / NAUR mitigation strategies where available.

## Features

- Includes editable presets for supported Savage and Ultimate fights.
- Uses PF / Ikuya / NAUR mitigation assignments where a public sheet is available.
- Supports all requested combat jobs and MT, OT, healer, D1-D4 slots.
- Automatically starts at combat entry and uses cactbot-derived, one-shot phase and mechanic anchors. Repeated action IDs are matched to the nearest expected point in a 20-second window, so the timer is corrected at the right occurrence without continuous resyncing.
- Keeps live mitigation reminders isolated to the active phase instead of continuously correcting the clock.
- Collapses repeated mitigation rows within 15 seconds to the final occurrence in that sequence; Panhaima keeps the first occurrence instead.
- Uses individual effect-duration warning times for catalogued mitigation skills, with a configurable fallback for custom text.
- Configurable alert persistence.
- Entering a configured Savage or Ultimate automatically selects that fight and opens the encounter setup popup.
- `/mitplan` opens the main window; `/mitplan p` opens player setup; `/mitplan start|stop|reset` controls the timer.

Timelines and assignments remain open to manual editing inside the plugin.
