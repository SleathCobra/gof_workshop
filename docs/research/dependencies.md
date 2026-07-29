# Dependency and renderer decisions

Reviewed 2026-07-29.

## Added dependencies

| Package | Version | Scope | License | Maintenance | Decision |
|---|---:|---|---|---|---|
| MSTest | 4.3.2 | Test projects only | MIT | Microsoft-supported; current stable package published 2026-07-13 | Selected for synthetic and optional local-corpus tests. It has no production/runtime role. |
| Avalonia.Desktop | 12.1.0 | Desktop application only | MIT | Avalonia-maintained current stable release, published 2026-07-09 | Selected as the Windows-first cross-platform desktop host. Supplies Win32, Skia, text shaping, and platform backends transitively. |
| Avalonia.Themes.Fluent | 12.1.0 | Desktop application only | MIT | Released and maintained with Avalonia 12.1.0 | Supplies the accessible compact Fluent control theme; avoids a custom control-theme dependency. |
| AssetRipper.TextureDecoder | 2.6.2 | AEI format library | MIT | Actively maintained AssetRipper component; current package published 2026-06-01 | Selected for managed PVRTC, ETC1/2, and ATC decoding. It is dependency-free, accepts raw spans, exposes predictable RGBA output, and adds no native runtime. The existing independently tested BC1/2/3 path is retained. |
| BCnEncoder.Net | 2.3.0 | AEI format library | MIT OR Unlicense | Current release published 2026-03-05; repository remains active | Selected for BC1/BC2/BC3 encoding behind `IAeiPixelEncoder`. It consumes raw RGBA spans, has no native dependency, exposes quality controls, and produces deterministic same-size blocks suitable for the preserved AEI surface layout. |

The `MSTest` metapackage brings the Microsoft test adapter, framework, testing platform, and coverage/reporting dependencies into test builds.

Official package records:

- <https://www.nuget.org/packages/MSTest/4.3.2>
- <https://www.nuget.org/packages/Avalonia.Desktop/12.1.0>
- <https://www.nuget.org/packages/Avalonia.Themes.Fluent/12.1.0>
- <https://www.nuget.org/packages/AssetRipper.TextureDecoder/2.6.2>
- <https://github.com/AssetRipper/AssetRipper.TextureDecoder>
- <https://www.nuget.org/packages/BCnEncoder.Net/2.3.0>

`Avalonia.Desktop` brings native SkiaSharp, HarfBuzz, Win32, ANGLE, X11, and macOS assets into the application output. Windows is first class; the cross-platform assets are a deployment-size tradeoff of the standard desktop package. Avalonia does not flow into parser, scene, exporter, CLI, or nonvisual workbench assemblies.

`dotnet list GalaxyOnFire2Workshop.sln package --vulnerable --include-transitive` reported no known vulnerable packages from the configured sources on 2026-07-29.

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

## Codec validation boundary

PVRTC 2/4bpp is validated both by synthetic zero blocks and by all 18 real local samples; visual inspection confirmed the expected RGBA/alpha orientation. ETC1, ETC2 RGBA, and ATC RGBA are exercised with bounded synthetic blocks because the current local corpus contains none. Android samples remain required to confirm platform-specific payload orientation and every alpha variant.

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
