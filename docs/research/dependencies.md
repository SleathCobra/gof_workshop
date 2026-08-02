# Dependency and renderer decisions

Reviewed 2026-08-01.

## Added dependencies

| Package | Version | Scope | License | Maintenance | Decision |
|---|---:|---|---|---|---|
| MSTest | 4.3.2 | Test projects only | MIT | Microsoft-supported; current stable package published 2026-07-13 | Selected for synthetic and optional local-corpus tests. It has no production/runtime role. |
| Avalonia.Desktop | 12.1.0 | Desktop application only | MIT | Avalonia-maintained current stable release, published 2026-07-09 | Selected as the Windows-first cross-platform desktop host. Supplies Win32, Skia, text shaping, and platform backends transitively. |
| Avalonia.Themes.Fluent | 12.1.0 | Desktop application only | MIT | Released and maintained with Avalonia 12.1.0 | Supplies the accessible compact Fluent control theme; avoids a custom control-theme dependency. |
| Avalonia.Browser | 12.1.0 | Browser host only | MIT | Avalonia-maintained current stable release, published with Avalonia 12.1.0 | Official WebAssembly host using CanvasKit/WebGL. It supplies browser lifecycle and browser-authorized storage pickers without adding a server component. |
| AssetRipper.TextureDecoder | 2.6.2 | AEI format library | MIT | Actively maintained AssetRipper component; current package published 2026-06-01 | Selected for managed PVRTC, ETC1/2, and ATC decoding. It is dependency-free, accepts raw spans, exposes predictable RGBA output, and adds no native runtime. The existing independently tested BC1/2/3 path is retained. |
| BCnEncoder.Net | 2.3.0 | AEI format library | MIT OR Unlicense | Current release published 2026-03-05; repository remains active | Selected for BC1/BC2/BC3 encoding behind `IAeiPixelEncoder`. It consumes raw RGBA spans, has no native dependency, exposes quality controls, and produces deterministic same-size blocks suitable for the preserved AEI surface layout. |

The `MSTest` metapackage brings the Microsoft test adapter, framework, testing platform, and coverage/reporting dependencies into test builds.

Official package records:

- <https://www.nuget.org/packages/MSTest/4.3.2>
- <https://www.nuget.org/packages/Avalonia.Desktop/12.1.0>
- <https://www.nuget.org/packages/Avalonia.Themes.Fluent/12.1.0>
- <https://www.nuget.org/packages/Avalonia.Browser/12.1.0>
- <https://www.nuget.org/packages/AssetRipper.TextureDecoder/2.6.2>
- <https://github.com/AssetRipper/AssetRipper.TextureDecoder>
- <https://www.nuget.org/packages/BCnEncoder.Net/2.3.0>

`Avalonia.Desktop` brings native SkiaSharp, HarfBuzz, Win32, ANGLE, X11, and macOS assets into the application output. `Avalonia.Browser` brings WebAssembly builds of SkiaSharp/CanvasKit and HarfBuzz; it requires the `wasm-tools` workload at publish time. Avalonia does not flow into parser, scene, exporter, CLI, or nonvisual workbench assemblies.

`dotnet list GalaxyOnFire2Workshop.sln package --vulnerable --include-transitive` and
`--deprecated --include-transitive` reported no known vulnerable or deprecated packages from the
configured sources on 2026-08-01.

## Preserved internal implementations

- PNG writing still uses `ZLibStream` plus the independently implemented PNG chunk/CRC writer.
- DXT1/DXT3/DXT5 decoding still uses the existing raw-span BC decoder.
- PVRTC 2/4bpp, ETC1, ETC2 RGBA, and ATC RGBA use a small bounded adapter around `AssetRipper.TextureDecoder`; container, mip, face, and array traversal remain Workshop-owned.
- glTF 2.0 and OBJ output still use the validated normalized scene exporters. glTF now emits deduplicated PNG images, samplers, and primitive material bindings.
- The primary interactive viewport is an Avalonia-owned OpenGL control. The bounded software renderer remains the headless, deterministic, and initialization-failure fallback.
- Avalonia display copies decoded RGBA rows directly into `WriteableBitmap`; it does not encode/decode a PNG for screen display.

## Docking evaluation

| Library | Reviewed version | License | Maintenance | Decision |
|---|---:|---|---|---|
| Dock.Avalonia | 12.0.0.2 | MIT | Active; Avalonia 12 build published 2026-04-24 with document/tool, floating-window, and persistence support | Not added. Its model, theme, serializer, and supporting-package graph would duplicate the established workbench/document state. The focused shell now supplies persisted detachable tool windows without another runtime package. |

The milestone uses Avalonia grids, splitters, `TabStrip`, owned floating windows, and pane abstractions. Sizes, visibility, floating state, active activity, and bottom tab persist. Explorer, Inspector, and bottom tools detach via their drag handles and dock when the owned window closes. Arbitrary docking zones remain a possible later Dock.Avalonia adapter without changing parsers, workspace services, editor providers, or document view models.

