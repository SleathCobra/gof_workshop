# Structured game-data corpus inventory

Validated 2026-07-31. Counts are from ignored local corpora; no content or private absolute paths is committed.

| Profile | Binary tables | Language tables | Text tables | Confirmed families |
|---|---:|---:|---:|---|
| GOF2 PC 1.x | 25 `.bin` | 11 `.lang` | 0 | agents, items, ships, ship/station parts, stations, systems, weapons, ticker, generated names, collision data |
| GOF2 Android | 51 `.bin` | 11 `.lang` | 0 | PC families plus docks, wanted targets, HD/SD weapons, attachment positions, additional collision data |
| GOF2 iOS | 30 `.bin` | 0 in this extraction | 0 | same main mobile database families |
| GOF2 macOS | 30 `.bin` | 0 in this extraction | 0 | same main mobile database families |
| GOF3D iOS research | 0 | 0 | 17 `.txt` | items, ships, stations, systems, names and other older text tables |

The single filename-based “mission candidate” in each GOF2 inventory is a false-positive asset name, not a mission database. No standalone mission table was found by extension, filename, header, or cross-corpus correspondence.

The first write-safe format is GOF2 `.lang`: a repetition of big-endian `UInt16` UTF-8 byte length followed by that many bytes, continuing to EOF. All 22 locally available PC/Android tables reconstruct byte-for-byte. Other database families remain candidates until their count, record boundaries, references and unknown fields are independently validated.
