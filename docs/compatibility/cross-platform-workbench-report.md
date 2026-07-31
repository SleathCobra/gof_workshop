# Cross-platform asset IDE continuation report

Validated 2026-07-31 with .NET 10 and Avalonia 12.1.0. Proprietary corpora and all generated
derivatives remained under ignored directories.

## Baseline and final quality gates

| Gate | Baseline | Final |
| --- | ---: | ---: |
| Release build | 0 warnings / 0 errors, 7.98 s | 0 warnings / 0 errors, 26.81 s including WebAssembly native link |
| Automated tests | 62 passed, 7.95 s | 81 passed, 8.24 s |
| PC full corpus | 1,228 AEI + 752 AEM, no crash, 12.14 s | unchanged; byte-identical writer validation added |
| NuGet audit | no known vulnerability | no known vulnerability |

The baseline desktop launched and responded at 498.9 MiB working set / 416.3 MiB private bytes.
The later restored-workspace smoke reached 933.1 MiB / 851.8 MiB while retaining many restored
documents. This is not a controlled leak measurement; document-close profiling remains required.

## Corpus compatibility

`AEI` columns are discovered / parsed / decoded. `AEM` columns are discovered / parsed /
scene-converted. Writer results are unchanged reconstruction attempts / byte-identical / failed.

| Profile | Inventory files | AEI | AEI writer | AEM | AEM writer | Controlled AEM exceptions | Lightweight index |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `gof2-pc-1x` | 2,047 | 1,228 / 1,228 / 1,228 | 1,228 / 1,228 / 0 | 752 / 752 / 752 | 752 / 752 / 0 | 0 | 0.824 s |
| `gof2-android` | 2,504 | 1,159 / 1,159 / 1,159 | 1,159 / 1,159 / 0 | 1,215 / 1,212 / 1,212 | 1,212 / 1,212 / 0 | 1 unsupported + 2 non-finite | 0.886 s |
| `gof2-ios` | 2,435 | 1,163 / 1,163 / 1,163 | 1,163 / 1,163 / 0 | 1,215 / 1,212 / 1,212 | 1,212 / 1,212 / 0 | 1 unsupported + 2 non-finite | 0.845 s |
| `gof2-macos` | 2,848 | 1,586 / 1,586 / 1,586 | 1,586 / 1,586 / 0 | 1,205 / 1,202 / 1,202 | 1,202 / 1,202 / 0 | 1 unsupported + 2 non-finite | 0.874 s |
| `gof3d-ios-research` | 184 | 9 / 9 / 9 | 9 / 9 / 0 | 127 / 115 / 115 | 115 / 115 / 0 | 12 ambiguous legacy strips | 0.768 s |

The three GOF2 mobile/macOS AEM exceptions correspond across corpora: one recognized animation
storage selector `9` and two files containing non-finite geometry. GOF3D remains a separate product
profile; its twelve legacy strip layouts are not interpreted using GOF2 assumptions.

## Implemented continuation

- Explicit product/platform profiles and anonymized corpus/cross-platform reports.
- Workspace-free Quick Inspect for files, multiple files, folders, drag/drop and command-line paths;
  temporary relationships and explicit copy-to-workspace are bounded and read-only by default.
- Static browser-local Avalonia host with browser-authorized multi-file input, drag/drop, AEI decode,
  AEM parse, filename-related textures, bounded textured software preview, PNG download and small
  local settings. No source bytes are uploaded or persisted by default.
- Portable macOS GL shader selection (ES 3.00, desktop core 1.50, legacy 1.20), core-profile VAO
  enforcement, layout rebinding, diagnostics, and software fallback.
- Generic relationship edges that distinguish viewer-only, export, heuristic game, and confirmed
  game-effective mappings. Current AEM texture inference remains heuristic.
- Bounded glTF/GLB and OBJ/MTL import into a neutral scene, then validated PC AEM v4/v5 authoring,
  reparse, scene conversion, statistic comparison and software preview.
