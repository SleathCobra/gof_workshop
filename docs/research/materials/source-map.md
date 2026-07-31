# Material relationship source map

No material-name or texture-path field has been confirmed inside AEM v1-v5. Workshop rendering therefore resolves external AEI dependencies through evidence-ranked strategies and records the result instead of mutating AEM.

Resolution order currently implemented:

1. persisted exact workspace override;
2. exact file-stem family match;
3. known suffix/prefix normalization;
4. neighboring-directory/name heuristic;
5. unresolved placeholder.

Each edge records source, confidence, candidates, selected asset, reason and warnings. Workspace overrides are viewer/export mappings unless separately proven game-effective.
