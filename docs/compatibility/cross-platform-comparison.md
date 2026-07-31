# Cross-platform corpus comparison

Generated from anonymized filename/signature/hash inventory on 2026-07-31. “Structurally similar” means matching lightweight container descriptors, not byte identity or semantic interchangeability.

| Pair | Shared names | Byte-identical | Similar AEI | Similar AEM |
| --- | ---: | ---: | ---: | ---: |
| PC / Android | 2,025 | 1,431 | 640 | 752 |
| PC / iOS | 2,000 | 1,421 | 654 | 752 |
| PC / macOS | 2,004 | 1,967 | 1,203 | 752 |
| Android / iOS | 2,429 | 1,967 | 698 | 1,215 |
| Android / macOS | 2,404 | 1,921 | 666 | 1,203 |
| iOS / macOS | 2,404 | 1,941 | 685 | 1,203 |

Confirmed:

- GOF2 AEM naming and lightweight structure are strongly shared between mobile platforms.
- Texture containers vary materially by platform codec even when asset names correspond.
- macOS carries four `A6` raw cube-strip textures not present under that identifier in the other corpora.
- GOF3D has essentially no name-level overlap and remains isolated.

Unresolved:

- Cube face order and complete orientation semantics are not established by filename/hash comparison.
- One AEM animation storage value (`9`) remains uninterpreted.
- Candidate database counts are intentionally broad extension/name heuristics; they do not establish record semantics.
- One anonymized mission-name candidate recurs in each GOF2 corpus. Its contents have not yet been promoted to a confirmed mission format.
