# MitPlan

MitPlan displays editable fight timelines and mitigation assignments for the
player's selected job and party slot. The built-in/default mitigation plans are
based on PF / Ikuya / NAUR mitigation strategies where available.

## Features

- Includes editable presets for supported Savage and Ultimate fights.
- Uses PF / Ikuya / NAUR mitigation assignments where a public sheet is available.
- Supports all requested combat jobs and MT, OT, healer, D1-D4 slots.
- Automatically starts at combat entry and uses one-shot phase and mechanic anchors. Repeated action IDs are consumed in chronological timeline order inside a 20-second recovery window, without continuous resyncing.
- Optional movable encounter-timeline overlay for M12S and the supported Ultimate fights, using the full visible mechanic timeline.
- Uses one authoritative encounter clock for both the optional timeline display and mitigation alerts. It starts automatically at encounter combat entry, stops when combat ends, and every anchor correction updates both the timeline and mitigation callouts. Hiding the timeline never stops its clock or its anchors.
- `/mitplan tl` shows or hides the fight timeline without starting, stopping, or resetting the encounter clock.
- Links each mitigation assignment to a phase-specific timeline event, so every anchor correction moves the mechanic and its mitigation callout together.
- Keeps live mitigation reminders isolated to the active phase instead of continuously correcting the clock.
- Collapses repeated mitigation rows within 15 seconds to the final occurrence in that sequence; Panhaima keeps the first occurrence instead.
- Uses individual effect-duration warning times for catalogued mitigation skills, with a configurable fallback for custom text.
- Configurable alert persistence.
- Entering a configured Savage or Ultimate automatically selects that fight and opens the encounter setup popup.
- `/mitplan` opens the main window; `/mitplan p` opens player setup; `/mitplan start|stop|reset` controls the timer.

Recorder imports include direct links between mitigation assignments and their recorded timeline events. Mitigation assignments remain open to manual editing inside the plugin.
