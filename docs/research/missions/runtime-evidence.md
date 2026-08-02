# Mission runtime evidence

Evidence sources reviewed:

- DeepOpen `Mission`, `Objective`, `Dialogue` and `LevelScript` classes for older/J2ME observable behavior;
- GOF2HD `Mission`, `Generator`, `Level`, `LevelScript`, `GameRecord` and menu/status paths for Android native behavior;
- KaamoClubModApi only as evidence that PC custom missions require runtime hooks and an auxiliary representation, not as implementation source.

Independent corpus checks found no matching declarative mission container. The agreement between two engine reconstructions and the absent corpus file strongly supports a mixed runtime model: procedural freelance mission generation plus hard-coded campaign scripting.

License boundary: none of these sources is compiled, copied or mechanically translated into the MIT project.

## Implemented research projection

`MissionEvidenceService` turns the independently corroborated facts into a versioned, read-only
research document. It currently records seven runtime identities:

- `LevelScript.process`, the campaign-state dispatcher;
- `Level.createCampaignMission`, the native campaign constructor;
- `Generator.createFreelanceMission`, the procedural job generator;
- `Dialogue.campaignTables`, compiled campaign-to-language mappings;
- `Status.currentCampaignMission`, the persisted campaign-step value;
- `Objective.achieved`, the live objective evaluator;
- `Status.nextCampaignMission`, native campaign progression.

These are research identities, not callable Workshop commands or executable addresses. Their graph
edges are classified as runtime-confirmed evidence and remain non-writable.

The supplied Android, iOS and macOS corpora each contain 25 typed `wanted.bin` records. Those records
contribute mission-adjacent bounty evidence plus encoded ship, weapon/item, loot and campaign-step
references. The Workshop does not infer spawn logic from those table fields.

The Mission Explorer can compare two equal-length, user-selected save snapshots and reports only
contiguous changed byte ranges. It neither persists the save bytes nor attaches semantics to a
difference automatically.
