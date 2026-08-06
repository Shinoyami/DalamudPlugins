# MitPlan Recorder

MitPlan Recorder captures enemy casts and resolved abilities during combat. It provides editable phase markers,
phase anchors, independently selectable one-shot mechanic sync anchors, a mitigation column, and CSV column mapping.
Exported mechanic anchors use MitPlan's 20-second chronological matching so repeated action IDs resolve to the
correct occurrence. Every included recorded mechanic is also exported as a phase-specific encounter-timeline
entry, including the action ID used to resync it. Reviewed recordings can be sent to MitPlan over Dalamud IPC or
exported as JSON.

The recorder never performs combat actions. CSV import uses local files only.
