# Mission research limitations

- No declarative mission record container is confirmed.
- Trigger, objective and state transitions are largely executable behavior.
- Save serialization of mission objects is not reconstructed safely.
- Campaign stage numbers differ by product/version.
- New-mission capacity and reference allocation are not data-only facts.
- Runtime-hook approaches are outside this milestone and platform-specific.

Therefore mission creation, writing and graph editing remain disabled. The smallest useful next experiment is a read-only, versioned save-record parser validated against user-created before/after saves, followed by correlation of mission object fields and campaign status. This must not begin with a writer.
