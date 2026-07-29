# Galaxy on Fire 2 Workshop

An open-source, Windows-first modding workbench for **Galaxy on Fire 2**. The repository contains clean-room C# parsers, texture decoding, model export, diagnostic rendering, and the first Avalonia desktop IDE around that proven pipeline.

This project is an independent community effort and is not affiliated with or endorsed by Fishlabs, Deep Silver, or Plaion. Game names and assets remain the property of their respective owners.

## Milestone status

Validated locally against an ignored Galaxy on Fire 2 corpus:

- 1,228/1,228 AEI containers fully parsed.
- 1,228/1,228 AEI textures decoded: raw RGBA, PC cube-map strips, DXT1, DXT5, and PVRTC 2/4bpp, including complete mip chains.
- ETC1, ETC2 RGBA, and ATC RGBA decoding is available behind the same bounded surface-codec abstraction and covered by synthetic block fixtures.
- Atlas PNG, per-region PNG, labeled region overlay, and JSON metadata export.
- 752/752 AEM files fully parsed and converted to the platform-neutral scene model, including the corpus v2 fixed-point mesh and all v4/v5 files.
- AEM v1 triangle strips plus v2/v3 fixed-point geometry are covered by synthetic fixtures; v3-v5 animation records are preserved.
- Animated glTF 2.0 and OBJ/MTL export. Reliable translation/rotation/scale curves play in the desktop viewport and export to glTF.
- Off-screen AEM preview with solid fill, wireframe, normal lines, pivot markers, bounding spheres, and submesh colors.
- Avalonia IDE workbench with separate mod/game explorers, background indexing, quick search, tabbed AEI/AEM documents, Inspector, Output, Problems, and persisted workspace/layout state.
- Interactive AEI atlas pan/zoom, region overlays and selection, surface/mip/face navigation, and PNG/region/overlay export.
- Realtime OpenGL AEM viewport with textured/lit/unlit/diagnostic modes, orbit/pan/zoom, animation playback, picking, isolation, wireframe, normals, pivots, bounds, winding, culling, and controlled software fallback.
- Confidence-bearing AEM-to-AEI material resolution, persisted manual overrides, cached mip uploads, and textured glTF export.
- AEI per-region PNG import with immutable original/working views, undo/redo, atomic recovery, raw/BC1/BC2/BC3 encoding, reparse/decode validation, staging, and revert.
- Changes activity, source-hash conflict detection, versioned mod manifests, and deterministic validated Build Mod output.
- Non-wrapping, horizontally scrollable document tabs, an all-documents picker, back/forward document history, and persisted detachable Explorer, Inspector, and bottom tool windows.
- Validated Add to Mod and replacement staging with an audited operation manifest; loss-preserving AEI and reconstructed AEM v1-v5 writers never overwrite the game root.

See [the anonymized local corpus report](docs/compatibility/local-corpus-report.md) for exact results.

## Requirements

