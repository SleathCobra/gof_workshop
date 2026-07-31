# Realtime rendering and editable-texture vertical slice

Validated 2026-07-29 on Windows with .NET 10.0.302 and Avalonia 12.1.0.

## Realtime rendering

- Avalonia `OpenGlControlBase` is the default AEM viewport; the prior software renderer remains
  available and is still used by the CLI/tests.
- Renderer path: normalized scene → animation transform snapshot → persistent VBO/IBO/diagnostic
  buffers → GL draw. Parsers and exporters have no OpenGL dependency.
- Modes: lit/unlit textured, solid diagnostic, provisional auxiliary channel, winding; overlays:
  wireframe, normals, pivots, bounds, selection, isolation, and back-face culling.
- Click selection uses bounds-first CPU ray/triangle tests and synchronizes the Inspector/list.
- Renderer-independent viewport input uses a dedicated captured interaction surface above the
  OpenGL/software layers. A physical 144x36-pixel Windows drag changed the real ship camera from
  yaw -35°/pitch 25° to yaw 48°/pitch 46°, matching the configured sensitivity exactly.
- Context/device validated: OpenGL ES 3.0, ANGLE/Direct3D 11, Intel UHD Graphics driver
  31.0.101.2137, maximum texture dimension 16,384.
- `ship_017_terran_lod_1.aem`: 5,574 vertices, 1,858 triangles, four diagnostic draw calls;
  observed submitted-frame time 34.99 ms with wire/pivot/bounds enabled.
- `bar_midorian_anim.aem`: 47,838 vertices, 15,946 triangles, two submeshes; textured transform
  animation was visually observed at 2.26/10 seconds while the UI remained responsive.
- Local smoke working set was about 420 MiB for the ship and 500 MiB for the dense animated model,
  including the full IDE, 1,980-entry index, decoded textures, software fallback image, and GL data.

## Materials and export

- The relationship service records source, confidence, reason, candidates, selected asset, and
  warnings. Low-confidence matches are not selected silently.
- Confirmed local naming result:
  `ship_017_terran_lod_1.aem` → `ship_017_terran_diffuse.aei`
  (`NamingConvention`, high confidence).
- Workspace overrides can assign, clear, or reset each primitive independently.
- AEI mip images decode once and upload through a source-hash/surface keyed GL cache.
- The real textured ship exported as glTF with one deduplicated PNG material. The animated
  `bar_midorian_anim.aem` export contains two meshes, two materials sharing one image, and one
  transform animation. Khronos `gltf-validator` 2.0.0-dev.3.10 reported zero errors, warnings,
  infos, or hints; Blender 5.1.2 imported two meshes, two materials, one image, and one animated
  action.

## Texture editing and mod build

- Matching-size PNG region import creates an operation; original parser/atlas snapshots remain
  immutable. Overlap detection emits a warning.
- Undo/redo, divergent redo clearing, autosave/recovery serialization, and source-hash conflict
  refusal are covered by synthetic tests.
- Raw RGBA, BC1, BC2, and BC3 encoding preserve surface byte lengths and mip layout. Each result is
  reconstructed, reparsed, decoded, and compared before it can be staged.
- A versioned manifest/build service verifies source and staged hashes, rejects conflicts, omits
  unchanged original copies, writes only outside the game root, and produced identical content
  hashes across repeated synthetic builds.

## Validation

```text
Release build:                 0 warnings, 0 errors
Automated tests:               60 passed, 0 failed
NuGet vulnerability audit:     no known vulnerable packages
Native picker workflow:        glTF, BIN, PNG, AEI copy, AEM copy succeeded
Khronos glTF validation:       0 errors, 0 warnings
AEI corpus:                    1,228 parsed / 1,228 decoded
AEM corpus:                    752 parsed / 752 scene-converted
Uncontrolled corpus crashes:   0
```

Ignored visual evidence is under `work/screenshots/`, including:

