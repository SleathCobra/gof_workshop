# Nondestructive editing foundation

The milestone does not implement operation-log editing, undo/redo, autosave, image re-encoding, or desktop geometry-authoring controls. It now implements the safe copy/staging and serialization boundary those features will use:

- Parsers open source assets read-only and return detached in-memory snapshots.
- Exporters consume snapshots and write only to an explicit output directory.
- No parser model exposes an in-place `Save` operation or stores authority to overwrite its source path.
- Original format identifiers, headers, offsets, ordering, payload data, animation bytes, and uninterpreted tails are preserved where the current layout permits.
- Scene conversion creates a separate normalized representation; it never rewrites the raw AEM model.
- The CLI never writes beneath the selected asset root and defaults generated files to ignored `work/`.
- `Add to Mod` creates a validated user-owned copy under `Assets/Textures` or `Assets/Models`; staged replacements are fully parsed before an atomic copy and audit entry.
- `AeiWriter` reconstructs a container with its preserved metadata and either the original or an explicitly supplied same-length encoded payload.
- `AemWriter.Write` validates and serializes representable v1-v5 geometry, bounds, preserved channels, and animation records; all local corpus files reproduce their original bytes.
- `AemWriter.WriteSnapshot` remains the explicit immutable-source-copy path.

A future editing layer should own three separate values:

1. immutable original snapshot;
2. ordered edit-operation log;
3. derived working snapshot.

Undo/redo should move through the operation log. Autosave and crash recovery should persist operations plus source hashes, not overwrite original game files. Replacement validation should run against a derived snapshot before any packaging or explicit apply step.
