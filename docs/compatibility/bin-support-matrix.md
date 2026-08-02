# Generated GOF2 BIN support matrix

> Generated from local ignored corpora. It contains filenames and aggregate structure facts, never proprietary bytes or absolute paths.

Generated: 2026-08-01T22:13:24.7571823+00:00  
Files: 136; parsed: 136; unchanged byte-identical: 136; controlled edited round trips: 136.  
Elapsed: 405 ms.

| Family | Platforms | Files | Support | Unchanged | Edited | Editor | Creation | Confidence | Blockers |
|---|---|---:|---|---:|---:|---|---|---|---|
| Names | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 52 | SemanticReadWrite | 52/52 | 52/52 | Semantic grid/form + raw research view | Disabled | Confirmed engine consumer and repeated corpus layout | Record creation is disabled pending name-pool capacity validation. |
| ItemsAndBlueprints | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | StructuralReadWrite | 4/4 | 4/4 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | Individual attribute indices remain unresolved. |
| Ships | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | SemanticReadWrite | 4/4 | 4/4 | Semantic grid/form + raw research view | Disabled | Confirmed engine consumer and repeated corpus layout | New ship capacity and model registration are executable-dependent. |
| SystemsAndConnections | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | StructuralReadWrite | 4/4 | 4/4 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | LegacyOrStaticIds semantics remain unresolved. |
| Stations | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | SemanticReadWrite | 4/4 | 4/4 | Semantic grid/form + raw research view | Disabled | Confirmed engine consumer and repeated corpus layout | New-station capacity is not in-game validated. |
| Agents | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | StructuralReadWrite | 4/4 | 4/4 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | MobileVariantParameter semantics remain unresolved. |
| WantedTargets | gof2-android, gof2-ios, gof2-macos | 3 | SemanticReadWrite | 3/3 | 3/3 | Semantic grid/form + raw research view | Disabled | Confirmed engine consumer and repeated corpus layout | Encounter spawning remains native runtime behavior. |
| NewsTicker | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | SemanticReadWrite | 4/4 | 4/4 | Semantic grid/form + raw research view | Disabled | Confirmed engine consumer and repeated corpus layout | Condition flag meanings are only structurally enumerated. |
| ShipParts | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | StructuralReadWrite | 4/4 | 4/4 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | Resource-ID-to-path map is not encoded in this table. |
| StationParts | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 4 | StructuralReadWrite | 4/4 | 4/4 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | Resource-ID-to-path map is not encoded in this table. |
| CollisionGeometry | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 16 | StructuralReadWrite | 16/16 | 16/16 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | Source coordinate scale/axis semantics remain profile-dependent. |
| DockingPoints | gof2-android, gof2-ios, gof2-macos | 9 | StructuralReadWrite | 9/9 | 9/9 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | Final auxiliary float3 gameplay meaning remains unresolved. |
| WeaponPositions | gof2-android, gof2-ios, gof2-macos, gof2-pc-1x | 24 | StructuralReadWrite | 24/24 | 24/24 | Typed structural grid/form + raw research view | Disabled | Confirmed record layout; unresolved fields remain explicitly labeled | Complete point-type enum is not confirmed. |

## Names

Files: `names_bobolan_0.bin`, `names_bobolan_1.bin`, `names_cyborg_0.bin`, `names_grey_0.bin`, `names_multipod_0.bin`, `names_multipod_1.bin`, `names_nivelian_0.bin`, `names_nivelian_1.bin`, `names_terran_0_m.bin`, `names_terran_0_w.bin`, `names_terran_1.bin`, `names_vossk_0.bin`, `names_vossk_1.bin`
Signature/detection: names_*.bin filename consumer
Header: BE int32 count
Records: Java modified UTF-8 strings
Known fields: `Name`
Unknown fields: None currently exposed.
References: No confirmed foreign-key semantics.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## ItemsAndBlueprints

Files: `items.bin`
Signature/detection: items.bin filename consumer
Header: No global header
Records: Three BE int32 counted arrays per record
Known fields: `Attributes[]`, `BlueprintComponents[]`, `ComponentAmounts[]`
Unknown fields: None currently exposed.
References: Blueprint component IDs refer to item records.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## Ships

Files: `ships.bin`
Signature/detection: ships.bin filename consumer
Header: No global header
Records: Nine BE int32 values per record
Known fields: `BaseHitpoints`, `BaseLoad`, `BaseValue`, `Handling`, `ShipId`, `SlotType0`, `SlotType1`, `SlotType2`, `SlotType3`
Unknown fields: None currently exposed.
References: Ship IDs are referenced by wanted and other runtime records.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## SystemsAndConnections

Files: `systems.bin`
Signature/detection: systems.bin filename consumer
Header: No global header
Records: Modified UTF name, eight BE int32 fields, four counted arrays
Known fields: `FactionOrRace`, `JumpgateStationId`, `Name`, `NeighbourSystemIds[]`, `PositionX`, `PositionY`, `PositionZ`, `Safety`, `StarColor[]`, `StarTextureId`, `StationIds[]`, `VisibleByDefault`
Unknown fields: `LegacyOrStaticIds[]`
References: Station IDs and neighbour system IDs are encoded.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## Stations

Files: `stations.bin`
Signature/detection: stations.bin filename consumer
Header: No global header
Records: Modified UTF name plus four BE int32 fields
Known fields: `Name`, `PlanetTextureId`, `StationId`, `SystemId`, `TechnologyLevel`
Unknown fields: None currently exposed.
References: SystemId is an encoded system reference.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## Agents

