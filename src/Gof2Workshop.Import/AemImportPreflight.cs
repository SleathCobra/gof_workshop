using System.Numerics;
using Gof2Workshop.Formats.Aem;

namespace Gof2Workshop.Import;

public sealed record AemImportPreflightPrimitive(
    string Name,
    int VertexCount,
    int TriangleCount,
    bool HasNormals,
    bool HasTextureCoordinates,
    bool HasAuxiliaryFloat4,
    string Material,
    bool IsRepresentable,
    string Summary);

public sealed record AemImportPreflightReport(
    string SourceName,
    string CoordinateConvention,
    IReadOnlyList<AemImportPreflightPrimitive> Primitives,
    int AnimationCount,
    IReadOnlyList<ModelImportDiagnostic> Diagnostics)
{
    public bool IsRepresentable =>
        Primitives.Count > 0 &&
        Primitives.All(value => value.IsRepresentable) &&
        Diagnostics.All(value => value.Severity != ModelImportSeverity.Error);
}

public sealed class AemImportPreflightService
{
    public AemImportPreflightReport Inspect(ImportedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        AemImportPreflightPrimitive[] primitives = scene.Primitives.Select(InspectPrimitive).ToArray();
        return new AemImportPreflightReport(
            scene.Name,
            scene.SourceCoordinateConvention,
            primitives,
            scene.Animations?.Count ?? 0,
            scene.Diagnostics);
    }

    public AemImportPreflightReport Inspect(AemFile file, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        AemImportPreflightPrimitive[] primitives = file.Submeshes.Select(value =>
        {
            bool representable = value.Positions.Length is > 0 and <= ushort.MaxValue &&
                value.Indices.Length is > 0 and <= ushort.MaxValue &&
                value.Indices.Length % 3 == 0 &&
                value.Indices.All(index => index < value.Positions.Length);
            return new AemImportPreflightPrimitive(
                $"submesh_{value.Index:D2}",
                value.Positions.Length,
                value.Indices.Length / 3,
                value.Normals is not null,
                value.TextureCoordinates is not null,
                value.AuxiliaryFloat4 is not null,
                "External mapping",
                representable,
                representable
                    ? "Source AEM submesh fits the selected writer's 16-bit limits."
                    : "Source AEM submesh is not representable by the selected PC writer.");
        }).ToArray();
        return new AemImportPreflightReport(
            sourceName,
            $"AEM v{(int)file.Version}; source pivot/coordinate convention preserved",
            primitives,
            file.Submeshes.Count(value => value.Animation is not null),
            []);
    }

