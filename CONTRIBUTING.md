# Contributing

Thank you for helping build the Galaxy on Fire 2 Workshop. Parser correctness, clean-room provenance, and keeping proprietary data out of Git are the first priorities.

## Before changing a format parser

1. Read `docs/research/provenance.md` and the relevant format note.
2. Describe the factual observation, source, signature/version, and byte offsets.
3. Independently validate the observation against synthetic data or a local ignored sample.
4. Write an original C# implementation. Do not paste or mechanically translate third-party code.
5. Preserve unknown bytes and existing metadata when practical.
6. Add a synthetic regression test that contains no copyrighted data.
7. Update the compatibility and provenance documentation.

GPL/AGPL code must not be copied into the MIT projects. If a future feature genuinely requires copyleft code, discuss a separate process/component and explicit license boundary before implementation.

## Proprietary asset policy

Never commit:

- game assets or executables;
- extracted archives;
- decoded textures or model exports;
- byte-for-byte excerpts used as fixtures;
- golden files derived from game content;
- user-specific absolute paths.

Use ignored `data/` for local originals and ignored `work/` for generated output. Parsers and tests must never modify `data/`.

## Build and test

```powershell
dotnet restore GalaxyOnFire2Workshop.sln
dotnet format GalaxyOnFire2Workshop.sln --verify-no-changes
dotnet build GalaxyOnFire2Workshop.sln --configuration Release --no-restore
dotnet test GalaxyOnFire2Workshop.sln --configuration Release --no-build
```

Local corpus tests skip when `data/` is absent. Before claiming compatibility, also run a bounded `validate-corpus` smoke test and report the exact command and counts.

## Pull requests

Keep changes focused. Complete every applicable section of the pull-request template, especially format provenance, tested variants, license implications, and the proprietary-asset confirmation.

Malformed input must fail with a controlled `FormatParseException` or another documented validation error. New file-controlled counts and offsets need explicit limits, overflow checks, and cancellation in long loops.

