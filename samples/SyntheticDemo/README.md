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