    private static AemImportPreflightPrimitive InspectPrimitive(ImportedPrimitive primitive)
    {
        bool channelCounts =
            (primitive.Normals is null || primitive.Normals.Length == primitive.Positions.Length) &&
            (primitive.TextureCoordinates is null || primitive.TextureCoordinates.Length == primitive.Positions.Length) &&
            (primitive.Colors is null || primitive.Colors.Length == primitive.Positions.Length);
        bool finite = primitive.Positions.All(IsFinite) &&
            (primitive.Normals is null || primitive.Normals.All(IsFinite)) &&
            (primitive.TextureCoordinates is null || primitive.TextureCoordinates.All(value =>
                float.IsFinite(value.X) && float.IsFinite(value.Y))) &&
            (primitive.Colors is null || primitive.Colors.All(value =>
                float.IsFinite(value.X) && float.IsFinite(value.Y) &&
                float.IsFinite(value.Z) && float.IsFinite(value.W)));
        bool topology = primitive.Indices.Length is > 0 and <= ushort.MaxValue &&
            primitive.Indices.Length % 3 == 0 &&
            primitive.Positions.Length is > 0 and <= ushort.MaxValue &&
            primitive.Indices.All(value => value < primitive.Positions.Length);
        bool representable = channelCounts && finite && topology;
        string summary = representable
            ? "Triangle topology fits the AEM 16-bit vertex/index limits."
            : "The primitive has invalid channels, non-finite data, or exceeds an AEM 16-bit limit.";
        return new AemImportPreflightPrimitive(
            primitive.Name,
            primitive.Positions.Length,
            primitive.Indices.Length / 3,
            primitive.Normals is not null,
            primitive.TextureCoordinates is not null,
            primitive.Colors is not null,
            primitive.MaterialName ?? "Unassigned",
            representable,
            summary);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal static class ImportedPrimitiveChunker
{
    public static IReadOnlyList<ImportedPrimitive> Split(
        string name,
        Vector3[] positions,
        Vector3[]? normals,
        Vector2[]? textureCoordinates,
        Vector4[]? colors,
        uint[] indices,
        string? materialName,
        int sourceNodeIndex,
        string? sourceNodeName,
        string? stableId)
    {
        ValidateSource(positions, normals, textureCoordinates, colors, indices);
        List<ImportedPrimitive> chunks = [];
        Dictionary<uint, ushort> remap = [];
        List<Vector3> chunkPositions = [];
        List<Vector3>? chunkNormals = normals is null ? null : [];
        List<Vector2>? chunkUvs = textureCoordinates is null ? null : [];
        List<Vector4>? chunkColors = colors is null ? null : [];
        List<ushort> chunkIndices = [];

        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            int additions = 0;
            for (int corner = 0; corner < 3; corner++)
            {
                if (!remap.ContainsKey(indices[triangle + corner]))
                {
                    additions++;
                }
            }

            if (chunkIndices.Count > 0 &&
                (chunkIndices.Count + 3 > ushort.MaxValue || remap.Count + additions > ushort.MaxValue))
            {
                Flush();
            }

            for (int corner = 0; corner < 3; corner++)
            {
                uint sourceIndex = indices[triangle + corner];
                if (!remap.TryGetValue(sourceIndex, out ushort localIndex))
                {
                    localIndex = checked((ushort)remap.Count);
                    remap.Add(sourceIndex, localIndex);
                    int source = checked((int)sourceIndex);
                    chunkPositions.Add(positions[source]);
                    chunkNormals?.Add(normals![source]);
                    chunkUvs?.Add(textureCoordinates![source]);
                    chunkColors?.Add(colors![source]);
                }
                chunkIndices.Add(localIndex);
            }
        }

        Flush();
        if (chunks.Count <= 1)
        {
            return chunks;
        }

        return chunks.Select((value, index) => value with
        {
            Name = $"{name}_part{index + 1:D2}",
            StableId = stableId is null ? null : $"{stableId}-part-{index + 1:D2}",
        }).ToArray();

        void Flush()
        {
            if (chunkIndices.Count == 0)
            {
                return;
            }

            chunks.Add(new ImportedPrimitive(
                name,
                chunkPositions.ToArray(),
                chunkNormals?.ToArray(),
                chunkUvs?.ToArray(),
                chunkColors?.ToArray(),
                chunkIndices.ToArray(),
                materialName,
                sourceNodeIndex,
                sourceNodeName,
                stableId));
            remap.Clear();
            chunkPositions.Clear();
            chunkNormals?.Clear();
            chunkUvs?.Clear();
            chunkColors?.Clear();
            chunkIndices.Clear();
        }
    }

    private static void ValidateSource(
        Vector3[] positions,
        Vector3[]? normals,
        Vector2[]? textureCoordinates,
        Vector4[]? colors,
        uint[] indices)
    {
        if (positions.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0)
        {
            throw new InvalidDataException("AEM import requires non-empty triangle geometry.");
        }
        if (indices.Any(value => value >= positions.Length))
        {
            throw new InvalidDataException("A primitive index is outside its position array.");
        }
        if ((normals is not null && normals.Length != positions.Length) ||
            (textureCoordinates is not null && textureCoordinates.Length != positions.Length) ||
            (colors is not null && colors.Length != positions.Length))
        {
            throw new InvalidDataException("Primitive channel counts do not match its positions.");
        }
    }
}
