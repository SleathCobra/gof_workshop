# Semantic game data, dependency graph, Mission Explorer, and AEM Authoring Studio

Validated on Windows on 2026-08-01. All proprietary corpora and generated evidence remained under
ignored roots. Asset names are included only where needed to identify public format consumers; no
game bytes or private absolute corpus paths are recorded here.

## Baseline and final gates

| Gate | Baseline | Final |
|---|---:|---:|
| Release solution build | passed, 0 warnings / 0 errors | passed, 0 warnings / 0 errors |
| Automated tests | 100 passed | 112 passed |
| GOF2 BIN files classified | 136 | 136 |
| Unchanged BIN byte-identical writes | 136 | 136 |
| Controlled BIN edited/reparse checks | not generated as one matrix | 136 |
| PC AEI parse/decode/write | 1,228 / 1,228 / 1,228 | unchanged |
| PC AEM parse/scene/write | 752 / 752 / 752 | unchanged |

The initial build took 12.33 seconds and the initial tests took 8.64 seconds. The resumed final gate
took 35.37 seconds for the complete Release build and 11.07 seconds for all 112 tests. The exact
commands are listed below.

## BIN family coverage

The generated, per-family/per-file evidence is in [bin-support-matrix.md](bin-support-matrix.md).
Every discovered GOF2 BIN belongs to one of these 13 structural families. “Structural” means that
record boundaries, numeric storage, offsets, and write behavior are confirmed while at least one
gameplay label remains unresolved. It does not mean guessed semantic support.

| Family | Platforms | Files | Confirmed/typed fields | Preserved unknowns | Read/write | Unchanged | Edited | Creation | Real-game status |
|---|---|---:|---|---|---|---:|---:|---|---|
| Names | PC/Android/iOS/macOS | 52 | modified-UTF name | none exposed | semantic | 52/52 | 52/52 | disabled | not mutation-tested |
| Items/blueprints | all GOF2 | 4 | three counted integer arrays; component IDs/amounts | attribute ordinal meanings | structural | 4/4 | 4/4 | disabled | not mutation-tested |
| Ships | all GOF2 | 4 | ID, hitpoints, load, value, handling, four slot types | none exposed | semantic | 4/4 | 4/4 | disabled | not mutation-tested |
| Systems/connections | all GOF2 | 4 | name, safety, visibility, faction, XYZ, jumpgate, star, stations, neighbours | legacy/static array meaning | structural | 4/4 | 4/4 | disabled | not mutation-tested |
| Stations | all GOF2 | 4 | name, station/system IDs, technology, planet texture ID | none exposed | semantic | 4/4 | 4/4 | disabled | not mutation-tested |
| Agents | all GOF2 | 4 | name, message, station/system, race/sex, blueprint, price, face bytes | mobile variant scalar | structural | 4/4 | 4/4 | disabled | not mutation-tested |
| Wanted targets | Android/iOS/macOS | 3 | ID, ship/loadout, HP, loot, reward, prerequisite fields | face-part meaning | semantic | 3/3 | 3/3 | disabled | not mutation-tested |
| News ticker | all GOF2 | 4 | active, four condition values, min/max level | condition meanings | semantic storage | 4/4 | 4/4 | disabled | not mutation-tested |
| Ship parts | all GOF2 | 4 | group/resource IDs, position/rotation/scale | resource-ID path map | structural | 4/4 | 4/4 | disabled | not mutation-tested |
| Station parts | all GOF2 | 4 | group/hangar/resource IDs, position/rotation | resource-ID path map | structural | 4/4 | 4/4 | disabled | not mutation-tested |
| Collision geometry | all GOF2 | 16 | owner, sphere center/radius, AABB center/half-extents | profile axis/scale | structural | 16/16 | 16/16 | disabled | not mutation-tested |
| Docking points | Android/iOS/macOS | 9 | owner/type, position, rotation | trailing auxiliary float3 | structural | 9/9 | 9/9 | disabled | not mutation-tested |
| Weapon positions | all GOF2 | 24 | owner/type, fixed position, optional direction float3 | incomplete point-type enum | structural | 24/24 | 24/24 | disabled | not mutation-tested |

