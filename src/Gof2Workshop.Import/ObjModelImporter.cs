using System.Globalization;
using System.Numerics;

namespace Gof2Workshop.Import;

public sealed class ObjModelImporter
{
    public ImportedScene Import(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Import(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), cancellationToken);
    }

    public ImportedScene Import(string text, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Vector3> sourcePositions = [];
        List<Vector2> sourceUvs = [];
        List<Vector3> sourceNormals = [];
        List<Vector3> positions = [];
        List<Vector2> uvs = [];
        List<Vector3> normals = [];
        List<ushort> indices = [];
        Dictionary<VertexKey, ushort> vertices = [];
        string? material = null;

        foreach (string untrimmed in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = untrimmed.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    sourcePositions.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                    break;
                case "vt" when parts.Length >= 3:
                    sourceUvs.Add(new Vector2(Parse(parts[1]), Parse(parts[2])));
                    break;
                case "vn" when parts.Length >= 4:
                    sourceNormals.Add(Vector3.Normalize(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3]))));
                    break;
                case "usemtl" when parts.Length >= 2:
                    material ??= string.Join('_', parts.Skip(1));
                    break;
                case "f" when parts.Length >= 4:
                    VertexKey[] face = parts.Skip(1)
                        .Select(value => ParseKey(value, sourcePositions.Count, sourceUvs.Count, sourceNormals.Count))
                        .ToArray();
                    for (int corner = 1; corner + 1 < face.Length; corner++)
                    {
                        Add(face[0]);
                        Add(face[corner]);
                        Add(face[corner + 1]);
                    }

                    break;
            }
        }

        if (indices.Count == 0)
        {
            throw new InvalidDataException("OBJ contains no triangle faces.");
        }

        bool hasUvs = vertices.Keys.All(value => value.Uv >= 0);
        bool hasNormals = vertices.Keys.All(value => value.Normal >= 0);
        return new ImportedScene(
            name,
            [new ImportedPrimitive(name, positions.ToArray(), hasNormals ? normals.ToArray() : null, hasUvs ? uvs.ToArray() : null, null, indices.ToArray(), material)],
            [],
            "OBJ coordinates retained; polygon faces triangulated as a fan");

        void Add(VertexKey key)
        {
            if (!vertices.TryGetValue(key, out ushort index))
            {
                index = checked((ushort)vertices.Count);
                vertices.Add(key, index);
                positions.Add(sourcePositions[key.Position]);
                uvs.Add(key.Uv >= 0 ? sourceUvs[key.Uv] : Vector2.Zero);
                normals.Add(key.Normal >= 0 ? sourceNormals[key.Normal] : Vector3.Zero);
            }

            if (indices.Count == ushort.MaxValue)
            {
                throw new InvalidDataException("OBJ primitive exceeds the AEM 16-bit index-count limit.");
            }

            indices.Add(index);
        }
    }

    private static float Parse(string value)
    {
        float result = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return float.IsFinite(result)
            ? result
            : throw new InvalidDataException("OBJ contains a non-finite numeric value.");
    }

    private static VertexKey ParseKey(string value, int positions, int uvs, int normals)
    {
        string[] parts = value.Split('/');
        int position = Resolve(parts[0], positions);
        int uv = parts.Length > 1 && parts[1].Length > 0 ? Resolve(parts[1], uvs) : -1;
        int normal = parts.Length > 2 && parts[2].Length > 0 ? Resolve(parts[2], normals) : -1;
        return new VertexKey(position, uv, normal);
    }

    private static int Resolve(string value, int count)
    {
        int source = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        int index = source > 0 ? source - 1 : count + source;
        return (uint)index < (uint)count
            ? index
            : throw new InvalidDataException("OBJ face references an attribute outside its source array.");
    }

    private readonly record struct VertexKey(int Position, int Uv, int Normal);
}
