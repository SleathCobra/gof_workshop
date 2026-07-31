# Browser-local Workshop host

`src/Gof2Workshop.Browser` is a static Avalonia WebAssembly host. It reuses the clean-room AEI/AEM parsers, normalized scene model, codec adapters, PNG writer and bounded software renderer. It has no server API and does not upload selected data.

## Build and serve

Install the matching .NET WebAssembly workload once, then publish:

```powershell
dotnet workload install wasm-tools
dotnet publish src/Gof2Workshop.Browser/Gof2Workshop.Browser.csproj -c Release -o artifacts/browser
dotnet serve --directory artifacts/browser --port 5237
```

Any static host that supplies the generated MIME types can serve the directory. Opening `index.html` directly with `file://` is not supported by WebAssembly module loading; use an HTTP static host.

## File and privacy model

- **Open local files** and drag/drop use browser-granted `IStorageFile` streams.
- Files are retained only in the current in-memory Inspection Collection, up to 256 MiB each and 512 MiB total.
- **Export PNG** uses a user-initiated browser save/download stream.
- The browser never has arbitrary filesystem access.
- Only the selected profile and small Workshop settings use origin-local `localStorage`; **Clear local settings** removes those keys.
- Proprietary asset bytes are not persisted across reloads in this milestone.

## Rendering

AEI pixels decode into Workshop-owned RGBA buffers. AEM scenes use the normalized scene model and a bounded textured software rasterizer; Avalonia presents the result through CanvasKit/WebGL. When a selected AEI stem matches an AEM stem (including known diffuse/LOD suffixes), it is applied locally to all UV-bearing primitives and the heuristic is shown in status text.

This proves the renderer-neutral scene boundary, but it is not yet a dedicated realtime WebGL scene backend. Desktop OpenGL controls are not referenced by the browser project.

## Validated publish

On 2026-07-31 a trimmed Release publish succeeded with the .NET 10 WebAssembly toolchain. The final static artifact is 170 files / 29.56 MiB uncompressed and contains `index.html`, `main.js`, `dotnet.js`, and the local-settings module. Static delivery of the host bootstrap was verified over HTTP; the final linked files were also checked directly in the publish directory.

Interactive automation could not attach to the available in-app browser in this environment because its control bootstrap failed before a browser session was created. Startup, file-picker, drag/drop and visual evidence therefore remain manual-browser validation requirements; build/link and static delivery are verified.

## Compatibility and offline

The target is current Chromium, Firefox and Safari releases with WebAssembly, WebGL 2/CanvasKit and required browser storage APIs. File System Access is not assumed. Avalonia storage-provider fallbacks supply standards-compatible picker/download behavior.

All application assets are static, so a service-worker/PWA cache is feasible. No service worker is shipped yet; offline use works only after a host/browser cache has the required files and is not claimed as an installable PWA.