Official Dock package record: <https://www.nuget.org/packages/Dock.Avalonia/12.0.0.2>

## Previously evaluated libraries

| Library | Current reviewed version | License | Maintenance status | Result |
|---|---:|---|---|---|
| SkiaSharp | 4.150.1 | MIT | Actively maintained; stable update published 2026-07-14 | Good future Avalonia/2D display candidate. Not needed for deterministic PNG output or this off-screen diagnostic renderer; would add native runtime assets. |
| SharpGLTF.Core | 1.0.6 | MIT | Maintained package, latest reviewed release published in late 2025 | Strong future choice when animation/material export expands. The milestone writer is intentionally narrow and was easier to validate byte-for-byte without another object model. |
| SixLabors.ImageSharp | 4.0.0 | Six Labors Split License 1.0 | Active; release published 2026-05-12 | Not selected. Its non-standard split/commercial terms are unnecessary for an MIT workshop when the required PNG path is small. |

Official package records:

- <https://www.nuget.org/packages/SkiaSharp/4.150.1>
- <https://www.nuget.org/packages/SharpGLTF.Core/1.0.6>
- <https://www.nuget.org/packages/BCnEncoder.Net/2.3.0>
- <https://www.nuget.org/packages/SixLabors.ImageSharp/4.0.0>

## Browser host decision

Avalonia's official `Avalonia.Browser` host was chosen instead of a parallel HTML framework. It
reuses the project’s C# parsers, authoring services, operation logs, and normalized scene model.
Browser file access is limited to browser-granted `IStorageFile` streams; exports use a
browser-authorized save/download stream. Inputs remain bounded to 256 MiB per file and 512 MiB per
inspection collection.

The dedicated scene backend is a small Workshop-owned WebGL 2 ES-module boundary. No JavaScript
package or game engine was added. JavaScript owns WebGL context calls, GPU objects, scheduling, ID
picking, and context restoration; C# owns parsing, animation/material snapshots, decoded RGBA, and
validation. The deterministic software renderer remains the fallback. IndexedDB access similarly
uses a focused, dependency-free ES module behind source-generated `JSImport` stubs.

The trimmed Release publish uses compiled Avalonia bindings. The resumed 2026-08-01 static publish
contains 206 files and is 31.12 MiB uncompressed after adding the import, workbench, and structured-
data assemblies. Real Brave and Edge runs validated WebGL 2 camera rendering and forced context
restoration; no WebGL binding package was required.

## Codec validation boundary

PVRTC 2/4bpp is validated by synthetic blocks and the PC/iOS/macOS corpora. ETC1 is exercised by 208 Android corpus files; ETC2/ATC-family dispatch is synthetic-fixture validated. All 5,136 GOF2 AEI files across the four product profiles, plus nine separately profiled GOF3D research files, parsed, decoded, and reconstructed byte-for-byte on 2026-07-31. Visual orientation and alpha correctness still require representative inspection per platform; a byte-identical container round trip alone does not prove orientation.

## Realtime preview approach

Avalonia 12.1.0's built-in `OpenGlControlBase` was selected over Silk.NET and a separate
native context host. Avalonia owns context creation, synchronization, DPI-aware surface resize,
and teardown; the Workshop makes GL calls only in the control's init/render/deinit callbacks.
The existing `SceneDocument`, `SceneCamera`, animation evaluator, and exporters remain free of
Avalonia and OpenGL. No extra OpenGL binding or game engine was added.

The renderer uploads immutable mesh/index/diagnostic buffers once per scene, caches decoded AEI
textures by asset identity and source hash, updates transform/uniform state per frame, and deletes
per-context resources during document/control teardown. Initialization or context failures create
a structured warning and switch to the retained software renderer.

The Windows validation context was OpenGL ES 3.0 through Avalonia's ANGLE backend on Intel UHD
Graphics (Direct3D 11), maximum texture dimension 16,384.

## Import and structured-data decision

No model-import package was added. The Workshop importer intentionally implements a bounded glTF
2.0/GLB/OBJ subset: triangle primitives, float position/normal/UV/color accessors, 8/16/32-bit
indices checked into the AEM 16-bit limit, contained sidecars, baked node transforms, and explicit
rejection of sparse accessors, skinning, morphs and unsupported topology. This keeps the supported
surface auditable and avoids pulling a general 3D engine into desktop or browser output.

`Gof2Workshop.GameData` adds no package. BIN-family readers/writers use BCL text codecs and
`BinaryPrimitives`; operation/recovery records are platform-neutral. The Avalonia editor consumes
that model without introducing a grid or MVVM framework.

No model-import or Blender package was added. The Blender helper uses Blender's bundled `bpy` and
glTF add-on only; the desktop detects and invokes an explicitly configured/local executable. The
core authoring path does not depend on Blender.
