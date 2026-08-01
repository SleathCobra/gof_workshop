# Public synthetic demonstration corpus

These assets are generated from original geometric and pixel patterns by:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -- generate-synthetic
```

They are not derived from Galaxy on Fire 2 or any proprietary game data. The generated AEI/AEM
fixtures, workspace, operation example, and manifest are dedicated to the public domain under
CC0-1.0; the generator source is covered by the repository MIT license.

The set includes raw, BC1, BC2-alpha, BC3-alpha, overlapping-region, and mipmapped AEI atlases;
AEM v1-v5 geometry, a textured cube, a two-submesh spacecraft-like mesh, and a transform-animated
mesh; glTF and OBJ import fixtures; a synthetic language table; matching material names; and
sample workspace/mod metadata.

`Assets/Data` also contains independently generated small BIN fixtures for every registered family:
names, items, ships, systems, stations, agents, wanted targets, ticker/news, ship/station parts,
weapon positions, collision, docking, and platform weapon tables. Confirmed fields have known values;
opaque families deliberately contain recognizable unknown-byte patterns. Tests assert expected
classification, byte-identical unchanged output, safe edits, malformed-input behavior, and source-hash
recovery. These fixtures are CC0 and contain no game names, records, or artwork.

The browser embeds only the smallest AEM/AEI/BIN/glTF examples. They drive the real-browser WebGL,
AEI editing, structured-data, persistence, and authoring smoke scenarios without selecting proprietary
data.