All editor documents share operation-based undo/redo, source-hash-checked recovery, raw values,
unknown-field warnings, JSON operation import/export, reparse validation, validated staging, and
Build Mod integration. Record resizing/creation remains disabled because ID allocation, sorting,
capacity, and external reference updates are not established for every profile.

## Unified dependency graph

`DependencyGraph` is platform-neutral and scope-incremental. Corpus, mission, and material producers
replace only their own scopes. Stable identities contain profile, family/path, record ID or stable
index, and subresource index. Repeated physical `OwnerId` values use record indices; a real mobile
corpus run found and fixed this distinction.

Nodes cover files/records, language entries, ships/items/systems/stations/agents/wanted/news,
AEM/submeshes, AEI/atlas regions, missions, native handlers, material/workspace overrides, generated
assets, and missing external references. Edges retain kind, originating field, evidence text,
confidence, profile, writability, and validation state. User confirmations remain workspace facts;
they do not mutate global format evidence or become fictional AEM fields.

| Profile | Indexed assets | Graph nodes | Graph edges | Broken/unresolved | Index ms | Graph ms | Managed delta |
|---|---:|---:|---:|---:|---:|---:|---:|
| GOF2 PC 1.x | 2,016 | 11,418 | 43,563 | 0 | 115.06 | 2,560.01 | 166.7 MiB |
| GOF2 Android | 2,436 | 13,861 | 45,817 | 3 | 103.72 | 3,317.88 | 45.6 MiB |
| GOF2 iOS | 2,408 | 10,495 | 8,842 | 30 | 109.12 | 3,699.82 | 26.1 MiB |
| GOF2 macOS | 2,821 | 11,207 | 9,138 | 30 | 127.48 | 4,158.25 | 27.7 MiB |

PC Mission Explorer contribution raises the live workbench graph to 11,440 nodes and 43,577 edges.
The three mobile AEM metadata failures are controlled graph problems. iOS/macOS additionally lack
the locale files present in the supplied PC/Android corpora, so 27 encoded agent message references
are honestly represented as missing rather than silently accepted.

The Dependencies activity supports incoming/outgoing queries, opening endpoints, user
confirm/reject decisions, and a bounded lazy graph document with pan, zoom, depth expansion, filters,
relationship/evidence/platform filtering, bounded shortest-path tracing, JSON report export, path
highlighting, and broken-reference colors. Build Mod adds dependency warnings for affected staged
source nodes.

## Mission Explorer and LevelScript findings

No standalone campaign-mission BIN container was found. Two independently consulted engine
reconstructions agree on a mixed model:

- freelance missions are constructed procedurally by the runtime;
- campaign progression is selected by a persisted campaign-step value and dispatched through native
  `Level`, `LevelScript`, dialogue, objective, and status logic;
- Android/iOS/macOS `wanted.bin` contributes 25 typed bounty records per supplied corpus;
- PC exposes the same runtime concepts through research evidence but has no supplied `wanted.bin`.

The read-only evidence model includes campaign/freelance/wanted identities, states, evidence-bearing
transitions, rewards, references, unknowns, platform findings, and seven named native/runtime
handlers. The Objective Explorer catalogs evaluator types 0 through 25. Twenty conditions are
confirmed, five remain strong/hypothetical, and the default/unresolved type stays unknown.

The Mission Explorer displays mission groups, handler evidence, objective types, states,
transitions, references, raw evidence, and platform differences. Search plus kind, confidence and
handler filters operate on the evidence index; references open the bounded dependency document and
the selected research set can be exported as JSON. The private save comparison tool reports only
contiguous changed ranges between equal-length user-provided saves; it stores neither save and does
not assign meaning automatically.

