using System.Numerics;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Import;

public sealed class AemAuthoringService
{
    public AemAuthoringResult Author(
        AemAuthoringProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        AemAuthoringProjectSnapshot source = project.Current;
        ImportedScene imported = new(
            source.Name,
            source.Submeshes.Select(value => value.Geometry).ToArray(),
            [],
            "Workshop authoring coordinates: right-handed, Y-up, metres");
        AemAuthoringResult initial = Author(
            imported,
            new AemAuthoringOptions(source.Version),
            cancellationToken);
        AemSubmesh[] authored = initial.File.Submeshes
            .Select((value, index) => value with
            {
                Pivot = source.Submeshes[index].Pivot,
                BoundingSphere = source.Submeshes[index].Bounds,
                Animation = AemAuthoringProject.ToAnimation(source.Submeshes[index], source.Version),
            })
            .ToArray();
        AemFile file = initial.File with { Submeshes = authored };
        using MemoryStream output = new();
        new AemWriter().Write(file, output, cancellationToken);
        byte[] bytes = output.ToArray();
        using MemoryStream input = new(bytes, writable: false);
        AemFile reparsed = new AemParser().Parse(input, source.Name + ".aem", AemParserOptions.Pc1X, cancellationToken);
        SceneDocument scene = new AemSceneConverter().Convert(reparsed);
        if (reparsed.Submeshes.Count != source.Submeshes.Count ||
            reparsed.Submeshes.SelectMany(value => value.Positions).Any(position =>
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)))
        {
            throw new InvalidDataException("Authoring validation failed after writer reparse.");
        }

        return new AemAuthoringResult(file, reparsed, scene, bytes, initial.Diagnostics);
    }

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

        if (imported.Animations is { Count: > 0 })
        {
            AemAuthoringProject animated = new(imported.Name, options.Version);
            animated.AddImportedScene(imported);
            return Author(animated, cancellationToken);
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

        if (primitive.Normals is { } normals &&
            (normals.Length != primitive.Positions.Length || normals.Any(value => !IsFinite(value))))
        {
            throw new InvalidDataException($"Primitive '{primitive.Name}' has invalid or non-finite normals.");
        }

        if (primitive.TextureCoordinates is { } uvs &&
            (uvs.Length != primitive.Positions.Length || uvs.Any(value => !float.IsFinite(value.X) || !float.IsFinite(value.Y))))
        {
            throw new InvalidDataException($"Primitive '{primitive.Name}' has invalid or non-finite UV coordinates.");
        }

        if (primitive.Colors is { } colors &&
            (colors.Length != primitive.Positions.Length || colors.Any(value =>
                !float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
                !float.IsFinite(value.Z) || !float.IsFinite(value.W))))
        {
            throw new InvalidDataException($"Primitive '{primitive.Name}' has invalid auxiliary float4 values.");
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
