# Confirmed and candidate reference graph

The dependency graph distinguishes evidence from filename heuristics.

Confirmed by runtime lookup behavior:

- language ordinal -> localized string;
- mission object -> agent, target station, production-good/item and mission type;
- status/save state -> current campaign index and current freelance/campaign mission objects;
- agent -> system/station and generated mission;
- system -> station indices;
- model viewer assignment -> AEI texture is currently viewer/export-only unless an external game-effective reference is confirmed.

Candidate edges requiring binary-record validation:

- ship -> AEM / AEI / attachment positions;
- item or equipment -> icon atlas region;
- station -> model / texture / system;
- mission/dialogue -> language ordinal;
- UI resource -> AEI symbol-map region.

Manual confirmations are stored in workspace state and never presented as embedded AEM semantics.
