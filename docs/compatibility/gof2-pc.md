# Galaxy on Fire 2 PC 1.x corpus

Validated locally on 2026-07-31 with profile `gof2-pc-1x`. The report contains counts only; corpus paths and asset contents remain ignored.

| Result | Count |
| --- | ---: |
| Files inventoried | 2,047 |
| AEI parsed / decoded | 1,228 / 1,228 |
| AEI byte-identical writer round trips | 1,228 / 1,228 |
| AEM parsed / converted | 752 / 752 |
| AEM byte-identical writer round trips | 752 / 752 |
| Unexpected failures | 0 |

AEI identifiers observed: `01` (643), `03` (3), `0D` (1), `10` (8), `12` (9), `20` (55), `22` (56), `24` (140), `26` (283), and `81` (30). The corpus exercises raw RGBA, BC1, BC3, PVRTC, mip chains, and the known PC cube-strip representation.

AEM distribution: v2 (1), v4 (748), v5 (3). All files safely converted to the neutral scene. Write validation here means unchanged parse/write reconstruction only; it is not evidence that every possible authored change is accepted by the game.