Mission creation remains disabled. Identity allocation, trigger registration, native handler
execution, dialogue/reward invocation, persistence, and executable capacity are not proven. The
smallest safely editable mission-adjacent subset is modification of existing typed wanted-contract
fields; even that remains writer/reparse-validated rather than claimed in-game validated.

## AEM Authoring Studio

The New AEM dialog selects PC v4/v5, name, mod target, and one of seven independently authored
templates: Empty, Static Prop, Single-mesh Ship, Multi-submesh Ship, Animated Object, Billboard,
and Station Component. Coordinate/unit choices remain constrained to the validated PC conversion.

The authoring document now provides:

- a multi-select hierarchy with drag-to-reorder, duplicate, delete, move, hide/show, and lock;
- explicit import options for scale, normals, degenerates, welding, pivot centering, winding, V flip,
  animation, and materials; failed multi-file imports roll back to their operation mark;
- AEM, glTF, GLB, and OBJ/MTL sources through the neutral imported scene;
- an import preflight tab showing source hierarchy rows, channels, material, statistics, diagnostics,
  and target representability;
- deterministic glTF `UNSIGNED_INT` and oversized OBJ triangle splitting into 16-bit-safe submeshes,
  with source-node animation copied to every derived chunk and an explicit split warning;
- pivot and bound changes, normal generation/normalization, winding reversal, degenerate removal,
  exact duplicate welding, and explicit scale/rotation/translation application;
- preview/export AEI assignment with an explicit warning that it is not a game-effective AEM field;
- selected-track key list, add/update/duplicate/delete, clear track, and source-submesh AEM animation
  import with replace or time-key merge;
- realtime OpenGL preview with the existing software fallback;
- validated AEM save, new-asset staging, glTF/OBJ export, and detected Blender 5.1 launch.

Every authored save/stage calls the existing build pipeline: target validation, AEM serialization,
reparse, normalized scene conversion, finite/index/count checks, and preview generation. New assets
use a distinct `Add` manifest operation with no fabricated original hash; attempts to add over a real
game asset are rejected and must use replacement staging.

Validated synthetic cases include all seven templates, v4/v5, multi-source composition,
duplicate/removal/reorder/pivot/material/transform operations, exact vertex welding, key
update/duplicate/delete, AEM animation replace/merge, writer/reparse, glTF animation reimport, and
deterministic software preview. New 65,538-vertex glTF and OBJ fixtures prove full triangle retention
across two generated submeshes. Existing Blender 5.1 headless geometry/material/animation evidence
remains valid and was not weakened.

## Corpus compatibility

| Profile | AEI parse/decode/write | AEM parse/scene/write | Controlled exceptions |
|---|---:|---:|---|
| GOF2 PC 1.x | 1,228/1,228/1,228 | 752/752/752 | none |
| GOF2 Android | 1,159/1,159/1,159 | 1,212/1,212/1,212 of 1,215 | selector 9; two non-finite records |
| GOF2 iOS | 1,163/1,163/1,163 | 1,212/1,212/1,212 of 1,215 | same three shared records |
| GOF2 macOS | 1,586/1,586/1,586 | 1,202/1,202/1,202 of 1,205 | same three shared records |
| GOF3D iOS research | 9/9/9 | 115/115/115 of 127 | 12 isolated legacy-strip layouts |

No uncontrolled crash occurred. GOF3D remains profile-isolated.

## Browser and package gates

The shared platform-neutral game-data, dependency, mission and AEM-authoring services compile in the
trimmed WebAssembly publish. The current publish contains 206 files and 32,629,080 bytes (31.12
MiB). The dedicated UI added in this continuation is desktop Avalonia UI; equivalent full
dependency/Mission/Studio browser presentation remains a follow-up.

