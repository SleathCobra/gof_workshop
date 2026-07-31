# Mission state model

Confirmed behavioral facts:

- `Status` tracks a numeric current campaign mission and separate current freelance/campaign mission objects.
- `LevelScript` processes campaign-specific state, timers, flags, events and cutscene geometry.
- objectives are evaluated against live `Level` state rather than a discovered declarative objective table.
- save loading restores a campaign index and mission objects, then applies version/stage repair cases.

Strong conclusion: GOF2 campaign progression is predominantly executable state-machine logic, while side missions are procedurally constructed runtime objects. A generic editable mission graph cannot yet reproduce this behavior.
