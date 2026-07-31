using System.Numerics;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Import;

public sealed class AemAuthoringService
{
    public AemAuthoringResult Author(
        ImportedScene imported,
        AemAuthoringOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        options ??= new AemAuthoringOptions();
        if (options.Version is not (AemVersion.V4 or AemVersion.V5))
        {
            throw new NotSupportedException("Custom model authoring currently targets AEM v4 or v5 only.");
        }

        List<ModelImportDiagnostic> diagnostics = [.. imported.Diagnostics];
        bool hasUv = imported.Primitives.All(primitive => primitive.TextureCoordinates is not null);
        bool hasColors = imported.Primitives.All(primitive => primitive.Colors is not null);
        if (imported.Primitives.Any(primitive => primitive.TextureCoordinates is not null) && !hasUv)
        {
            throw new InvalidDataException("AEM flags apply to every submesh; UVs must be present on all or none of the imported primitives.");
        }

        if (imported.Primitives.Any(primitive => primitive.Colors is not null) && !hasColors)
        {
            diagnostics.Add(new ModelImportDiagnostic(
                ModelImportSeverity.Warning,
                "AEM_AUXILIARY_OMITTED",
                "Mixed vertex colors were not authored. The AEM auxiliary float4 semantic remains provisional."));
            hasColors = false;
        }

        List<AemSubmesh> submeshes = [];
        for (int index = 0; index < imported.Primitives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportedPrimitive primitive = imported.Primitives[index];
            ValidatePrimitive(primitive);
            Vector3[] normals = primitive.Normals is not null
                ? [.. primitive.Normals]
                : options.GenerateMissingNormals
                    ? GenerateNormals(primitive.Positions, primitive.Indices)
                    : throw new InvalidDataException($"Primitive '{primitive.Name}' has no normals.");
            if (primitive.Normals is null)
            {
                diagnostics.Add(new ModelImportDiagnostic(
                    ModelImportSeverity.Information,
                    "AEM_NORMALS_GENERATED",
                    $"Generated vertex normals for '{primitive.Name}'."));
            }

            Vector3 pivot = options.UseGeometryCenterAsPivot
                ? CalculateCenter(primitive.Positions)
                : Vector3.Zero;
            AemBoundingSphere sphere = CalculateSphere(primitive.Positions);
            Vector2[]? sourceUvs = primitive.TextureCoordinates?
                .Select(uv => new Vector2(uv.X, 1f - uv.Y))
                .ToArray();
            submeshes.Add(new AemSubmesh(
                index,
                pivot,
                [.. primitive.Indices],
                [.. primitive.Positions],
                sourceUvs,
                normals,
                hasColors ? [.. primitive.Colors!] : null,
                sphere,
                CreateStaticAnimation(options.Version),
                0));
        }

        AemFlags flags = AemFlags.BaseMesh | AemFlags.Indices | AemFlags.Normals;
        if (hasUv)
        {
            flags |= AemFlags.TextureCoordinates;
        }

        if (hasColors)
        {
            flags |= AemFlags.AuxiliaryFloat4;
            diagnostics.Add(new ModelImportDiagnostic(
                ModelImportSeverity.Warning,
                "AEM_AUXILIARY_PROVISIONAL",
                "Imported COLOR_0 was stored in auxiliary float4; this is a Workshop diagnostic interpretation, not a confirmed game color semantic."));
        }

        string signature = options.Version == AemVersion.V4 ? "V4AEMesh" : "V5AEMesh";
        AemFile file = new(
            null,
            ProfileCatalog.Pc1X.Id,
            signature,
            options.Version,
            flags,
            submeshes,
            [],
            [],
            [],
            null,
            []);
        using MemoryStream output = new();
        new AemWriter().Write(file, output, cancellationToken);
        byte[] bytes = output.ToArray();
        using MemoryStream input = new(bytes, writable: false);
        AemFile reparsed = new AemParser().Parse(input, "authored.aem", AemParserOptions.Pc1X, cancellationToken);
        SceneDocument scene = new AemSceneConverter().Convert(reparsed);
        if (reparsed.Submeshes.Count != imported.Primitives.Count ||
            reparsed.Submeshes.Sum(value => value.Positions.Length) != imported.Primitives.Sum(value => value.Positions.Length) ||
            reparsed.Submeshes.Sum(value => value.Indices.Length) != imported.Primitives.Sum(value => value.Indices.Length))
        {
            throw new InvalidDataException("Authored AEM changed primitive, vertex, or index counts after reparse.");
        }

        return new AemAuthoringResult(file, reparsed, scene, bytes, diagnostics);
    }

    private static void ValidatePrimitive(ImportedPrimitive primitive)
    {
        if (primitive.Positions.Length == 0 || primitive.Positions.Length > ushort.MaxValue ||
            primitive.Indices.Length == 0 || primitive.Indices.Length > ushort.MaxValue ||
            primitive.Indices.Length % 3 != 0 ||
            primitive.Indices.Any(value => value >= primitive.Positions.Length))
        {
            throw new InvalidDataException($"Primitive '{primitive.Name}' exceeds AEM triangle/16-bit limits.");
        }

        if (primitive.Positions.Any(value => !IsFinite(value)))
        {
            throw new InvalidDataException($"Primitive '{primitive.Name}' contains non-finite positions.");
        }
    }

    private static Vector3[] GenerateNormals(Vector3[] positions, ushort[] indices)
    {
        Vector3[] normals = new Vector3[positions.Length];
        for (int index = 0; index < indices.Length; index += 3)
        {
            ushort ia = indices[index];
            ushort ib = indices[index + 1];
            ushort ic = indices[index + 2];
            Vector3 normal = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
            if (normal.LengthSquared() > 1e-12f)
            {
                normals[ia] += normal;
                normals[ib] += normal;
                normals[ic] += normal;
            }
        }

        for (int index = 0; index < normals.Length; index++)
        {
            normals[index] = normals[index].LengthSquared() < 1e-12f
                ? Vector3.UnitY
                : Vector3.Normalize(normals[index]);
        }

        return normals;
    }

    private static AemBoundingSphere CalculateSphere(Vector3[] positions)
    {
        Vector3 center = CalculateCenter(positions);
        float radius = positions.Max(value => Vector3.Distance(center, value));
        return new AemBoundingSphere(center, radius);
    }

    private static Vector3 CalculateCenter(Vector3[] positions)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        return (minimum + maximum) * 0.5f;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static AemAnimation CreateStaticAnimation(AemVersion version)
    {
        AemAnimationChannel[] channels =
        [
            AemAnimationChannel.TranslationX,
            AemAnimationChannel.TranslationY,
            AemAnimationChannel.TranslationZ,
            AemAnimationChannel.RotationX,
            AemAnimationChannel.RotationY,
            AemAnimationChannel.RotationZ,
            AemAnimationChannel.ScaleX,
            AemAnimationChannel.ScaleY,
            AemAnimationChannel.ScaleZ,
        ];
        return new AemAnimation(
            0,
            0,
            0,
            -1,
            version == AemVersion.V5 ? (short)0 : null,
            0,
            channels.Select(channel => new AemAnimationCurve(channel, [])).ToArray(),
            [],
            0);
    }
}
