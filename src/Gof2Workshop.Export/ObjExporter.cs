using System.Globalization;
using System.Numerics;
using System.Text;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Export;

public sealed record ObjExportResult(string ObjPath, string MtlPath);

public sealed class ObjExporter
{
    public ObjExportResult Export(
        SceneDocument scene,
        string outputDirectory,
        string? baseName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string safeBaseName = SanitizeFileName(baseName ?? scene.Name);
        string objPath = Path.Combine(outputDirectory, safeBaseName + ".obj");
        string mtlPath = Path.Combine(outputDirectory, safeBaseName + ".mtl");
        WriteMaterials(scene, mtlPath, cancellationToken);

        using StreamWriter writer = new(
            objPath,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.WriteLine("# Galaxy on Fire 2 Workshop OBJ export");
        writer.WriteLine($"# Source convention: {scene.SourceCoordinateConvention}");
        writer.WriteLine($"# Normalized convention: {scene.NormalizedCoordinateConvention}");
        writer.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");

        int positionBase = 1;
        int textureBase = 1;
        int normalBase = 1;
        foreach (ScenePrimitive primitive in scene.Primitives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine();
            writer.WriteLine($"o {SanitizeIdentifier(primitive.Name)}");

            foreach (Vector3 position in primitive.Positions)
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"v {position.X:R} {position.Y:R} {position.Z:R}"));
            }

            if (primitive.TextureCoordinates is not null)
            {
                foreach (Vector2 uv in primitive.TextureCoordinates)
                {
                    writer.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"vt {uv.X:R} {uv.Y:R}"));
                }
            }

            if (primitive.Normals is not null)
            {
                foreach (Vector3 normal in primitive.Normals)
                {
                    writer.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"vn {normal.X:R} {normal.Y:R} {normal.Z:R}"));
                }
            }

            writer.WriteLine($"g {SanitizeIdentifier(primitive.Name)}");
            writer.WriteLine($"usemtl {SanitizeIdentifier(primitive.Material.Name)}");
            for (int index = 0; index + 2 < primitive.Indices.Length; index += 3)
            {
                int a = primitive.Indices[index] + positionBase;
                int b = primitive.Indices[index + 1] + positionBase;
                int c = primitive.Indices[index + 2] + positionBase;
                writer.WriteLine("f "
                    + FaceElement(a, primitive.TextureCoordinates is not null, textureBase, normalBase, positionBase, primitive.Normals is not null)
                    + " "
                    + FaceElement(b, primitive.TextureCoordinates is not null, textureBase, normalBase, positionBase, primitive.Normals is not null)
                    + " "
                    + FaceElement(c, primitive.TextureCoordinates is not null, textureBase, normalBase, positionBase, primitive.Normals is not null));
            }

            positionBase += primitive.Positions.Length;
            if (primitive.TextureCoordinates is not null)
            {
                textureBase += primitive.TextureCoordinates.Length;
            }

            if (primitive.Normals is not null)
            {
                normalBase += primitive.Normals.Length;
            }
        }

        return new ObjExportResult(objPath, mtlPath);
    }

    private static void WriteMaterials(
        SceneDocument scene,
        string path,
        CancellationToken cancellationToken)
    {
        using StreamWriter writer = new(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.WriteLine("# Galaxy on Fire 2 Workshop material placeholders");
        foreach (SceneMaterial material in scene.Primitives.Select(primitive => primitive.Material).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine();
            writer.WriteLine($"newmtl {SanitizeIdentifier(material.Name)}");
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Ka {material.BaseColor.X * 0.15f:R} {material.BaseColor.Y * 0.15f:R} {material.BaseColor.Z * 0.15f:R}"));
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Kd {material.BaseColor.X:R} {material.BaseColor.Y:R} {material.BaseColor.Z:R}"));
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"d {material.BaseColor.W:R}"));
            writer.WriteLine("illum 2");
        }
    }

    private static string FaceElement(
        int positionIndex,
        bool hasTextures,
        int textureBase,
        int normalBase,
        int positionBase,
        bool hasNormals)
    {
        int localIndex = positionIndex - positionBase;
        int textureIndex = textureBase + localIndex;
        int normalIndex = normalBase + localIndex;
        return (hasTextures, hasNormals) switch
        {
            (true, true) => $"{positionIndex}/{textureIndex}/{normalIndex}",
            (true, false) => $"{positionIndex}/{textureIndex}",
            (false, true) => $"{positionIndex}//{normalIndex}",
            _ => positionIndex.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "scene" : sanitized;
    }

    private static string SanitizeIdentifier(string value)
    {
        return string.Concat(value.Select(character => char.IsWhiteSpace(character) ? '_' : character));
    }
}
