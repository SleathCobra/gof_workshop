# Browser-local Workshop host

Validated 2026-08-01 with Avalonia 12.1.0, .NET 10 WebAssembly, Brave
150.1.92.144, and Edge 150.0.4078.105.

`src/Gof2Workshop.Browser` is a static application. Parsing, editing, rendering,
reconstruction, and export happen in the browser process; there is no asset API
and no selected file is uploaded.

## Build and serve

```powershell
dotnet workload install wasm-tools
dotnet publish src/Gof2Workshop.Browser/Gof2Workshop.Browser.csproj `
  -c Release -o artifacts/browser
.\scripts\serve-browser.ps1 -Directory artifacts/browser -Port 5237
```

Open `http://127.0.0.1:5237/`. `file://` is not supported because the browser
must load JavaScript modules and WebAssembly resources from an HTTP origin. Any
static host with correct `.wasm` and JavaScript MIME types is sufficient.

The resumed clean publish contained 206 files / 32,629,080 bytes (31.12 MiB)
uncompressed. It includes no game asset.

## File, storage, and privacy model

- Browser-authorized pickers and drag/drop create a temporary Inspection
  Collection. Individual inputs are limited to 256 MiB and the collection to
  512 MiB.
- The collection can contain AEI, AEM, BIN, LANG, PNG, glTF/GLB and OBJ/MTL
  files. Selected glTF sidecars and AEM/AEI companions resolve within the
  collection without arbitrary filesystem access.
- Export uses a browser-authorized save/download stream.
- Versioned workspace archives contain the selected profile, selected asset
  bytes, material/edit state, and recovery operation logs. Import verifies
  source hashes before replay.
- IndexedDB stores a workspace only after **Save local workspace**. The UI shows
  origin quota/usage and can remove workspace data. Small profile settings use
  `localStorage`.
- Clearing local data removes Workshop IndexedDB and setting keys. Browsers may
  still retain ordinary HTTP cache entries containing only the public app.

The browser cannot and does not claim unrestricted local filesystem access.

## Realtime rendering

`workshopWebGl.js` is a focused WebGL 2 backend over the same normalized scene,
camera, material, texture, animation, selection, and diagnostic state used by
desktop renderers. It owns persistent VAO/VBO/IBO and texture objects, uploads
static geometry once, schedules frames only when needed, suspends hidden
content, and disposes resources explicitly.

Implemented modes include lit/unlit/solid rendering, decoded AEI texture upload,
perspective/orthographic camera, orbit/pan/zoom, frame all/selected, animation,
selection ID picking, isolation, culling, alpha, wireframe, pivots, bounds,
resize/high-DPI handling, and context-loss reconstruction. The bounded software
rasterizer remains selectable and is used when WebGL 2 initialization fails.

Final real-browser diagnostics:

| Browser | WebGL | Demo meshes/draws | Camera frame | Forced loss/restore |
|---|---|---:|---:|---|
| Brave 150.1.92.144 | WebGL 2 / OpenGL ES 3.0 Chromium | 2 / 6 | 1 -> 2, 1.5 ms | passed, one loss |
| Edge 150.0.4078.105 | WebGL 2 / OpenGL ES 3.0 Chromium | 2 / 6 | 1 -> 2, 0.3 ms | passed, one loss |

Both reported a maximum texture dimension of 16,384. These figures are for the
small public synthetic smoke scene and are functional evidence, not a dense-model
benchmark.

## Browser editing and authoring

- AEI: decode, region selection, matching-size PNG replacement, undo/redo,
  raw/BC source-preserving encode, writer reparse/decode validation, and edited
  AEI download.
- AEM: inspect and animate; import glTF with selected sidecars, GLB, and OBJ/MTL;
  author PC AEM v4, reparse, render through WebGL, and download the result.
- BIN: registry-based family detection, structured field table, size-stable safe
  edits, undo/redo, source-hash recovery, reparse, and download. Unknown bytes
  remain in their original locations.
- Workspace: versioned IndexedDB persistence and portable archive import/export.

The browser UI remains a focused Quick Inspect surface, not the complete desktop
IDE shell. Full multi-document workbench layout, native process launching, and
direct Blender integration are desktop-only.

## Repeatable real-browser smoke

```powershell
.\scripts\browser-smoke.ps1 `
  -Executable 'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe' `
  -Url 'http://127.0.0.1:5237/?smoke=1' `
  -Screenshot work\screenshots\browser-webgl.png
```

Additional public-data scenarios are `?smoke=bin`, `?smoke=storage`,
`?smoke=aei-edit`, and `?smoke=aem-author`. The harness attaches through the
Chromium DevTools protocol, checks the application-owned smoke state, exercises
camera input and context loss for 3D cases, and captures a real screenshot.
Connect, send, and receive operations have hard deadlines; a failed bootstrap aborts the socket and
returns a controlled diagnostic instead of hanging during WebSocket disposal.

Firefox was not installed in this environment. Safari cannot be physically
validated without macOS. Both remain explicit runtime-validation gaps. A service
worker/PWA shell is feasible but is not shipped in this milestone.
