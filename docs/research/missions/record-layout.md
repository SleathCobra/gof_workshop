# Mission runtime record layout

This is a runtime object model, not a proven disk-table layout.

GOF2HD research exposes a `Mission` object containing failure/win flags, agent reference, integer type/id, client and target names, client image/race, costs, bonus, target station/system, reward, distance, campaign/instant-action state, two production-good values, a status value and visibility. DeepOpen independently exposes the same broad concepts in its Java reconstruction.

The Workshop does not serialize this layout. Pointer-sized members, engine strings and live object references prove that it cannot be copied directly into a portable file record. Save-game serialization remains a separate research problem.
