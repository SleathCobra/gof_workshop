# Galaxy on Fire 2 iOS corpus

Validated locally on 2026-07-31 with profile `gof2-ios`.

| Result | Count |
| --- | ---: |
| Files inventoried | 2,435 |
| AEI parsed / decoded | 1,163 / 1,163 |
| AEI byte-identical writer round trips | 1,163 / 1,163 |
| AEM parsed / converted | 1,212 / 1,215 |
| AEM byte-identical writer round trips | 1,212 / 1,212 |
| Recognized unsupported AEM | 1 |
| Structurally invalid AEM | 2 |

AEI identifiers: `01` (702), `03` (2), `0D` (2), `10` (126), `12` (307), `24` (1), `81` (23). PVRTC 2bpp and 4bpp, including mip chains, decode successfully. No corpus-wide vertical or channel rewrite is applied: source orientation remains profile metadata and display alternatives remain diagnostics.

The three AEM exceptions match Android by anonymized structural evidence: one unresolved storage value `9`, one NaN UV, and one NaN scale key. All representable files round-trip byte-for-byte.
