# Initial AEI/AEM research map

This map records binary facts used for the first clean-room C# implementation. It is intentionally concise: reference implementations are treated as behavioral specifications, not source to translate.

## Research-source map

| Area | Primary research material | Useful scope | License posture |
|---|---|---|---|
| AEI container and codec identifiers | AEPi | Header, atlas rectangles, symbol maps, mip flag, codec IDs | Apache-2.0; facts independently checked against local files |
| AEI workflows | AEIporter | Whole-atlas and per-region export behavior | GPL-3.0; behavior only, no code adopted |
| AEI/AEM structural overview | AEMesh README and ImHex patterns | Version signatures and field order | No license file found; facts only, independently checked |
| AEM v1-v5 behavior | AEMesh Noesis/Blender tools | Optional vertex channels, fixed-point variants, v4/v5 animation records | No license file found; no implementation copied |
| J2ME engine behavior | DeepOpen | Older AEM flags, triangle strips, fixed-point UVs | No license file found; corroborating reference only |
| Android behavior | gof2hd-decomp | Engine/runtime context | No repository license found; no code adopted |
| Runtime asset loading | KaamoClubModApi | Asset-loading context only | GPL-3.0; not used in this milestone |
| Package organization | gof2edit and local extracted layout | Naming/layout context | No implementation dependency |

## AEI

### Confirmed by local corpus observations

- All 1,228 files use the eight-byte ASCII signature `AEimage\0`.
- Multi-byte container fields are little-endian.
- The base header is: signature (8), format byte (1), width (u16), height (u16), region count (u16).
- Each atlas region is four u16 values in `(x, y, width, height)` order.
- Compressed variants store a u32 payload length before the payload.
- Raw variants in the corpus store exactly `width * height * 4` payload bytes without a payload-length field.
- The payload is followed by a u16 symbol-map group count. Symbol groups account exactly for the larger tails found in UI atlases.
- Top-level DXT1 payloads use standard 8-byte 4x4 blocks. DXT5 payloads use standard 16-byte 4x4 blocks.
- The `0x02` bit indicates a complete mip chain for the DXT files observed. Payload sizes match the sum of block-rounded levels down to 1x1.
- Raw `0x81` files have `height == width * 6` in the local corpus and are treated as a vertical six-face cube-map strip.

Observed format-byte distribution:

| Raw ID | Count | Initial interpretation |
|---:|---:|---|
| `0x01` | 643 | Raw RGBA UI atlas |
| `0x26` | 283 | DXT5 plus complete mip chain |
| `0x24` | 140 | DXT5, top level only |
| `0x22` | 56 | DXT1 plus complete mip chain |
| `0x20` | 55 | DXT1, top level only |
| `0x81` | 30 | Raw RGBA PC cube-map strip |
| `0x12` | 9 | PVRTC 4bpp plus mip chain |
| `0x10` | 8 | PVRTC 4bpp |
| `0x03` | 3 | Raw RGBA, purpose not yet confirmed |
| `0x0D` | 1 | PVRTC 2bpp with alpha |

### Strong hypotheses

- Raw pixel order is RGBA. This matches AEPi's documented behavior and byte-level inspection; generated previews are used as the independent visual check.
- Atlas origin is top-left. This matches region extraction behavior and is visually checked by overlays.
- `0x81` represents six cube faces rather than an arbitrary tall atlas. The exact face ordering remains unknown.

### Unresolved

- ATC, ETC1/ETC2, DXT3, and Android-specific array layouts do not occur in this corpus. Their decoder adapters are therefore synthetic-fixture validated rather than corpus validated.
- PVRTC payload sizes are consistent with 2bpp/4bpp encodings. All 18 real samples now decode, and a representative RGBA explosion texture was visually inspected.
- Compression-quality footer semantics and any variants that include it need more samples.
- Exact cube-face order, array-element ordering, and every unknown flag are not yet established.

## AEM

### Confirmed by local corpus observations

- Signatures are null-terminated ASCII. The corpus contains one `V2AEMesh`, 748 `V4AEMesh`, and three `V5AEMesh` files.
- Multi-byte values are little-endian.
- All local v4/v5 files use flag `0x17` or `0x1f`.
- For v4/v5, the header is: signature (9), flags (u8), submesh count (u16).
- Per-submesh v4/v5 field order is:
  1. pivot `(x, y, z)` as three f32 values;
  2. index count (u16) and that many u16 indices;
  3. vertex count (u16);
  4. f32 positions, three components per vertex;
  5. optional f32 UVs when flag `0x02` is set;
  6. optional f32 normals when flag `0x04` is set;
  7. optional four-f32 per-vertex channel when flag `0x08` is set;
  8. bounding sphere `(x, y, z, radius)` as four f32 values;
  9. versioned animation records.
- Indices in inspected files are triangle-list indices and their counts are divisible by three.
- Static v4 animation tails observed in small files decode as three transform groups, a v4 special group marker, and two-byte padding.
- v5 files add UV-animation curve groups after the v4 special group.

Observed signature/flag distribution:

| Signature | Flags | Count |
|---|---:|---:|
| `V4AEMesh\0` | `0x17` | 614 |
| `V4AEMesh\0` | `0x1f` | 134 |
| `V5AEMesh\0` | `0x17` | 2 |
| `V5AEMesh\0` | `0x1f` | 1 |
| `V2AEMesh\0` | `0x17` | 1 |

### Confirmed historical variants

- v1 uses one mesh; stored u16 index strips are followed by strip lengths and expand with alternating winding. Positions are signed i16 source units; UVs/normals use 1/256 scaling.
- v2 uses one triangle-list mesh; positions are signed 16.16, UVs use 4.12, and normals use signed 1.15. The real corpus v2 plane independently confirms this layout and final transparency byte.
- v3 adds a submesh count, f32 pivots/bounding spheres, and animation records while retaining v2 fixed-point vertex channels.
- v4/v5 use f32 vertex channels as documented above.

### Coordinate, winding, and UV status

- v4/v5 positions and normals are retained losslessly in parser models.
- The normalized scene currently preserves source XYZ coordinates. This is explicitly marked as the source coordinate convention until handedness and up-axis are validated across representative ships and stations.
- UV normalization for glTF/PNG-backed textures uses `(u, 1-v)`; raw UVs remain available in the AEM model.
- Face winding is diagnosed by comparing geometric triangle normals with averaged stored vertex normals. Winding is not silently reversed.

### Unresolved

- The semantic name of the optional four-f32 channel is not proven. It is provisionally exposed as a color-like auxiliary attribute and preserved.
- Parent relationships are not present/proven. Static geometry is localized around each submesh pivot and the glTF node carries that pivot so transform animation rotates/scales around the preserved pivot.
- Transform animation key times are milliseconds, converted to seconds. Translation/scale use linear interpolation and rotation uses quaternion slerp after the observed Euler-radian conversion.
- UV animation and v4/v5 special channels remain preserved but unresolved; they are not played or exported.
- v1 and v3 have synthetic fixture coverage but no real local sample.
