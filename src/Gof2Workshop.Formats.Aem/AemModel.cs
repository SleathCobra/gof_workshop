using System.Numerics;
using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aem;

public enum AemVersion
{
    V1 = 1,
    V2 = 2,
    V3 = 3,
    V4 = 4,
    V5 = 5,
}

[Flags]
public enum AemFlags : byte
{
    BaseMesh = 1 << 0,
    TextureCoordinates = 1 << 1,
    Normals = 1 << 2,
    AuxiliaryFloat4 = 1 << 3,
    Indices = 1 << 4,
}

public enum AemAnimationChannel
{
    TranslationX,
    TranslationY,
    TranslationZ,
    TranslationXyz,
    RotationX,
    RotationY,
    RotationZ,
    RotationXyz,
    ScaleX,
    ScaleY,
    ScaleZ,
    ScaleXyz,
    SpecialV4,
    UvOffsetX,
    UvOffsetY,
    UvScaleX,
    UvScaleY,
    UnknownV5A,
    UnknownV5B,
    UvRotationZ,
}

public enum AemPrimitiveTopology
{
    Triangles,
    TriangleStrips,
}

public sealed record AemAnimationKey(float Time, Vector3 Value, int ComponentCount);

public sealed record AemAnimationCurve(
    AemAnimationChannel Channel,
    IReadOnlyList<AemAnimationKey> Keys);

public sealed record AemAnimation(
    ushort TranslationStorage,
    ushort RotationStorage,
    ushort ScaleStorage,
    short SpecialV4Type,
    short? V5UvMarker,
    short Padding,
    IReadOnlyList<AemAnimationCurve> Curves,
    byte[] RawData,
    long OriginalOffset);

public sealed record AemBoundingSphere(Vector3 Center, float Radius);

public sealed record AemSubmesh(
    int Index,
    Vector3 Pivot,
    ushort[] Indices,
    Vector3[] Positions,
    Vector2[]? TextureCoordinates,
    Vector3[]? Normals,
    Vector4[]? AuxiliaryFloat4,
    AemBoundingSphere BoundingSphere,
    AemAnimation Animation,
    long OriginalOffset,
    AemPrimitiveTopology SourceTopology = AemPrimitiveTopology.Triangles,
    bool IsTransparent = false,
    ushort[]? SourceIndices = null,
    ushort[]? SourceStripLengths = null);

public sealed record AemFile(
    string? SourcePath,
    string ProfileId,
    string Signature,
    AemVersion Version,
    AemFlags Flags,
    IReadOnlyList<AemSubmesh> Submeshes,
    byte[] RawHeader,
    byte[] UnknownTrailingData,
    IReadOnlyList<FormatDiagnostic> Diagnostics,
    ParseTrace? Trace,
    byte[] OriginalData);

public sealed record AemParserOptions(
    AssetPlatformProfile Profile,
    bool ResearchDiagnostics = false,
    int MaximumSubmeshCount = 4_096,
    int MaximumVertexCountPerSubmesh = 65_535,
    int MaximumIndexCountPerSubmesh = 65_535,
    int MaximumAnimationKeysPerCurve = 65_535,
    int MaximumTrailingBytes = 16 * 1024 * 1024)
{
    public static AemParserOptions Pc1X { get; } = new(ProfileCatalog.Pc1X);
}
