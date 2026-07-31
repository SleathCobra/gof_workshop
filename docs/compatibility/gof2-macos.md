# Galaxy on Fire 2 macOS corpus

Validated locally on 2026-07-31 with profile `gof2-macos`.

| Result | Count |
| --- | ---: |
| Files inventoried | 2,848 |
| AEI parsed / decoded | 1,586 / 1,586 |
| AEI byte-identical writer round trips | 1,586 / 1,586 |
| AEM parsed / converted | 1,202 / 1,205 |
| AEM byte-identical writer round trips | 1,202 / 1,202 |
| Recognized unsupported AEM | 1 |
| Structurally invalid AEM | 2 |

AEI identifiers: `01` (669), `03` (3), `0D` (1), `10` (8), `12` (10), `20` (55), `22` (56), `24` (224), `26` (514), `81` (42), `A6` (4).

Identifier `A6` is newly confirmed by byte accounting as an uncompressed RGBA cube-strip form: all four samples are 64 x 384 and payload length equals width x height x 4 after container metadata. It must not inherit the ordinary mip flag interpretation. The three AEM exceptions have the same controlled classifications as the Android/iOS corpora.

Format compatibility is separate from native viewport validation. Parsing and software rendering were validated on Windows; hardware rendering still requires real-Mac driver/context evidence.
