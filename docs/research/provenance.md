# Clean-room provenance

The Workshop is an independent MIT-licensed C# implementation. Local research repositories are specifications and behavioral references only. No source file from those repositories is compiled, vendored, mechanically translated, or copied into this project.

## Format discoveries

| Discovery | Research source | Independent validation | Implementation basis |
|---|---|---|---|
| AEI signature, little-endian dimensions, and rectangle order | AEPi; AEMesh ImHex pattern | Parsed all 1,228 local headers; region ranges and payload offsets were bounds-checked | Sample analysis plus factual layout |
| AEI codec identifiers and mip bit | AEPi constants/documentation | Grouped raw IDs in the local corpus; DXT payload lengths exactly match standard BC block and mip-chain sizes | Factual identifiers plus standard BC specifications |
| Raw AEI channel order | AEPi documented behavior | Visual inspection of independently generated PNG output | Behavioral test |
| PVRTC/ETC/ATC codec dispatch and RGBA order | AEPi identifiers; AssetRipper.TextureDecoder public API | Zero-block fixtures for every added codec; all 18 corpus PVRTC files decoded; representative explosion atlas visually inspected | Factual identifier mapping plus independent payload/visual tests; third-party codec invoked through a bounded adapter |
| AEI symbol-map record shape | AEPi format model | Symbol group records consume the exact post-payload bytes in local UI atlases | Sample analysis |
| AEM signatures and v4/v5 field order | AEMesh README and ImHex pattern | Byte-offset walkthrough of small v4 files and full-corpus safe parsing | Factual layout plus sample analysis |
| AEM flags for UVs, normals, and auxiliary float4 data | AEMesh tools | File-size accounting and finite/range checks across local v4/v5 files | Cross-source hypothesis validated structurally |
| AEM v4/v5 animation group shape | AEMesh structural pattern | Static tails and animated v5 files were walked independently with checked offsets | Sample analysis and behavioral comparison |
| AEM v1 triangle-strip layout and fixed-point attributes | AEMesh; DeepOpen | Independently constructed v1 fixture expands alternating strip winding and round-trips its immutable source snapshot | Factual layout plus synthetic sample analysis |
| AEM v2/v3 fixed-point geometry, bounds, and v3 animation position | AEMesh; DeepOpen | Real corpus v2 file parses/renders/exports as a four-vertex plane; synthetic v2/v3 fixtures validate numeric conversion, bounds, and animation alignment | Cross-source facts independently validated against samples |
| AEM transform key time unit | AEMesh behavioral tooling describes millisecond key times | Corpus animation durations become plausible seconds after `/1000`; a 0-to-10, 1000 ms fixture evaluates to 5 at 0.5 s and exports a glTF translation channel | Behavioral observation plus independent evaluator/export test |
| AEM scalar transform axes, rotation construction, and hierarchy | AEMesh Noesis behavior; gof2hd-decomp mesh, transform, and quaternion reconstruction | A bounded audit parsed all 752 AEM files and found 150 animated files / 660 animated submeshes, all using scalar transform storage; synthetic tests verify axis signs and long-arc interpolation | Mathematical behavior independently expressed in `AemTransformSemantics`; no source was copied or mechanically translated |
| AEM v1-v5 structural writing | Parsed field order and numeric encodings documented above | Independent writer reproduces every version fixture and all 752 parsed corpus files byte-for-byte; an edited v4 position reparses with the new value | Clean-room serialization from the independently reconstructed model; no reference implementation copied |
| AEM-to-AEI material relationship | DeepOpen resource-loading behavior; local file/folder naming | High-resolution `*_diffuse.aei` matches were inspected on real textured models; every inferred result retains its strategy and confidence | External-resource behavior plus independently tested corpus heuristics; no material field is invented in AEM |
| BC1/BC2/BC3 encoding | Public block-compression formats; BCnEncoder.Net API | Synthetic raw/BC1/BC2/BC3 reconstruction reparses and decodes; surface byte counts and container metadata are asserted | Third-party managed encoder behind a Workshop-owned interface, followed by independent decoder validation |
| macOS AEI identifier `0xA6` | Local macOS corpus byte layout; cross-platform identifier inventory | Four 64 x 384 samples have exact raw RGBA payload accounting and cube-strip dimensions; all parse, decode, and reconstruct byte-for-byte | Independent sample analysis; no reference implementation used |
| v2 AEM optional trailing transparency byte | Cross-platform sample offsets | A real v2 sample ends immediately after indices; bounded parsing and presence tracking reproduce both present and absent synthetic/real forms byte-for-byte | Independent sample analysis with explicit presence preservation |
| Cross-platform identifier/version distribution | Five ignored local corpora | Streaming inventory, anonymized hashes, all-corpus parse/decode, and unchanged writer reconstruction | Behavioral testing; no proprietary bytes or paths recorded in source |
| GOF2 language table framing | PC and Android `.lang` samples; runtime ordinal text lookup | All 22 available tables parse as complete `UInt16BE byteLength + UTF-8` sequences and reconstruct byte-for-byte; malformed UTF-8/truncation fixtures fail safely | Independent sample analysis and synthetic testing |
| Mission storage model | DeepOpen runtime classes; GOF2HD Mission/Generator/LevelScript/GameRecord behavior; five-corpus filename/signature search | No standalone mission table found; two engine reconstructions independently show procedural side-mission creation and campaign-specific executable state logic | Behavioral evidence only; no mission writer or copied implementation |
| glTF/OBJ to AEM authoring | Public glTF 2.0 and OBJ specifications; established AEM facts above | Synthetic glTF, GLB and OBJ import to AEM v4/v5, serialize, reparse, scene-convert and render with count/bounds checks | Independent bounded importer and neutral-scene conversion |
| Desktop OpenGL core portability | OpenGL desktop core/ES API requirements; Avalonia context version | Shader variants compile in tests, VAO lifecycle is explicit, and Windows ANGLE remains validated; real macOS hardware validation is still pending | Public API behavior and diagnostics; no third-party renderer source |