- Windows 10 or newer is the first-class host.
- [.NET SDK 10.0.302](https://dotnet.microsoft.com/) or a compatible .NET 10 feature-band SDK.
- A legally obtained, locally extracted Galaxy on Fire 2 asset folder for integration testing.

The AEI library uses managed `AssetRipper.TextureDecoder` 2.6.2 for PVRTC/ETC/ATC and
`BCnEncoder.Net` 2.3.0 for BC1/BC2/BC3 encoding. Both have MIT-compatible licensing and no native
runtime dependency. The desktop application uses Avalonia 12.1.0; MSTest is test-only.

## Keep proprietary assets local

Place the local compatibility corpus under:

```text
data/
```

The root `.gitignore` excludes `/data/`, `/work/`, `/artifacts/`, and `/resources/`. Never commit original assets, executables, decoded textures, exported models, or golden fixtures derived from game content. The tools never modify files under `data/`.

## Build and test

From the repository root in PowerShell:

```powershell
dotnet restore GalaxyOnFire2Workshop.sln
dotnet build GalaxyOnFire2Workshop.sln --configuration Release --no-restore
dotnet test GalaxyOnFire2Workshop.sln --configuration Release --no-build
```

The synthetic tests do not need game assets. Local corpus tests skip inconclusively when `data/` is absent.

## Technical testbed

Show all commands:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -- help
```

Scan without loading every file into memory:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -- scan data --profile pc-1x --json work/inventory.json
```

Inspect and export an AEI:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -- aei-info data/path/to/texture.aei --profile pc-1x
dotnet run --project src/Gof2Workshop.Testbed -- aei-export data/path/to/texture.aei --output work/texture
```

`aei-export` writes:

```text
work/texture/
  atlas.png
  atlas-regions.png
  metadata.json
  regions/
    region_0000.png
    ...
```

Inspect, export, and preview an AEM:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -- aem-info data/path/to/model.aem --profile pc-1x
dotnet run --project src/Gof2Workshop.Testbed -- aem-export data/path/to/model.aem --format both --output work/model
dotnet run --project src/Gof2Workshop.Testbed -- aem-preview data/path/to/model.aem --output work/model/preview.png
```

Run bounded or full local validation:

```powershell
dotnet run --project src/Gof2Workshop.Testbed -- validate-corpus data --decode --limit 30 --json work/smoke.json
dotnet run --project src/Gof2Workshop.Testbed -- validate-corpus data --decode --json work/full-validation.json
```

Add `--research` to `aei-info` or `aem-info` to include field offsets, lengths, interpreted values, and sections.

## Desktop workbench

Launch the application:

```powershell
dotnet run --configuration Release --project src/Gof2Workshop.App
```

Then:

1. choose **File > New Workspace** and select a mod-owned folder;
2. select the `PC 1.x` or `Android` profile explicitly;
3. choose **File > Select Game Folder**;
4. wait for the nonblocking lightweight scan;
5. double-click an AEI or AEM in **Game Assets**, or use `Ctrl+P`;
6. export copies with `Ctrl+E` or the document toolbar.

Select an AEI region and use **Import Region** for a matching PNG. Undo/redo operates on edit
operations; **Validate** encodes using the preserved codec, reconstructs, reparses, and decodes the
container; only then does **Stage** become available. The Changes activity validates source hashes
and builds a distributable manifest/output without touching the game root.

The original game root is immutable to the application. Export validation refuses the root itself and all descendants. Workspaces contain only configuration, mod-owned assets, generated files, and local state:

```text
MyGof2Mod/
  project.gof2workspace
  Assets/
    Textures/
    Models/
  Generated/
  .work/
```

For a repeatable developer smoke launch:

```powershell
dotnet run --configuration Release --project src/Gof2Workshop.App -- `
  --workspace work/desktop-smoke-workspace/project.gof2workspace `
  --open data/path/to/texture.aei
```

The optional `--asset-root <folder>` argument creates/uses an ignored smoke workspace when no workspace is available. Do not use command-line asset paths in committed scripts or reports.

The Windows-only native-picker smoke launches the real Avalonia application, accepts the native folder/save dialogs, and validates animated glTF, PNG, AEI copy, and reconstructed AEM output:

```powershell
.\scripts\Test-NativeExportPickers.ps1 `
  -Workspace work\desktop-smoke-workspace\project.gof2workspace `
  -AemAsset data\path\to\animated-model.aem `
  -AeiAsset data\path\to\texture.aei
```

## Solution structure

| Project | Responsibility |
|---|---|
| `Gof2Workshop.Core` | Profiles, diagnostics, inventory records, RGBA image buffer |
| `Gof2Workshop.Binary` | Bounds-checked, explicitly endian binary reads and rich parse errors |
| `Gof2Workshop.Formats.Aei` | Loss-preserving AEI model/writer, parser, surface layout, raw/BC/PVRTC/ETC/ATC decoders |
| `Gof2Workshop.Formats.Aem` | AEM v1-v5 model, geometry, bounds, animation parser, structural writer, and immutable snapshot writer |
| `Gof2Workshop.Scene` | Parser-neutral normalized scene representation and winding diagnostics |
| `Gof2Workshop.Export` | PNG, atlas overlay, OBJ, glTF, and software model preview |
| `Gof2Workshop.Workbench` | UI-independent workspace, indexing, search, Problems/Output, document/provider, layout, and path-safety services |
| `Gof2Workshop.App` | Avalonia 12 desktop IDE shell and interactive AEI/AEM documents |
| `Gof2Workshop.Testbed` | Manual-profile CLI, scan, reports, exports, and corpus validation |

Parser projects do not depend on Avalonia or the CLI. The desktop application displays the existing RGBA buffers directly through `WriteableBitmap` and consumes the existing scene/software-renderer boundary.

## Known limitations

- AEI raw RGBA, DXT1/BC1, DXT3/BC2, DXT5/BC3, PVRTC 2/4bpp, ETC1, ETC2 RGBA, and ATC RGBA decoding is implemented. The local corpus exercises raw, DXT1, DXT5, and PVRTC; real Android ETC/ATC samples remain needed for platform-level validation.
- Cube-map face ordering is not confirmed. The observed PC layout is exported as the original vertical six-face strip.
- AEM v1-v3 geometry parsing is implemented from independently validated historical layouts. The local corpus contains one real v2 file; v1/v3 remain synthetic-fixture validated.
- Transform animation uses source milliseconds, linear translation/scale interpolation, and quaternion rotation interpolation. UV and unresolved special channels remain preserved but are not played or exported.
- The source AEM coordinate convention is preserved. Handedness, up-axis, pivot hierarchy, and the semantic name of the optional float4 channel remain under validation.
- OpenGL is the primary desktop model viewport. The adaptive software preview remains available for fallback, deterministic images, and headless validation.
- Pane drag handles detach Explorer, Inspector, and bottom tools into owned windows and persist that state. Arbitrary docking zones/tab groups are not implemented.
- AEI writing supports raw RGBA and BC1/BC2/BC3 source-preserving encoding and same-size region/full-atlas edits. PVRTC/ETC/ATC encoding, atlas resizing, and metadata layout edits are not implemented.
- AEM writing serializes the parsed v1-v5 geometry, bounds, supported channels, and animation records. All 752 unchanged corpus models round-trip byte-for-byte. The workbench does not yet expose geometry-authoring controls; v1 topology edits must retain a representable source strip grouping.

## Research and licensing

- [Research map](docs/research/format-map.md)
- [Clean-room provenance](docs/research/provenance.md)
- [Dependency decisions](docs/research/dependencies.md)
- [AEI notes](docs/formats/aei.md)
- [AEM notes](docs/formats/aem.md)
- [Nondestructive editing foundation](docs/architecture/nondestructive-editing.md)

The project is licensed under the [MIT License](LICENSE). Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting parser or compatibility changes.
