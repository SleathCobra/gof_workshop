# AEM format notes

The implementation parses little-endian AEM v1 through v5. The local corpus validates v2, v4, and v5; v1 and v3 use independent synthetic fixtures derived from cross-source factual layouts.

## Header

```text
char[9] null-terminated "V4AEMesh\0" or "V5AEMesh\0"
u8      flags
u16     submesh count
```

Version signatures are `AEMesh\0`, `V2AEMesh\0`, `V3AEMesh\0`, `V4AEMesh\0`, and `V5AEMesh\0`. V1/v2 contain one mesh and omit the header submesh count; v3-v5 include it.

Observed flag bits:

| Bit | Meaning |
|---:|---|
| `0x01` | Base mesh present |
| `0x02` | UV float2 per vertex |
| `0x04` | Normal float3 per vertex |
| `0x08` | Auxiliary float4 per vertex; likely color-like, semantics not proven |
| `0x10` | Indexed geometry marker |

## v4/v5 submesh

```text
f32[3] pivot
u16    index count
u16[]  triangle-list indices
u16    vertex count
f32[]  positions (xyz)
f32[]  UVs (uv), if flag 0x02
f32[]  normals (xyz), if flag 0x04
f32[]  auxiliary values (four per vertex), if flag 0x08
f32[4] bounding sphere (x, y, z, radius)
animation record
```

## v1-v3 geometry

- V1 stores a u16 index count and u16 indices, then a u16 strip count and u16 length per strip. The normalized model expands the strips into triangles with alternating winding. Positions are signed i16 source units; UVs and normals divide signed i16 values by 256.
- V2 stores triangle-list indices. Positions are signed 16.16 fixed point, UVs divide by 4096, and normals divide by 32768. A final byte records transparency.
- V3 adds f32 pivots and bounding spheres, multiple submeshes, and the animation record while retaining v2 fixed-point vertex channels.

When the indices flag is absent, sequential draw-array indices are synthesized in the normalized model and reported diagnostically.

## Animation record

Translation, rotation, and scale each begin with a u16 storage selector:

- `0`: three scalar curves; each curve is `u16 count`, then `(f32 time, f32 value)` keys.
- `1`: one vector curve; `u16 count`, then `(f32 time, f32 x, f32 y, f32 z)` keys.

Next is a signed v4 special marker. Marker `2` is followed by a scalar curve. V5 then stores a UV-animation marker; a nonzero value introduces seven scalar curves for UV offset X/Y, scale X/Y, two unresolved channels, and rotation Z. A signed 16-bit padding value ends the record.

Curves and raw animation bytes are preserved. Transform key times are interpreted as milliseconds and converted to seconds. Translation and scale interpolate linearly; Euler-radian rotations are converted to quaternions and interpolate with slerp. The software viewer plays these transform tracks, and glTF writes translation/rotation/scale samplers and channels. UV animation and special/unresolved channels remain preserved but are not played or exported.

## Scene normalization

- Positions, normals, index order, and pivots are retained without lossy source-model mutation.
- UVs in the scene/export layer become `(u, 1-v)` for PNG/glTF-oriented display; raw UVs remain in the AEM model.
- The current scene convention retains source XYZ and declares glTF Y-up. Geometry is localized around a submesh pivot in glTF and that pivot becomes the node translation, permitting non-destructive transform animation about the expected origin. Broader handedness/up-axis conversion remains deferred.
- Face winding is not silently modified. Each scene conversion reports geometric-normal versus stored-normal alignment.

## Safety

Submesh, vertex, index, key, and trailing-data limits are explicit. Every indexed vertex is range-checked. Non-finite floats, truncated arrays, invalid storage selectors, and arithmetic overflow fail with controlled offset/field diagnostics.

`AemWriter.Write` serializes the decoded v1-v5 structure with explicit version-specific numeric encodings, channel/flag validation, bounded counts, animation storage markers, preserved v1 strip indices/lengths, and uninterpreted trailing bytes. Unchanged fixtures for every version and all 752 real corpus files round-trip byte-for-byte. Edited v2-v5 triangle indices and decoded geometry/animation fields are serialized after representability checks; v1 topology edits require a valid preserved strip grouping. `WriteSnapshot` remains available for callers that specifically need the immutable original byte sequence.
