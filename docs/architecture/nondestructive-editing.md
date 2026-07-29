# Nondestructive editing

The texture vertical slice now implements the three-part editing boundary:

1. immutable parsed AEI snapshot and decoded original atlas;
2. ordered region-replacement operation log with an undo/redo cursor;
3. derived working atlas rebuilt from the original plus applied operations.

- Parsers open source assets read-only and return detached in-memory snapshots.
- Exporters consume snapshots and write only to an explicit output directory.
- No parser model exposes an in-place `Save` operation or stores authority to overwrite its source path.
- Original format identifiers, headers, offsets, ordering, payload data, animation bytes, and uninterpreted tails are preserved where the current layout permits.
- Scene conversion creates a separate normalized representation; it never rewrites the raw AEM model.
- The CLI never writes beneath the selected asset root and defaults generated files to ignored `work/`.
- `Add to Mod` creates a validated user-owned copy under `Assets/Textures` or `Assets/Models`; staged replacements are fully parsed before an atomic copy and audit entry.
- `AeiWriter` reconstructs a container with its preserved metadata and either the original or an explicitly supplied same-length encoded payload.
- Raw RGBA and BC1/BC2/BC3 encoding is isolated behind `IAeiPixelEncoder`; the default preserves the source codec and mip count.
- Every writable AEI is reconstructed in memory, reparsed, decoded, dimension/structure checked, and compared before staging is enabled.
- Undo/redo never copies the proprietary source file. Recovery JSON contains the source hash and user replacement pixels, is written atomically under `.work/recovery`, and refuses hash-mismatched replay.
- The Changes activity derives distributable replacements from validated audit records. `Build Mod` verifies current source and staged hashes and omits byte-identical original copies.
- Build output contains a versioned `mod.gof2manifest.json`, only validated user-owned replacements, and a deterministic machine-readable report.
- `AemWriter.Write` validates and serializes representable v1-v5 geometry, bounds, preserved channels, and animation records; all local corpus files reproduce their original bytes.
- `AemWriter.WriteSnapshot` remains the explicit immutable-source-copy path.

Geometry authoring and direct game installation remain outside this boundary.