## License boundary

- AEPi is Apache-2.0. It is not a runtime dependency; factual format observations are cited above.
- AEIporter and KaamoClubModApi are GPL-3.0. Their code is not used.
- The local AEMesh, DeepOpen, and gof2hd-decomp copies do not contain a top-level license file identifiable during this review. They are treated only as non-redistributed research material.
- `AssetRipper.TextureDecoder` 2.6.2 is an MIT-licensed, managed, dependency-free runtime package used only for PVRTC/ETC/ATC block decoding. No source is copied into the Workshop; the adapter validates lengths and converts the package's generic RGBA values into the Workshop-owned image model.
- `BCnEncoder.Net` 2.3.0 is MIT OR Unlicense, managed, and has no native dependency. It is used only for BC1/BC2/BC3 compression; AEI container reconstruction, mip layout, validation, and pixel comparison remain Workshop-owned.
- BC decoding, PNG writing, software preview rendering, OBJ writing, and glTF writing remain independent C# implementations based on public format specifications and sample validation.
- Avalonia.Browser is confined to the browser host. Parser, scene, validation, and exporter projects remain independent of Avalonia and browser APIs.
- The model import implementation adds no third-party runtime dependency; it accepts a deliberately bounded glTF/GLB/OBJ subset from public specifications.
- `Gof2Workshop.GameData` is a platform-neutral independent implementation and has no production package dependency.
- Test-framework packages, if present, are development-only and are recorded in `docs/research/dependencies.md`.

## Contributor rule

Contributions that change a parser must describe the factual observation, source, independent validation, affected signatures/variants, and license implications. Proprietary samples stay under ignored `/data/`; generated derivatives stay under ignored `/work/`.
