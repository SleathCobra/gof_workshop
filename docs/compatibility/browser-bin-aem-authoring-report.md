# Browser, BIN, and AEM authoring validation

Validated 2026-08-01. Proprietary corpora and generated derivatives stayed in
ignored roots; only independently generated CC0 fixtures are included in the
repository.

## Baseline and final gates

| Gate | Result |
|---|---|
| Release solution build | passed, 0 warnings / 0 errors |
| Automated tests | 90 passed / 0 failed / 0 skipped |
| Browser clean publish | 206 files / 32,618,127 bytes (31.11 MiB) |
| NuGet vulnerability/deprecation audit | no known vulnerable or deprecated package |
| Tracked proprietary/generated-root files | none |

Self-contained cross-publishes also passed from Windows: Windows x64 244 files /
215,293,387 bytes; Linux x64 241 / 107,745,236; macOS x64 241 / 110,631,270;
macOS arm64 241 / 117,549,224. These are unpacked sizes. The macOS outputs are
compile/package evidence only and are not claimed as runtime validation.

The initial baseline before this continuation was 81 tests and a 30,998,367-byte
browser publish. The Windows desktop smoke with a restored document session used
approximately 342 MiB working set; that is an observation, not a controlled leak
profile. Browser WebAssembly linked with a 35,979,264-byte initial heap and a
2 GiB growth ceiling; application input limits remain lower and explicit.

## Real browser result

The primary AEM path is dedicated WebGL 2, with persistent GPU buffers and a
bounded software fallback. Real Brave 150.1.92.144 and Edge 150.0.4078.105 runs
both rendered the public two-mesh scene, reacted to camera input, and recovered
from a forced context loss. Brave additionally passed:

- editable BIN: changed a synthetic name, reparsed it, and retained the original;
- workspace recovery: restored three assets and replayed a source-hash-checked
  structured operation from IndexedDB;
- AEI edit: decoded a PNG, replaced a region, encoded BC3, reparsed and decoded,
  with maximum synthetic channel error 33;
- AEM authoring: imported glTF plus its selected sidecar from the in-memory
  collection, wrote PC AEM v4, reparsed it, and rendered the authored submesh.

Browser screenshots are generated under ignored `work/screenshots/`. The smoke
harness and static server are committed, so these runs can be reproduced without
game assets.

## BIN corpus coverage

Every one of the 136 discovered `.bin` files in the four supplied GOF2 corpora
was classified, parsed, written without edits, and reproduced byte-for-byte.
The isolated GOF3D corpus contains no `.bin` in the supplied sample. Detection is
intentionally filename/consumer based because the files do not share one magic.

| Family | Files | Support | Existing-record editor | Creation | Remaining unknowns |
|---|---:|---|---|---|---|
| Names | 52 | semantic read/write | yes, size-stable text | no | semantic role is confirmed; allocation limits are not |
| Items and blueprints | 4 | structural read/write | yes | no | most array ordinals and cross-table meaning |
| Ships | 4 | structural read/write | yes | no | all but the strongest parameter hypotheses |
| Systems and connections | 4 | structural read/write | yes | no | several integer/reference roles |
| Stations | 4 | semantic/structural read/write | yes | no | unresolved integer roles |
| Agents | 4 | structural read/write | yes | no | mobile/PC field semantics and references |
| Wanted targets | 3 | structural read/write | yes | no | IDs, rewards, and reference meaning |
| News/ticker | 4 | structural read/write | yes | no | flag semantics |
| Ship parts | 4 | structural read/write | yes | no | attachment role/axis semantics |
| Station parts | 4 | structural read/write | yes | no | attachment role/axis semantics |
| Weapon positions | 17 | structural read/write | yes | no | group/reference semantics |
| Collision geometry | 16 | loss-preserving advanced | raw preservation/copy | no | platform records are not safe for semantic edits |
| Docking points | 9 | loss-preserving advanced | raw preservation/copy | no | platform record meanings |
| Weapons and equipment | 7 | loss-preserving advanced | raw preservation/copy | no | platform table variants remain opaque |

