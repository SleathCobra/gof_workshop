# IDE workbench architecture

The desktop milestone adds two layers without changing the clean-room parser boundary.

## UI-independent application layer

`Gof2Workshop.Workbench` owns:

- versioned workspace and local application state;
- safe relative resolution for mod-owned paths;
- explicit rejection of export destinations beneath the game asset root;
- cancellable lightweight AEI/AEM indexing, rescan deltas, and structured scan problems;
- in-memory name/kind/support/version search;
- Output and Problems aggregation;
- document lifecycle, active-document tracking, deduplication, and restoration;
- ordered editor-provider resolution.
- validated Add to Mod/replacement staging with atomic copies and an audited operation manifest.
- operation-based AEI edit sessions, undo/redo, versioned atomic recovery, and source-hash conflicts;
- versioned distributable manifests plus validated deterministic mod builds;
- confidence-bearing AEM-to-AEI relationships and workspace material overrides.

It has no Avalonia dependency. Its services use records and interfaces that can be tested without a window.

## Avalonia presentation layer

`Gof2Workshop.App` composes:

- conventional menus and keyboard shortcuts through one command set;
- an activity rail;
- separate Mod Workspace and immutable Game Assets trees;
- a single-row, horizontally scrollable `TabStrip` document area with an all-documents picker and back/forward activation history;
- contextual Inspector groups;
- Output, Problems, and Asset Details tools;
- persisted split-pane sizes, visibility, and detachable Explorer/Inspector/bottom tool windows;
- AEI and AEM editor providers.
- a Changes activity for validated replacements, warnings, conflicts, validation, and builds.

An editor provider receives an indexed asset and returns an `IDocument`. The workbench does not switch over every future file/editor type. Mission graphs, scripts, blueprint graphs, UI designers, database editors, and documentation pages can register providers and add their own view data template.

## Selection and diagnostics flow

```text
indexed asset / tree / search result
  -> DocumentEditorRegistry
  -> AEI, AEM, or Unsupported provider
  -> DocumentManager (dedupe + active document)
  -> active IInspectorSource
  -> Inspector + Asset Details

parser diagnostics / scan warnings / export errors
  -> ProblemService + OutputService
  -> bottom tool panes
```

The game tree holds lightweight `IndexedAsset` records only. Full payloads, decoded textures, and scene buffers exist only in open documents and are released with the document bitmap when closed.

## Nondestructive boundary

Game assets are always marked original/read-only. No command overwrites a source asset. UI exports and reconstructed/snapshot copies pass through `PathPolicy.ValidateExportDestination`, which rejects the selected game root and every descendant. Add to Mod and staged replacement write only beneath a separately validated mod root, fully parse candidate files before staging, use atomic temp-file replacement, and append source/staged hashes to `.work/asset-operations.json`. AEI/AEM parsers still produce detached snapshots and the scene converter leaves raw AEM data unchanged.

## Layout decision

The shell uses grids, splitters, hideable pane hosts, and owned floating windows. Drag handles detach Explorer, Inspector, or bottom tools; closing a tool window docks the pane. Docked and floating panes are separate view instances bound to the same workbench state, which avoids moving controls between Avalonia visual roots. Floating state persists with the workspace. Layout state is independent of editor/provider logic, so arbitrary future docking zones can still map the same pane/document abstractions to a docking library.

The AEM document uses a realtime `OpenGlControlBase` viewport by default and retains the adaptive
software renderer as an explicit fallback. Both consume the same parser-neutral scene/camera and
animation state. OpenGL mesh buffers are persistent, decoded AEI mip chains are cached and uploaded
outside the per-frame path, and CPU ray picking runs only on clicks. Material resolution is an
independent service; neither renderer parses AEM or AEI containers.
