# Structured-data unknown fields and write policy

Only `.lang` framing is currently write-safe. It has no observed header, count or tail: parsing consumes complete `UInt16BE length + UTF-8 payload` records through EOF. Empty records are valid and ordering is semantic because runtime callers use numeric indices.

Unknown for `.bin` families includes the authoritative record count, every record-size discriminator, optional sections, sentinel behavior, cross-table ID constraints, capacity limits and platform-specific tails. Hex patterns and class field names are insufficient evidence for a writer. These files therefore remain immutable and are not classified as corrupt merely because the Workshop lacks a schema.

Mission creation is additionally blocked by executable campaign state logic. See the mission research notes.
