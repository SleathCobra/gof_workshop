# Galaxy on Fire 2 Android corpus

Validated locally on 2026-07-31 with profile `gof2-android`.

| Result | Count |
| --- | ---: |
| Files inventoried | 2,504 |
| AEI parsed / decoded | 1,159 / 1,159 |
| AEI byte-identical writer round trips | 1,159 / 1,159 |
| AEM parsed / converted | 1,212 / 1,215 |
| AEM byte-identical writer round trips | 1,212 / 1,212 |
| Recognized unsupported AEM | 1 |
| Structurally invalid AEM | 2 |

AEI identifiers: `01` (678), `03` (231), `0D` (1), `10` (8), `12` (9), `24` (1), `42` (208), `C2` (23). ETC/ATC-family payloads decode through the maintained `AssetRipper.TextureDecoder` adapter; unchanged containers reconstruct byte-for-byte.

AEM versions v2-v5 occur. One v5 record uses unresolved animation storage value `9`; it is reported as recognized/unsupported. Two v5 records contain non-finite values (one UV and one scale key) and are rejected as structurally invalid rather than normalized silently. A v2 variant without the normally present trailing transparency byte is now preserved exactly.