The CDP harness now hard-cancels and aborts timed-out connect/send/receive operations instead of
leaving an outstanding WebSocket receive during disposal. A fresh physical Brave 150.1.92.144 run
against the static publish passed: WebGL 2 rendered two meshes in six draw calls, camera orbit moved
frame 1 to frame 2 at 0.5 ms for the measured frame, maximum texture size was 16,384, and a forced
context loss restored after one recorded loss. The 105,268-byte real screenshot is under the ignored
`work/screenshots/browser-webgl-resume.png`. NuGet reported no known vulnerable or deprecated direct
or transitive package in any solution project.

## Visual evidence

Screenshots were captured from the running Windows application under ignored `work/screenshots/`:

- `semantic-mission-aem-authoring.png`: synthetic three-submesh v4 Studio, realtime viewport,
  hierarchy, geometry tools, timeline, validation/Inspector, and empty Problems pane;
- `dependency-activity.png`: live 11,440-node / 43,577-edge PC dependency activity;
- `mission-explorer.png`: campaign/freelance evidence and LevelScript/native handlers beside a real
  textured AEM document.

They are local validation evidence and are not committed because two views also contain proprietary
asset names/rendered content.

## Exact commands

```powershell
dotnet build GalaxyOnFire2Workshop.sln -c Release --no-restore
dotnet test GalaxyOnFire2Workshop.sln -c Release --no-build
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- `
  bin-matrix data android_data ios_data macos_data `
  --json work/bin-support-matrix.json --markdown docs/compatibility/bin-support-matrix.md
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- `
  compare-corpora data android_data ios_data macos_data ios_data2 `
  --json work/cross-platform-latest.json
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- `
  dependency-report data --profile gof2-pc-1x --json work/dependency-pc.json
dotnet run --project src/Gof2Workshop.Testbed -c Release --no-build -- `
  validate-corpus data --decode --roundtrip --profile gof2-pc-1x --json work/validate-pc.json
dotnet publish src/Gof2Workshop.Browser/Gof2Workshop.Browser.csproj `
  -c Release --no-restore -o work/browser-publish
.\scripts\serve-browser.ps1 -Directory work/browser-publish -Port 5238
.\scripts\browser-smoke.ps1 `
  -Executable 'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe' `
  -Url 'http://127.0.0.1:5238/?smoke=1' `
  -Screenshot work\screenshots\browser-webgl-resume.png
dotnet list GalaxyOnFire2Workshop.sln package --vulnerable --include-transitive
dotnet list GalaxyOnFire2Workshop.sln package --deprecated --include-transitive
dotnet run --project src/Gof2Workshop.App -c Release --no-build -- `
  --asset-root data --new-aem-template MultiSubmeshShip
```

Equivalent `validate-corpus` and `dependency-report` commands were run for Android, iOS, and macOS;
the isolated GOF3D profile was corpus-validated but was not fed into GOF2 semantic graph rules.

## Remaining limitations

- Unknown BIN meanings remain raw; record creation is disabled and no semantic edit is claimed
  in-game validated.
- Filename material candidates remain heuristic. Workspace choices are viewer/export mappings unless
  a separate external game-effective reference is proven.
- Mission writing and executable/LevelScript modification are disabled.
- AEM authoring targets PC v4/v5 only. It does not support skinning, armatures, morphs, arbitrary
  hierarchy, vertex sculpting, or nonlinear glTF interpolation. Oversized triangle geometry is
  split safely, but preservation of unsupported hierarchy or rigging remains deliberately blocked.
- Import conversion controls are global to the selected import batch. Preflight is per primitive,
  while per-primitive conversion overrides remain a follow-up UX improvement.
- Browser projects compile and platform-neutral services are shared, but the new desktop-only graph
  canvas/New AEM dialog were not physically browser-automated in this continuation.
- The measured 632.5 MiB desktop working set included a restored multi-document session, a full
  corpus graph, textures, and an extra synthetic OpenGL authoring viewport. It is not a leak claim,
  but lazy graph/locale node materialization is the next memory target.

## Recommended next milestone

Make dependency construction lazy for locale entries and atlas regions, and add per-primitive import
conversion overrides. Mission work should next validate existing wanted-field changes in a
user-controlled game session; campaign creation should remain gated.