- `realtime-textured-animation.png`: textured ship, material confidence, diagnostics, and GPU log.
- `realtime-animation-playback.png`: dense textured model during animation playback.
- `aei-region-edit-validated.png`: imported region, working/original state, difference statistics,
  and successful reconstruction/reparse/decode validation.
- `changes-staged-aei.png`: the validated replacement in the Changes activity.
- `mod-build-output.png`: the deterministic one-asset build, report, manifest, and output folder.

## 2026-07-30 workbench and AEM follow-up

- The document strip and AEM command bar now use a Workshop-owned horizontal overflow control.
  Native scroll thumbs are hidden; two fixed 20 × 20 theme-aware edge buttons and horizontal
  wheel/trackpad scrolling remain available without covering the controls.
- A document-tab context menu selects the clicked tab and delegates Close, Close Others, Close
  Tabs to the Right, and Close All to the centralized document commands. Multi-close operations
  dispose the removed documents and publish one document-manager change notification.
- Cross-checking AEMesh's independent importer behavior against the reconstructed engine transform
  and quaternion paths confirmed that scalar animation translation is `(X, Z, -Y)`, rotations use
  the engine's signed Euler-to-quaternion construction, and rotation keys use component-wise
  normalized linear interpolation. The former generic .NET yaw/pitch/roll and slerp path was
  removed.
- A bounded corpus audit parsed all 752 AEM files: 150 files / 660 submeshes contain transform
  animation, and all 660 animated submeshes use the scalar storage covered by the correction.
  Multi-submesh files are flat sibling mesh objects with independent pivots and animation records;
  the examined format and engine paths contain no bone weights, skeletal rig table, or submesh
  parent index.
- Release validation after the follow-up: 62 tests passed, the isolated Release solution build
  completed with zero warnings/errors, and full corpus validation parsed and scene-converted all
  752 AEM plus parsed/decoded all 1,228 AEI without an uncontrolled crash.
- Ignored screenshots `compact-scroll-hover.png` and `tab-context-menu.png` record the live
  Avalonia hover footprint and context menu. Time-sampled software renders for animated corpus
  models are under `work/animation-validation/`.

## Remaining limitations

- Material relationships remain heuristic unless manually confirmed; AEM contains no invented
  material fields.
- Android ETC/ATC decoding has synthetic coverage but needs a real Android corpus. Mobile encoding
  remains unavailable.
- Cube-map face order, source handedness/up-axis, container-level transform composition, and
  auxiliary float4 semantics remain explicitly unresolved. No hierarchy is inferred among the
  sibling submeshes stored in one AEM.
- Packed-vector animation axis behavior is preserved as stored because the local animated corpus
  uses scalar tracks exclusively. UV and special/unresolved animation channels remain preserved
  but are not played or exported.
- Recovery replay is implemented and hash-guarded; a dedicated startup choice dialog is not yet
  wired.
- The Changes activity currently presents validated staged replacements and conflicts; richer
  stage/unstage grouping and full side-by-side comparison documents remain follow-up UI work.

## 2026-07-31 cross-platform continuation

- The Release solution now contains explicit GOF2 PC, Android, iOS, and macOS profiles plus a
  separately identified GOF3D iOS research profile. Final automated validation is 81/81 tests.
- The five available local corpora contain 5,145 AEI and 4,514 AEM files. All AEI files parse,
  decode, and reconstruct byte-for-byte. AEM scene conversion succeeds for 4,493 files; 21 legacy
  or malformed files are classified with controlled diagnostics rather than crashing.
- The macOS renderer now chooses explicit GLSL ES 3.00, desktop core 1.50, or legacy desktop 1.20
  sources from the actual context and enforces VAO use on desktop core contexts. Physical Mac
  hardware was unavailable, so this is build/test validation plus a controlled software fallback,
  not a claim of completed driver validation.
- Workspace-free Quick Inspect, the browser-local host, glTF/GLB and OBJ import into validated PC
  AEM v4/v5, a keyframe inspector/editor for confirmed transform channels, the structured language
  editor, the expanded CC0 synthetic corpus, and the restartable tutorial overlay are implemented.
