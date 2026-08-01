# Structured-data source map

| Family | Corpus evidence | Research evidence | Workshop status |
|---|---|---|---|
| Language | PC/Android `.lang` files | Runtime text lookups use ordinal numeric IDs | Read/write editor; strict UTF-8 and exact round trip validated |
| Items/equipment | `items.bin`, `shipparts.bin`, `weapons*.bin` | DeepOpen and GOF2HD classes name fields and relationships | Items/parts structural editor; platform weapon tables loss-preserving raw |
| Ships | `ships.bin`, attachment-position tables | Engine `Ship`/geometry loading behavior | Structural read/write editor; semantic labels remain conservative |
| Systems/stations | `systems.bin`, `stations.bin`, `stationparts.bin`, docks | Galaxy construction and index lookup behavior | Systems/stations/parts structural editor; docks loss-preserving raw |
| Agents/wanted | `agents.bin`, mobile `wanted.bin` | Mission generator selects agent, station, system and race by integer IDs | Structural read/write editor; reference semantics unresolved |
| News | `ticker.bin` | Text lookup behavior | Structural read/write editor; flag meanings unresolved |
| Saves | No stable standalone sample was put in the compatibility corpora | GOF2HD `GameRecord` reconstruction | Runtime structure evidence only |
| Missions/dialogue | No standalone mission records found | Mission objects, procedural generator, campaign `LevelScript`, and dialogue lookup tables are implemented in engine code | Read-only research; creation is not write-safe |

Research repositories are factual and behavioral references only. Their code is neither compiled nor mechanically translated. See `docs/research/provenance.md`.
