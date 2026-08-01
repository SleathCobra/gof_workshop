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
    string? MaterialName,
    int SourceNodeIndex = -1,
    string? SourceNodeName = null,
    string? StableId = null);

public sealed record ImportedVectorKey(float TimeSeconds, Vector3 Value);

public sealed record ImportedQuaternionKey(float TimeSeconds, Quaternion Value);

public sealed record ImportedAnimationTrack(
    int TargetNodeIndex,
    string TargetName,
    IReadOnlyList<ImportedVectorKey> Translations,
    IReadOnlyList<ImportedQuaternionKey> Rotations,
    IReadOnlyList<ImportedVectorKey> Scales);

public sealed record ImportedAnimation(
    string Name,
    IReadOnlyList<ImportedAnimationTrack> Tracks,
    float DurationSeconds);

public sealed record ImportedScene(
    string Name,
    IReadOnlyList<ImportedPrimitive> Primitives,
    IReadOnlyList<ModelImportDiagnostic> Diagnostics,
    string SourceCoordinateConvention,
    IReadOnlyList<ImportedAnimation>? Animations = null);

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
