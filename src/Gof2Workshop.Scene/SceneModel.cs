using System.Numerics;
using Gof2Workshop.Core;

namespace Gof2Workshop.Scene;

public sealed record SceneBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Center => (Minimum + Maximum) * 0.5f;

    public Vector3 Size => Maximum - Minimum;
}

public sealed record SceneMaterial(
    string Name,
    Vector4 BaseColor);

public sealed record ScenePrimitive(
    string Name,
    Vector3[] Positions,
    Vector3[]? Normals,
    Vector2[]? TextureCoordinates,
    Vector4[]? Colors,
    ushort[] Indices,
    SceneMaterial Material,
    Vector3 SourcePivot,
    Vector3 BoundingSphereCenter,
    float BoundingSphereRadius);

public sealed record SceneTransformKey(
    float TimeSeconds,
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale);

public sealed record SceneAnimationTrack(
    int PrimitiveIndex,
    IReadOnlyList<SceneTransformKey> Keys,
    bool HasTranslation,
    bool HasRotation,
    bool HasScale);

public sealed record SceneAnimationClip(
    string Name,
    float DurationSeconds,
    IReadOnlyList<SceneAnimationTrack> Tracks,
    string SourceTimeUnit,
    IReadOnlyList<string> Limitations);

public sealed record SceneDocument(
    string Name,
    string SourceCoordinateConvention,
    string NormalizedCoordinateConvention,
    IReadOnlyList<ScenePrimitive> Primitives,
    SceneBounds Bounds,
    IReadOnlyList<FormatDiagnostic> Diagnostics,
    IReadOnlyList<SceneAnimationClip> Animations);

public readonly record struct SceneTransform(
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale)
{
    public static SceneTransform Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One);

    public Vector3 TransformPosition(Vector3 value, Vector3 pivot)
    {
        Vector3 local = (value - pivot) * Scale;
        return Vector3.Transform(local, Rotation) + pivot + Translation;
    }

    public Vector3 TransformDirection(Vector3 value)
    {
        Vector3 scaled = new(
            Scale.X == 0 ? 0 : value.X / Scale.X,
            Scale.Y == 0 ? 0 : value.Y / Scale.Y,
            Scale.Z == 0 ? 0 : value.Z / Scale.Z);
        return scaled.LengthSquared() < 1e-12f
            ? Vector3.Zero
            : Vector3.Normalize(Vector3.Transform(scaled, Rotation));
    }
}

public sealed record WindingStatistics(
    long TriangleCount,
    long AlignedWithNormals,
    long ReversedAgainstNormals,
    long DegenerateOrUnclassified)
{
    public string Interpretation => TriangleCount == 0
        ? "No triangles"
        : AlignedWithNormals > ReversedAgainstNormals
            ? "Stored winding is predominantly aligned with vertex normals."
            : ReversedAgainstNormals > AlignedWithNormals
                ? "Stored winding is predominantly reversed against vertex normals."
                : "Winding is inconclusive.";
}
