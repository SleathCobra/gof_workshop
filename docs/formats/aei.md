# AEI format notes

All offsets are relative to the start of the file. The implemented variants are little-endian.

## Container

```text
char[8]  "AEimage\0"
u8       raw format identifier
u16      atlas width
u16      atlas height
u16      atlas-region count
repeat region count:
  u16 x, y, width, height
if compressed:
  u32 payload byte length
payload
u16 symbol-map group count
repeat symbol-map group count:
  u16 glyph count
  u16[glyph count] UTF-16LE code units
  repeat glyph count:
    u16 x, y, width, height
optional one-byte compression quality
optional uninterpreted trailing bytes
```

Raw variants omit the u32 payload length; their corpus payload is `width * height * 4` RGBA bytes.

## Format IDs

| Base ID | Meaning | Milestone decode |
|---:|---|---|
| `0x01` | Raw RGBA UI | Yes |
| `0x03` | Raw RGBA | Yes |
| `0x81` | Raw RGBA PC cube-map strip | Yes |
| `0xc2` | Raw RGBA cube map | Yes, layout-dependent |
| `0x0d` | PVRTC 2bpp RGBA | Yes |
| `0x10` | PVRTC 4bpp RGBA | Yes |
| `0x11` | ATC RGBA8 | Yes |
| `0x17` | ETC2 RGBA8 | Yes |
| `0x20` | DXT1 / BC1 | Yes |
| `0x21` | DXT3 / BC2 | Yes |
| `0x24` | DXT5 / BC3 | Yes |
| `0x40` | ETC1 | Yes |

For IDs that do not already denote an exact base format, bit `0x02` marks a complete mip chain. Each BC level uses block-rounded dimensions; levels continue through 1x1. PVRTC levels apply their minimum encoded dimensions, which exactly accounts for the observed payload sizes.

## Preservation and safety

The model retains raw format flags, raw header bytes, original region ordering/offsets, overlapping regions, complete payload bytes, surface offsets, symbol order, compression quality, and unknown trailing data.

Dimensions, region tables, payload lengths, symbol counts, offsets, and allocations are checked before reads. Out-of-atlas regions are preserved with warnings rather than silently clipped in the parser.

Raw and BC1/2/3 decoders are independent Workshop implementations. PVRTC/ETC/ATC are dispatched through a bounded adapter to the managed MIT-licensed `AssetRipper.TextureDecoder`; container traversal, mip/face selection, payload length validation, cancellation, and RGBA ownership remain in the Workshop.

`AeiWriter` reconstructs the preserved container and can accept an explicitly supplied same-length
encoded payload. `IAeiPixelEncoder` now produces raw RGBA or BC1/BC2/BC3 blocks, regenerates the
existing primary mip chain with a bounded box filter, and leaves unrelated faces/array elements
untouched. The editor constrains PNG region imports to matching dimensions and never changes the
atlas rectangle table, ordering, symbol maps, codec, face order, or unknown trailing data.

After encoding, the Workshop writes to memory, reparses the complete AEI, decodes mip zero, checks
dimensions and surface layout, and calculates absolute/maximum channel error. Staging stays disabled
unless that validation succeeds. PVRTC/ETC/ATC encoding and atlas-layout resizing remain unsupported.
