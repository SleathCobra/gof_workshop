using System.Numerics;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Import;

public enum ModelImportSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ModelImportDiagnostic(
    ModelImportSeverity Severity,
    string Code,
    string Message);

public sealed record ImportedPrimitive(
    string Name,
    Vector3[] Positions,
    Vector3[]? Normals,
    Vector2[]? TextureCoordinates,
    Vector4[]? Colors,
    ushort[] Indices,
    string? MaterialName);

public sealed record ImportedScene(
    string Name,
    IReadOnlyList<ImportedPrimitive> Primitives,
    IReadOnlyList<ModelImportDiagnostic> Diagnostics,
    string SourceCoordinateConvention);

public sealed record AemAuthoringOptions(
    AemVersion Version = AemVersion.V4,
    bool GenerateMissingNormals = true,
    bool BakeNodeTransforms = true,
    bool UseGeometryCenterAsPivot = false);

public sealed record AemAuthoringResult(
    AemFile File,
    AemFile Reparsed,
    SceneDocument Scene,
    byte[] Bytes,
    IReadOnlyList<ModelImportDiagnostic> Diagnostics);
