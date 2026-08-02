# Mission state model

Confirmed behavioral facts:

- `Status` tracks a numeric current campaign mission and separate current freelance/campaign mission objects.
- `LevelScript` processes campaign-specific state, timers, flags, events and cutscene geometry.
- objectives are evaluated against live `Level` state rather than a discovered declarative objective table.
- save loading restores a campaign index and mission objects, then applies version/stage repair cases.

Strong conclusion: GOF2 campaign progression is predominantly executable state-machine logic, while side missions are procedurally constructed runtime objects. A generic editable mission graph cannot yet reproduce this behavior.

## Read-only evidence flow

The Workshop visualizes only this corroborated high-level cycle:

```text
persisted campaign step
  -> native campaign mission construction
  -> LevelScript local state/timers/events
  -> native completion/progression branch
  -> persisted campaign step
```

Each transition carries its evidence and confidence. The final transition is marked strong rather
than universally linear because expansion and save-repair branches differ. Wanted contracts expose
`locked`, `available`, `active`, and `terminated` evidence states, but encounter spawning remains an
explicitly unknown runtime concern.

Freelance missions expose offered, active, success and failed lifecycle evidence. Individual offers
are not editable corpus records: the runtime constructs them procedurally.
