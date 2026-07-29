# IDE workbench local validation report

Validated on Windows with .NET SDK 10.0.302 and Avalonia 12.1.0 on 2026-07-29. Proprietary paths, file bytes, and generated derivatives remain under ignored `data/` and `work/`.

## Baseline before this upgrade

| Check | Result |
|---|---:|
| Release build | Succeeded, 0 warnings, 0 errors |
| Existing tests | 34 passed (9 AEI, 9 AEM, 13 workbench, 3 integration) |
| Full corpus AEI | 1,228 parsed; 1,210 decoded; 18 PVRTC recognized unsupported |
| Full corpus AEM | 752 indexed; 751 parsed/scenes; 1 v2 recognized unsupported |
| Existing desktop | AEI/AEM viewing and static exports worked; limitations listed below were not implemented |

## Desktop validation

| Check | Result |
|---|---:|
| Lightweight assets indexed | 1,980 |
| AEI / AEM indexed | 1,228 / 752 |
| Recognized unsupported assets | 0 |
| Observed live scan | 0.08–0.13 seconds |
| Raw atlas parse/decode | 18–41 ms |
| Representative AEM v4 parse/scene/render | 67–77 ms |
| Representative PVRTC parse/decode | 46 ms |
| UI responsiveness | Window responding during/after every automated smoke launch |
| Clean desktop exits | All automation sessions closed normally |
| Nine restored/open documents | Responsive; 397.3 MiB working set / 317.0 MiB private |
| Final automated tests | 50 passed (15 AEI, 16 AEM, 15 workbench, 4 integration) |
| Full decode/scene corpus validation | 12.6 seconds wall-clock; 1,228 AEI decoded and 752 AEM scenes; 0 unsupported/corrupt/crashes |
| Representative DXT5 export | PNG atlas, overlay, metadata, and region PNG |
| Native export-picker automation | Passed AEM folder picker and AEI save picker; output remained in workspace `Generated/` |
| Native animated AEM export | 2 meshes, 1 animation, 3 transform channels, 1,627,460-byte BIN |
| Native PVRTC AEI export | Valid 25,126-byte PNG |
| Save-copy automation | AEI reconstruction and AEM structural serialization SHA-256-identical to their immutable sources |
| AEM structural writer | All 752 real corpus files round-tripped byte-for-byte in memory |

Representative documents opened through the desktop provider pipeline:

- raw RGBA UI atlas, 2048×2048, 251 regions;
- DXT1;
- DXT5;
- PC raw cube-map strip;
- PVRTC 2/4bpp decoded;
- AEM v4 with 16 submeshes and 990 triangles;
- AEM v5;
- AEM v2 parsed, rendered, and exported;
- v4 transform animation played and exported.

The animated v4 document exposed 47,838 vertices, 15,946 triangles, 154 source keys, a 10-second transform clip, and animation-enabled glTF status. Orbit, pan, zoom, perspective, animation time/playback, wireframe, pivots, bounds, submesh selection/isolation, normals, and winding controls are connected to the UI-independent renderer/evaluator options.

The software buffer resized to the actual viewport/DPI during live validation (for example 868×510), rather than retaining the 1000×700 bootstrap buffer. Resizes are bounded to 2,048 pixels per side and three million pixels.

Document tabs stay on one fixed-height horizontal row with scroll controls, an all-documents picker, and Alt+Left/Alt+Right activation history. The document surface reconciles one stable observable collection, so repeated selection does not recreate item presenters. A live regression alternated among three tabs twelve times: every selection activated, and the presenter count remained equal to the unique open-document count. Concurrent opens of the same normalized path also share one provider task. Explorer and Inspector detachment, close-to-dock, persisted floating state, and restored floating state were exercised in the real process. Docked and floating panes use separate view instances bound to the same workbench view model; controls are never reparented between Avalonia visual roots.

## Safety and persistence

- The application-state and workspace JSON formats are versioned.
- Malformed application state falls back to defaults.
- Missing game roots open with an actionable warning.
- Document paths under the game root persist relative to that root.
- Pane sizes, visibility, activity, bottom tab, open documents, active document, recent assets/workspaces, window bounds, and profile persist.
- Export destinations equal to or beneath the selected game root are rejected.
- Add to Mod and staged replacement validate full AEI/AEM parses, use atomic copies, record source/staged SHA-256 values in `.work/asset-operations.json`, and refuse a mod root beneath the game root.
- AEI container copies, reconstructed AEM copies, and immutable snapshots use the same destination guard.
- No desktop smoke run wrote beneath `data/`.

## Visual evidence

Ignored local screenshots:

- `work/screenshots/upgrade-aei-pvrtc.png`
- `work/screenshots/upgrade-aem-animation.png`
- `work/screenshots/upgrade-floating-inspector.png`
- `work/screenshots/upgrade-tab-overflow.png`

## Known limitations

- ETC1, ETC2 RGBA, and ATC RGBA have synthetic decoder coverage but no real samples in the local PC corpus.
- AEM v1/v3 have synthetic parser coverage but no real samples in the local corpus.
- UV animation and unresolved special channels remain preservation-only.
- Software rendering is CPU-bound and intentionally capped; it is not a production GPU viewport.
- Detachable tool windows are implemented, but arbitrary docking zones, floating document groups, and drag previews are not.
- Replacement stages a complete validated AEI/AEM file. PNG-to-AEI compression and per-region replacement are not implemented. AEM model serialization is implemented, but geometry-authoring controls are not yet exposed in the desktop.
