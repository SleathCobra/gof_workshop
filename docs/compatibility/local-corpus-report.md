# Local corpus compatibility report

Generated from the ignored local corpus on 2026-07-29 with:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- scan data --profile pc-1x --json work/final-upgrade-inventory.json
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- validate-corpus data --decode --json work/final-upgrade-validation.json
```

No proprietary files, paths, hashes, decoded images, or model exports are included in this report.

## Summary

| Measure | Result |
|---|---:|
| AEI files discovered | 1,228 |
| AEI header parses | 1,228 |
| AEI full parses | 1,228 |
| AEI texture decodes | 1,228 |
| AEI recognized but decoder unavailable | 0 |
| AEI corrupt/safety failures | 0 |
| AEM files discovered | 752 |
| AEM v1-v5 full parses | 752 |
| AEM scene conversions | 752 |
| AEM recognized unsupported versions | 0 |
| AEM corrupt/safety failures | 0 |
| Representative glTF exports run | v2 static and v4 animated |
| Representative OBJ/MTL exports run | v2 and v4 |
| Representative off-screen model previews run | v2 and v4 |
| Uncontrolled crashes | 0 |

## AEI distribution

| Variant | Count | Full parse | Decode |
|---|---:|---:|---:|
| Raw RGBA UI `0x01` | 643 | 643 | 643 |
| Raw RGBA `0x03` | 3 | 3 | 3 |
| Raw RGBA PC cube-map strip `0x81` | 30 | 30 | 30 |
| DXT1 `0x20` | 55 | 55 | 55 |
| DXT1 + mips `0x22` | 56 | 56 | 56 |
| DXT5 `0x24` | 140 | 140 | 140 |
| DXT5 + mips `0x26` | 283 | 283 | 283 |
| PVRTC 2bpp `0x0d` | 1 | 1 | 1 |
| PVRTC 4bpp `0x10` | 8 | 8 | 8 |
| PVRTC 4bpp + mips `0x12` | 9 | 9 | 9 |

A 1024x512 raw atlas with 261 regions produced a visually correct full PNG, 261 region PNGs, and a labeled overlay. A separate DXT5 texture and a PVRTC 4bpp explosion texture produced visually correct decoded PNGs.

## AEM distribution

| Variant | Count | Full parse / scene |
|---|---:|---:|
| v4 flags `0x17` | 614 | 614 |
| v4 flags `0x1f` | 134 | 134 |
| v5 flags `0x17` | 2 | 2 |
| v5 flags `0x1f` | 1 | 1 |
| v2 flags `0x17` | 1 | 1 |

The representative v4 ship contained 12,198 vertices and 4,066 triangles. It exported to `.gltf` + `.bin` and `.obj` + `.mtl`, and rendered to a 1024x1024 PNG with all 4,066 triangles plus 2,440 sampled normal lines, wireframe, pivot, and bounding sphere.

The real v2 sample decoded as one four-vertex, four-triangle fixed-point plane. It rendered with bounds/pivot/normals and exported to glTF+BIN and OBJ+MTL. An animated v4 scene exported two meshes plus one glTF animation containing three transform channels.

The structural AEM writer serialized all 752 parsed v2/v4/v5 corpus models in memory and reproduced every original byte sequence. Synthetic v1/v3 fixtures provide the corresponding version coverage, including preserved v1 triangle-strip groups.

## Known warnings and gaps

- Profile warnings remain expected for the 18 PVRTC assets found beside the PC-oriented corpus, but all now decode successfully.
- ETC1, ETC2, and ATC decode paths are synthetic-fixture validated because those codecs do not occur in this corpus.
- V1/v3 AEM geometry is synthetic-fixture validated because those versions do not occur in this corpus.
- Transform animations play/export; UV and unresolved special channels remain preservation-only.
- Exact cube face ordering, source coordinate convention, pivot hierarchy, and auxiliary float4 semantics remain unresolved.
