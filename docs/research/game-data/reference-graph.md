# Confirmed and candidate reference graph

The implemented `DependencyGraph` is one platform-neutral, incremental service used by BIN editors,
asset documents, Mission Explorer, material mappings, Problems, and Build Mod. Each node identity
contains its product/profile plus family/path and stable record or subresource identity. Producers
replace their own scopes, so a material edit does not rebuild every parsed asset.

## Encoded and runtime-confirmed edges

- BIN file -> contained record;
- AEI -> encoded atlas region;
- AEM -> encoded submesh;
- locale file -> ordinal language entry;
- agent `MessageId` -> ordinal language entry;
- agent/station fields -> system and station records;
- system station/neighbour arrays -> station/system records;
- wanted ship/weapon/loot fields -> ships/items;
- mission evidence -> wanted record and native/runtime handlers;
- campaign evidence -> persisted campaign step, native construction, LevelScript state, and native
  progression handlers.

Repeated physical-layout `OwnerId` values are not record identities. Collision, docking, and
weapon-position nodes use stable record indices, while confirmed unique wanted/ship/station IDs are
retained. This distinction was validated against all mobile corpora and has a synthetic regression
test.

## Material and asset evidence

Exact/normalized AEM-to-AEI filename matches are candidate edges. A workspace material assignment
adds a separate workspace-override node and a `ConfirmedByUser` mapping to the selected AEI. Its
evidence explicitly says viewer/export-only; no mapping is represented as an embedded or
game-effective AEM field.

Candidate edges still requiring external reference proof include:

- ship/station/resource ID -> exact AEM or attachment group;
- item/equipment -> icon atlas region;
- UI resource -> AEI symbol-map region;
- resource IDs in part tables -> exact model path.

## Validation and UI

Missing encoded targets become explicit broken nodes/edges. Unreadable AEM/AEI/BIN metadata is
isolated as a per-asset broken edge rather than aborting construction. The Dependencies activity
shows uses/referenced-by/evidence and opens a bounded graph document; user confirmations/rejections
remain workspace facts. Build Mod reports broken or unresolved outgoing references for changed
nodes.

The 2026-08-01 PC run built 11,418 corpus nodes and 43,563 edges in 2.56 seconds after a 115 ms
index. Mission contribution raised the live graph to 11,440 nodes and 43,577 edges. See the
[validation report](../../compatibility/semantic-data-mission-authoring-report.md).
