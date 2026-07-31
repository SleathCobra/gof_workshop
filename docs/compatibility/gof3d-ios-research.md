# Galaxy on Fire 3D iOS research corpus

This is a separate product profile, `gof3d-ios-research`. No GOF3D layout inference is allowed to change GOF2 behavior implicitly.

| Result | Count |
| --- | ---: |
| Files inventoried | 184 |
| AEI parsed / decoded | 9 / 9 |
| AEI byte-identical writer round trips | 9 / 9 |
| AEM parsed / converted | 115 / 127 |
| AEM byte-identical writer round trips | 115 / 115 |
| Structurally ambiguous AEM | 12 |

The corpus contains legacy `AEMesh` v1 signatures (12) and `V2AEMesh` signatures (115). The current legacy triangle-strip interpretation successfully handles 115 v2 files and reconstructs them byte-for-byte. Twelve records produce impossible strip lengths at stable, bounded offsets. They remain research failures; the parser does not guess an alternate index layout.

The nine AEI files use identifier `01` and raw RGBA. The profile remains research/read-only at the product-policy level even though unchanged in-memory reconstruction succeeds.
