# AEM material evidence

All parsed AEM versions provide geometry/submesh separation and optional UVs, normals and an auxiliary float4 channel. Full byte accounting across five corpora did not reveal a stable embedded texture filename or material structure. The auxiliary float4 remains a diagnostic attribute; it is not labeled as vertex color semantics in the source format.

Unknown/trailing bytes are preserved by the AEM model/writer. They are not repurposed as invented material fields.