Files: `agents.bin`
Signature/detection: agents.bin filename consumer
Header: No global header
Records: Variant-sensitive modified UTF and BE scalar records
Known fields: `BlueprintId`, `FaceParts`, `MaleFlag`, `MessageId`, `Name`, `Race`, `SecretSystemId`, `SellPrice`, `StationId`, `SystemId`
Unknown fields: `MobileVariantParameter`
References: Station, system and blueprint IDs are encoded.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## WantedTargets

Files: `wanted.bin`
Signature/detection: wanted.bin filename consumer
Header: No global header
Records: Modified UTF, thirteen BE int32 fields, optional five face bytes
Known fields: `BoardId`, `Hitpoints`, `Id`, `LootAmount`, `LootItemId`, `MaleFlag`, `Name`, `RaceId`, `RequiredBounties`, `RequiredMissionId`, `Reward`, `ShipId`, `WeaponId`
Unknown fields: `FaceParts`
References: Ship, item and native campaign-step references are encoded.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## NewsTicker

Files: `ticker.bin`
Signature/detection: ticker.bin filename consumer
Header: No global header
Records: Seven BE int32 fields per record
Known fields: `Active`, `ConditionFlag0`, `ConditionFlag1`, `ConditionFlag2`, `ConditionFlag3`, `MaximumLevel`, `MinimumLevel`
Unknown fields: None currently exposed.
References: No confirmed foreign-key semantics.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## ShipParts

Files: `shipparts.bin`
Signature/detection: shipparts.bin filename consumer
Header: No global header
Records: Group/count byte prefix and fixed mixed-width transforms
Known fields: `GroupId`, `Part[].PositionX`, `Part[].PositionY`, `Part[].PositionZ`, `Part[].ResourceId`, `Part[].RotationX`, `Part[].RotationY`, `Part[].RotationZ`, `Part[].ScaleX`, `Part[].ScaleY`, `Part[].ScaleZ`
Unknown fields: None currently exposed.
References: Resource IDs refer to model resources.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## StationParts

Files: `stationparts.bin`
Signature/detection: stationparts.bin filename consumer
Header: No global header
Records: Group/hangar/count prefix and fixed mixed-width transforms
Known fields: `GroupId`, `HangarResourceId`, `Part[].PositionX`, `Part[].PositionY`, `Part[].PositionZ`, `Part[].ResourceId`, `Part[].RotationX`, `Part[].RotationY`, `Part[].RotationZ`
Unknown fields: None currently exposed.
References: Hangar/resource IDs refer to model resources.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## CollisionGeometry

Files: `collision_test.bin`, `collision.bin`, `static_collisions.bin`, `v_collisions.bin`, `wreck_collisions.bin`
Signature/detection: collision*.bin filename consumers
Header: LE owner and payload-word count-minus-one
Records: LE shape count followed by sphere or AABB records
Known fields: `OwnerId`, `Shape[].CenterX`, `Shape[].CenterY`, `Shape[].CenterZ`, `Shape[].HalfExtentX`, `Shape[].HalfExtentY`, `Shape[].HalfExtentZ`, `Shape[].Radius`, `Shape[].Type`
Unknown fields: None currently exposed.
References: Owner IDs correlate with model/resource consumers.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## DockingPoints

Files: `docks_hd.bin`, `docks.bin`, `sn_docking_points_battlestation.bin`, `sn_docking_points_cargo_wrecks.bin`, `sn_docking_points_carrier_terran.bin`
Signature/detection: docks*.bin and *_docking_points*.bin consumers
Header: LE int16 owner and count
Records: 38-byte typed position/rotation/auxiliary records
Known fields: `OwnerId`, `Point[].PositionX`, `Point[].PositionY`, `Point[].PositionZ`, `Point[].RotationX`, `Point[].RotationY`, `Point[].RotationZ`, `Point[].Type`
Unknown fields: `Point[].Auxiliary0`, `Point[].Auxiliary1`, `Point[].Auxiliary2`
References: Owner IDs correlate with station/model resources.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

## WeaponPositions

Files: `sn_ship_044_elite_nivelian_weapons.bin`, `sn_ship_045_most_wanted_weapons.bin`, `sn_ship_046_most_wanted_weapons.bin`, `sn_ship_047_most_wanted_weapons.bin`, `sn_ship_048_most_wanted_weapons.bin`, `sn_ship_049_boss_nivelian_weapons.bin`, `sn_ship_051_dropship_terran_weapons.bin`, `sn_ship_052_retro_weapons.bin`, `sn_ship_054_vossk_weapons.bin`, `sn_ship_055_modified_weapons.bin`, `sn_ship_056_modified_weapons.bin`, `sn_ship_057_modified_weapons.bin`, `sn_ship_058_modified_weapons.bin`, `sn_ship_059_modified_weapons.bin`, `sn_ship_060_modified_weapons.bin`, `v_ships_deep_science_weapons.bin`, `v_ships_vossk_weapons.bin`, `weapons_hd.bin`, `weapons_sd.bin`, `weapons.bin`
Signature/detection: weapons*.bin and *_weapons.bin consumers
Header: LE int16 owner and count
Records: LE int16 type/position with optional direction float3
Known fields: `OwnerId`, `Point[].DirectionX`, `Point[].DirectionY`, `Point[].DirectionZ`, `Point[].Type`, `Point[].X`, `Point[].Y`, `Point[].Z`
Unknown fields: None currently exposed.
References: Owner IDs correlate with ship/model resources.
Real-game status: Writer/reparse validated locally; in-game mutation has not been performed by this command.