No discovered family is left unclassified. “Loss-preserving advanced” does not
mean semantic editing: the editor exposes metadata/raw preservation and exact
copy output but deliberately provides no guessed field edits. All write paths
reparse and refuse destinations under the selected game root. Record-resizing
string edits are refused until offsets/count behavior is proven.

No separate dialogue, faction, reputation, save, mission, or UI-layout `.bin`
family exists in the five supplied corpora. Blueprint arrays occur inside the
detected items family; weapons/equipment use the detected platform weapon-table
family. Runtime research still locates campaign/mission behavior in native
`LevelScript`/generator state rather than in a missed `.bin`. Those requested
categories are therefore documented absences or runtime blockers, not silently
ignored files.

## AEM authoring and Blender

`AemAuthoringProject` is an operation-based model over immutable snapshots. It
supports PC v4/v5 targets, submesh import from AEM/glTF/GLB/OBJ, selection,
composition, duplicate/remove/reorder/rename, geometry replacement, pivots,
bounds, material identifiers, hide/lock state, confirmed transform tracks,
key add/delete/replace, undo/redo, and validated writer/reparse/scene conversion.
Cross-platform targets and preservation-only animation channels fail explicitly.

The desktop **Create / Compose AEM** command accepts multiple source models in
one operation, including existing AEM submeshes, then commits the result
atomically only after validation. The browser supports glTF/GLB/OBJ authoring and
download. A full canvas-style desktop authoring document and empty unsaved model
document remain future UI work; the underlying authoring operations are tested.

Blender 5.1.2 at the configured local installation was run headlessly. A Workshop
synthetic animated AEM plus AEI texture exported to glTF, imported as two meshes,
two materials, one image, and one action. The script changed a location key by
+0.25, re-exported glTF, and retained both stable submesh IDs and document IDs.
Workshop reimport then authored AEM v4 with two submeshes, nine vertices, three
triangles, reparsed it, evaluated its animation, and produced a deterministic
software preview. The optional add-on registered two operators and its panel in
a clean Blender session.

Blender warned that unequal channel key counts required animation baking. The
reimport therefore has 753 baked keys across 18 curves: the path is structurally
valid and playable, but it is a deliberately documented resampling loss, not a
lossless curve round trip.

## Research blockers retained

- The corresponding Android/iOS/macOS selector-9 animation and two non-finite
  AEM samples remain controlled unsupported records. Raw structures are retained;
  no guessed sentinel replacement is made.
- GOF3D legacy strips remain isolated from GOF2 profiles.
- Corpus and runtime evidence still supports procedural side missions plus native
  campaign `LevelScript` state. No declarative mission container is confirmed,
  so mission writing and a mission graph remain unsafe.
- Firefox, Safari, and physical macOS rendering were not run in this environment.
- Game-effective materials remain distinct from viewer/export assignments; no
  fictional material field is serialized into AEM.

## Exact reproduction commands

```powershell
dotnet build GalaxyOnFire2Workshop.sln -c Release --no-restore
dotnet test GalaxyOnFire2Workshop.sln -c Release --no-restore
dotnet publish src/Gof2Workshop.Browser/Gof2Workshop.Browser.csproj `
  -c Release --no-restore -o artifacts/browser
.\scripts\serve-browser.ps1 -Directory artifacts/browser -Port 5237
.\scripts\browser-smoke.ps1 -Executable $brave `
  -Url 'http://127.0.0.1:5237/?smoke=1' `
  -Screenshot work\screenshots\browser-webgl.png
dotnet list GalaxyOnFire2Workshop.sln package --vulnerable --include-transitive
dotnet list GalaxyOnFire2Workshop.sln package --deprecated --include-transitive
```