- Animation curve/key inspector; confirmed transform keys are editable in mod-owned AEM documents
  with undo/redo and writer/reparse validation. Unresolved channels remain preserved/read-only.
- First structured editor for confirmed `.lang` framing, including immutable originals, operation
  log, undo/redo, source-hash recovery guard, exact writer validation and safe copy export.
- Expanded CC0 synthetic corpus: six AEI variants, AEM v1-v5, animated and multi-submesh examples,
  language data, glTF/OBJ import sources, sample workspace and sample mod metadata.
- Restartable tutorial overlay with Quick Inspect, texture, model import, animation and structured
  language tracks; progress persists in application state.
- GitHub Actions now validates WebAssembly and publishes nightly Windows x64, Linux x64, macOS x64,
  macOS arm64 and browser artifacts on pushes.

## Browser result

The clean trimmed publish contains 170 files / 30,998,519 bytes (29.56 MiB). Build, native link and
static artifact composition passed. The available in-app browser control failed during its own
bootstrap (`Cannot redefine property: process`), before a browser session existed, so visual browser
startup/drop/export remains a manual requirement. The first viewport is CanvasKit/WebGL presentation
of the bounded software rasterizer, not a dedicated realtime WebGL renderer. Proprietary bytes are
session-only; only profile/settings use origin-local storage.

## macOS result

The previous desktop-core incompatibilities (missing portable shader variants, mandatory VAO use,
and overlay attribute state leaking into mesh draws) are addressed and synthetically tested. No
physical Mac was available. Consequently hardware rendering, texture upload, camera input, animation,
resize, Intel/Apple-Silicon packaging and driver performance are not claimed as validated. The
software fallback and exportable context/shader diagnostics remain the controlled path.

## Authoring and research boundaries

Synthetic glTF and OBJ cubes both authored as PC AEM (v4 and v5 respectively), reparsed, converted,
and produced deterministic software previews: one submesh, eight vertices and twelve triangles each.
This proves Workshop structural authoring, not in-game acceptance. Skinning, morph targets, multiple
UV sets and Blender animation reimport are not supported.

All 22 available GOF2 PC/Android language tables reconstruct byte-for-byte. Other `.bin` families are
not write-enabled. Clean-room runtime and corpus evidence indicates GOF2 missions are mixed native/
procedural state rather than a confirmed standalone mission container. Mission creation therefore
remains unavailable; the evidence and unresolved fields are documented under `docs/research/missions`.

## Reproduction

```powershell
dotnet build GalaxyOnFire2Workshop.sln -c Release --no-restore
dotnet test GalaxyOnFire2Workshop.sln -c Release --no-build --no-restore
dotnet format GalaxyOnFire2Workshop.sln --verify-no-changes --no-restore
dotnet list GalaxyOnFire2Workshop.sln package --vulnerable --include-transitive
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- validate-corpus data --decode --roundtrip --profile gof2-pc-1x --json work/baseline/pc-roundtrip-validation.json
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- compare-corpora data android_data ios_data macos_data ios_data2 --json work/baseline/multi-corpus-inventory.json
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- generate-synthetic --output samples/SyntheticDemo
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- model-import samples/SyntheticDemo/Assets/Imported/synthetic_cube_import.gltf --version 4 --output work/import-gltf.aem --preview work/import-gltf.png
dotnet publish src/Gof2Workshop.Browser/Gof2Workshop.Browser.csproj -c Release --no-restore -o artifacts/browser
dotnet run --project src/Gof2Workshop.App -c Release --no-build -- --tutorial quick-inspect
```

## Next smallest milestone

Use a physical Apple-Silicon Mac and current Safari/Chromium machines to close the hardware/browser
validation gaps, then implement a true WebGL render backend over the existing render snapshot. In
parallel, capture an actual Blender export/reimport pair before enabling animation conversion, and
isolate one non-language database family whose record boundaries and reference rules can be proved
byte-for-byte.
