using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace Gof2Workshop.Import;

public sealed class GltfModelImporter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;

    public ImportedScene Import(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        return Import(bytes, Path.GetFileName(fullPath), Path.GetDirectoryName(fullPath), cancellationToken);
    }

    public ImportedScene Import(
        ReadOnlyMemory<byte> bytes,
        string name,
        string? sidecarDirectory = null,
        CancellationToken cancellationToken = default)
    {
        return ImportCore(bytes, name, sidecarDirectory, sidecarResolver: null, cancellationToken);
    }

    public ImportedScene ImportWithSidecars(
        ReadOnlyMemory<byte> bytes,
        string name,
        Func<string, byte[]?> sidecarResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sidecarResolver);
        return ImportCore(bytes, name, sidecarDirectory: null, sidecarResolver, cancellationToken);
    }

    private static ImportedScene ImportCore(
        ReadOnlyMemory<byte> bytes,
        string name,
        string? sidecarDirectory,
        Func<string, byte[]?>? sidecarResolver,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        byte[] json;
        byte[]? glbBuffer = null;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span) == GlbMagic)
        {
            (json, glbBuffer) = ReadGlb(bytes.Span);
        }
        else
        {
            json = bytes.ToArray();
        }

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 128,
            CommentHandling = JsonCommentHandling.Skip,
        });
        JsonElement root = document.RootElement;
        ValidateAsset(root);
        List<ModelImportDiagnostic> diagnostics = [];
        byte[][] buffers = ReadBuffers(root, glbBuffer, sidecarDirectory, sidecarResolver);
        JsonElement[] bufferViews = root.TryGetProperty("bufferViews", out JsonElement views)
            ? views.EnumerateArray().ToArray()
            : [];
        JsonElement[] accessors = root.TryGetProperty("accessors", out JsonElement accessorArray)
            ? accessorArray.EnumerateArray().ToArray()
            : [];
        JsonElement[] meshes = root.GetProperty("meshes").EnumerateArray().ToArray();
        List<(int Mesh, Matrix4x4 World, string Name, int Node)> instances = ReadInstances(root, meshes.Length);
        List<ImportedPrimitive> primitives = [];
        foreach ((int meshIndex, Matrix4x4 world, string nodeName, int nodeIndex) in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement mesh = meshes[meshIndex];
            string meshName = mesh.TryGetProperty("name", out JsonElement meshNameValue)
                ? meshNameValue.GetString() ?? nodeName
                : nodeName;
            int primitiveIndex = 0;
            foreach (JsonElement primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                int mode = primitive.TryGetProperty("mode", out JsonElement modeValue)
                    ? modeValue.GetInt32()
                    : 4;
                if (mode != 4)
                {
                    throw new NotSupportedException(
                        $"glTF primitive mode {mode} is unsupported; only TRIANGLES (4) can be authored to AEM.");
                }

                JsonElement attributes = primitive.GetProperty("attributes");
                Vector3[] positions = ReadVector3(
                    accessors,
                    bufferViews,
                    buffers,
                    attributes.GetProperty("POSITION").GetInt32());
                Vector3[]? normals = attributes.TryGetProperty("NORMAL", out JsonElement normalAccessor)
                    ? ReadVector3(accessors, bufferViews, buffers, normalAccessor.GetInt32())
                    : null;
                Vector2[]? uvs = attributes.TryGetProperty("TEXCOORD_0", out JsonElement uvAccessor)
                    ? ReadVector2(accessors, bufferViews, buffers, uvAccessor.GetInt32())
                    : null;
                Vector4[]? colors = attributes.TryGetProperty("COLOR_0", out JsonElement colorAccessor)
                    ? ReadVector4(accessors, bufferViews, buffers, colorAccessor.GetInt32())
                    : null;
                uint[] indices = primitive.TryGetProperty("indices", out JsonElement indexAccessor)
                    ? ReadIndices(accessors, bufferViews, buffers, indexAccessor.GetInt32())
                    : Enumerable.Range(0, positions.Length).Select(value => checked((uint)value)).ToArray();

                for (int index = 0; index < positions.Length; index++)
                {
                    positions[index] = Vector3.Transform(positions[index], world);
                    if (normals is not null)
                    {
                        Vector3 transformed = Vector3.TransformNormal(normals[index], world);
                        normals[index] = transformed.LengthSquared() < 1e-12f
                            ? Vector3.UnitY
                            : Vector3.Normalize(transformed);
                    }
                }

                string? materialName = ReadMaterialName(root, primitive);
                string primitiveName = $"{meshName}_{primitiveIndex:D2}";
                string? stableId = ReadStableId(root, nodeIndex);
                IReadOnlyList<ImportedPrimitive> chunks = ImportedPrimitiveChunker.Split(
                    primitiveName,
                    positions,
                    normals,
                    uvs,
                    colors,
                    indices,
                    materialName,
                    nodeIndex,
                    nodeName,
                    stableId);
                primitives.AddRange(chunks);
                if (chunks.Count > 1)
                {
                    diagnostics.Add(new ModelImportDiagnostic(
                        ModelImportSeverity.Warning,
                        "AEM_IMPORT_SPLIT_16_BIT",
                        $"Primitive '{primitiveName}' was split into {chunks.Count:N0} submeshes to preserve all triangles within AEM 16-bit limits."));
                }
                primitiveIndex++;
            }
        }

        if (primitives.Count == 0)
        {
            throw new InvalidDataException("The glTF contains no triangle primitives.");
        }

        IReadOnlyList<ImportedAnimation> animations = ReadAnimations(root, accessors, bufferViews, buffers);
        return new ImportedScene(
            Path.GetFileNameWithoutExtension(name),
            primitives,
            diagnostics,
            "glTF 2.0 right-handed, Y-up; node transforms baked into geometry",
            animations);
    }

    private static (byte[] Json, byte[]? Binary) ReadGlb(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2)
        {
            throw new InvalidDataException("Only glTF Binary version 2 is supported.");
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (declared != bytes.Length)
        {
            throw new InvalidDataException("GLB declared length does not match the selected file.");
        }

        byte[]? json = null;
        byte[]? binary = null;
        int offset = 12;
        while (offset < bytes.Length)
        {
            if (offset + 8 > bytes.Length)
            {
                throw new InvalidDataException("GLB chunk header is truncated.");
            }

            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            offset += 8;
            if (length < 0 || offset + length > bytes.Length)
            {
                throw new InvalidDataException("GLB chunk exceeds the file bounds.");
            }

            if (type == JsonChunk)
            {
                json = bytes.Slice(offset, length).ToArray();
            }
            else if (type == BinaryChunk)
            {
                binary = bytes.Slice(offset, length).ToArray();
            }

            offset += length;
        }

        return (json ?? throw new InvalidDataException("GLB has no JSON chunk."), binary);
    }

    private static void ValidateAsset(JsonElement root)
    {
        string? version = root.GetProperty("asset").GetProperty("version").GetString();
        if (version is null || !version.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"glTF asset version '{version}' is unsupported.");
        }

        if (!root.TryGetProperty("meshes", out _))
        {
            throw new InvalidDataException("The glTF has no meshes array.");
        }
    }

    private static byte[][] ReadBuffers(
        JsonElement root,
        byte[]? glbBuffer,
        string? directory,
        Func<string, byte[]?>? sidecarResolver)
    {
        List<byte[]> result = [];
        int index = 0;
        foreach (JsonElement buffer in root.GetProperty("buffers").EnumerateArray())
        {
            int declaredLength = buffer.GetProperty("byteLength").GetInt32();
            byte[] bytes;
            if (!buffer.TryGetProperty("uri", out JsonElement uriValue))
            {
                bytes = index == 0 && glbBuffer is not null
                    ? glbBuffer
                    : throw new InvalidDataException("A glTF buffer has no URI and no GLB binary chunk.");
            }
            else
            {
                string uri = uriValue.GetString() ?? throw new InvalidDataException("A buffer URI is null.");
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = uri.IndexOf(',');
                    if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException("Only base64 data URIs are supported for embedded glTF buffers.");
                    }

                    bytes = Convert.FromBase64String(uri[(comma + 1)..]);
                }
                else
                {
                    if (sidecarResolver is not null)
                    {
                        bytes = sidecarResolver(Uri.UnescapeDataString(uri))
                            ?? throw new InvalidDataException($"Sidecar buffer '{uri}' was not supplied.");
                    }
                    else if (directory is null)
                    {
                        throw new InvalidDataException($"Sidecar buffer '{uri}' was not supplied.");
                    }
                    else
                    {
                        string path = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(uri)));
                        if (!IsWithin(path, directory))
                        {
                            throw new InvalidDataException("A glTF sidecar path escapes its source directory.");
                        }

                        bytes = File.ReadAllBytes(path);
                    }
                }
            }

            if (bytes.Length < declaredLength)
            {
                throw new InvalidDataException($"Buffer {index} is shorter than its declared byteLength.");
            }

            result.Add(bytes);
            index++;
        }

        return result.ToArray();
    }

    private static bool IsWithin(string candidate, string root)
    {
        string fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar &&
             fullCandidate.StartsWith(
                 fullRoot + Path.AltDirectorySeparatorChar,
                 StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadStableId(JsonElement root, int nodeIndex)
    {
        if (nodeIndex < 0 || !root.TryGetProperty("nodes", out JsonElement nodes))
        {
            return null;
        }

        JsonElement[] values = nodes.EnumerateArray().ToArray();
        if ((uint)nodeIndex >= (uint)values.Length ||
            !values[nodeIndex].TryGetProperty("extras", out JsonElement extras))
        {
            return null;
        }

        if (extras.TryGetProperty("stableSubmeshId", out JsonElement stable) &&
            stable.ValueKind == JsonValueKind.String)
        {
            return stable.GetString();
        }

        return extras.TryGetProperty("sourceSubmeshIndex", out JsonElement source) && source.TryGetInt32(out int index)
            ? $"workshop-source-{index}"
            : null;
    }

    private static List<ImportedAnimation> ReadAnimations(
        JsonElement root,
        JsonElement[] accessors,
        JsonElement[] views,
        byte[][] buffers)
    {
        if (!root.TryGetProperty("animations", out JsonElement animationsValue))
        {
            return [];
        }

        JsonElement[] nodes = root.TryGetProperty("nodes", out JsonElement nodeArray)
            ? nodeArray.EnumerateArray().ToArray()
            : [];
        List<ImportedAnimation> animations = [];
        int animationIndex = 0;
        foreach (JsonElement animation in animationsValue.EnumerateArray())
        {
            JsonElement[] samplers = animation.GetProperty("samplers").EnumerateArray().ToArray();
            Dictionary<int, ImportedTrackBuilder> tracks = [];
            float duration = 0;
            foreach (JsonElement channel in animation.GetProperty("channels").EnumerateArray())
            {
                int samplerIndex = channel.GetProperty("sampler").GetInt32();
                JsonElement sampler = Get(samplers, samplerIndex, "animation sampler");
                string interpolation = sampler.TryGetProperty("interpolation", out JsonElement interpolationValue)
                    ? interpolationValue.GetString() ?? "LINEAR"
                    : "LINEAR";
                if (!string.Equals(interpolation, "LINEAR", StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        $"Animation interpolation {interpolation} is unsupported for AEM authoring; only LINEAR is representable.");
                }

                float[] times = ReadScalar(accessors, views, buffers, sampler.GetProperty("input").GetInt32());
                if (times.Length == 0 || times.Any(value => value < 0))
                {
                    throw new InvalidDataException("glTF animation input times must be finite, non-negative, and non-empty.");
                }

                duration = Math.Max(duration, times[^1]);
                JsonElement target = channel.GetProperty("target");
                int node = target.GetProperty("node").GetInt32();
                if ((uint)node >= (uint)nodes.Length)
                {
                    throw new InvalidDataException("glTF animation targets a node outside the nodes array.");
                }

                string nodeDisplayName = nodes[node].TryGetProperty("name", out JsonElement nodeName)
                    ? nodeName.GetString() ?? $"node_{node:D2}"
                    : $"node_{node:D2}";
                if (!tracks.TryGetValue(node, out ImportedTrackBuilder? builder))
                {
                    builder = new ImportedTrackBuilder(node, nodeDisplayName);
                    tracks.Add(node, builder);
                }

                int output = sampler.GetProperty("output").GetInt32();
                string path = target.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("glTF animation target path is null.");
                switch (path)
                {
                    case "translation":
                        {
                            Vector3[] values = ReadVector3(accessors, views, buffers, output);
                            RequireMatchingKeyCounts(times, values.Length, path);
                            builder.SetTranslations(times.Select((time, index) => new ImportedVectorKey(time, values[index])).ToArray());
                            break;
                        }
                    case "rotation":
                        {
                            Vector4[] values = ReadVector4(accessors, views, buffers, output);
                            RequireMatchingKeyCounts(times, values.Length, path);
                            ImportedQuaternionKey[] keys = values.Select((value, index) =>
                            {
                                Quaternion rotation = new(value.X, value.Y, value.Z, value.W);
                                if (rotation.LengthSquared() < 1e-12f)
                                {
                                    throw new InvalidDataException("glTF rotation animation contains a zero quaternion.");
                                }

                                return new ImportedQuaternionKey(times[index], Quaternion.Normalize(rotation));
                            }).ToArray();
                            builder.SetRotations(keys);
                            break;
                        }
                    case "scale":
                        {
                            Vector3[] values = ReadVector3(accessors, views, buffers, output);
                            RequireMatchingKeyCounts(times, values.Length, path);
                            builder.SetScales(times.Select((time, index) => new ImportedVectorKey(time, values[index])).ToArray());
                            break;
                        }
                    case "weights":
                        throw new NotSupportedException("Morph-target animation cannot be represented in AEM.");
                    default:
                        throw new NotSupportedException($"glTF animation target path '{path}' is unsupported.");
                }
            }

            string name = animation.TryGetProperty("name", out JsonElement animationName)
                ? animationName.GetString() ?? $"animation_{animationIndex:D2}"
                : $"animation_{animationIndex:D2}";
            animations.Add(new ImportedAnimation(name, tracks.Values.Select(value => value.Build()).ToArray(), duration));
            animationIndex++;
        }

        return animations;
    }

    private static float[] ReadScalar(JsonElement[] accessors, JsonElement[] views, byte[][] buffers, int index) =>
        ReadFloatAccessor(accessors, views, buffers, index, "SCALAR", 1).Select(value => value[0]).ToArray();

    private static void RequireMatchingKeyCounts(float[] times, int values, string path)
    {
        if (times.Length != values)
        {
            throw new InvalidDataException(
                $"glTF {path} animation has {times.Length} input times but {values} output values.");
        }
    }

    private sealed class ImportedTrackBuilder(int node, string name)
    {
        private IReadOnlyList<ImportedVectorKey> translations = [];
        private IReadOnlyList<ImportedQuaternionKey> rotations = [];
        private IReadOnlyList<ImportedVectorKey> scales = [];

        public void SetTranslations(IReadOnlyList<ImportedVectorKey> value)
        {
            if (translations.Count != 0) throw new InvalidDataException("A node has duplicate translation animation channels.");
            translations = value;
        }

        public void SetRotations(IReadOnlyList<ImportedQuaternionKey> value)
        {
            if (rotations.Count != 0) throw new InvalidDataException("A node has duplicate rotation animation channels.");
            rotations = value;
        }

        public void SetScales(IReadOnlyList<ImportedVectorKey> value)
        {
            if (scales.Count != 0) throw new InvalidDataException("A node has duplicate scale animation channels.");
            scales = value;
        }

        public ImportedAnimationTrack Build() => new(node, name, translations, rotations, scales);
    }

    private static List<(int Mesh, Matrix4x4 World, string Name, int Node)> ReadInstances(JsonElement root, int meshCount)
    {
        List<(int, Matrix4x4, string, int)> instances = [];
        if (!root.TryGetProperty("nodes", out JsonElement nodesValue))
        {
            for (int mesh = 0; mesh < meshCount; mesh++)
            {
                instances.Add((mesh, Matrix4x4.Identity, $"mesh_{mesh:D2}", -1));
            }

            return instances;
        }

        JsonElement[] nodes = nodesValue.EnumerateArray().ToArray();
        int[] parents = Enumerable.Repeat(-1, nodes.Length).ToArray();
        for (int index = 0; index < nodes.Length; index++)
        {
            if (nodes[index].TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    int childIndex = child.GetInt32();
                    if ((uint)childIndex >= (uint)nodes.Length || parents[childIndex] != -1)
                    {
                        throw new InvalidDataException("glTF node hierarchy is invalid or has multiple parents.");
                    }

                    parents[childIndex] = index;
                }
            }
        }

        Matrix4x4?[] cache = new Matrix4x4?[nodes.Length];
        for (int index = 0; index < nodes.Length; index++)
        {
            JsonElement node = nodes[index];
            if (!node.TryGetProperty("mesh", out JsonElement meshValue))
            {
                continue;
            }

            int mesh = meshValue.GetInt32();
            if ((uint)mesh >= (uint)meshCount)
            {
                throw new InvalidDataException("glTF node references a mesh outside the meshes array.");
            }

            Matrix4x4 world = World(index, nodes, parents, cache, new HashSet<int>());
            string name = node.TryGetProperty("name", out JsonElement nameValue)
                ? nameValue.GetString() ?? $"node_{index:D2}"
                : $"node_{index:D2}";
            instances.Add((mesh, world, name, index));
        }

        if (instances.Count == 0)
        {
            for (int mesh = 0; mesh < meshCount; mesh++)
            {
                instances.Add((mesh, Matrix4x4.Identity, $"mesh_{mesh:D2}", -1));
            }
        }

        return instances;
    }

    private static Matrix4x4 World(
        int index,
        JsonElement[] nodes,
        int[] parents,
        Matrix4x4?[] cache,
        HashSet<int> visiting)
    {
        if (cache[index] is Matrix4x4 cached)
        {
            return cached;
        }

        if (!visiting.Add(index))
        {
            throw new InvalidDataException("glTF node hierarchy contains a cycle.");
        }

        Matrix4x4 local = Local(nodes[index]);
        Matrix4x4 world = parents[index] < 0
            ? local
            : local * World(parents[index], nodes, parents, cache, visiting);
        visiting.Remove(index);
        cache[index] = world;
        return world;
    }

    private static Matrix4x4 Local(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement matrixValue))
        {
            float[] m = matrixValue.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (m.Length != 16 || m.Any(value => !float.IsFinite(value)))
            {
                throw new InvalidDataException("glTF node matrix must contain 16 finite values.");
            }

            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        Vector3 scale = ReadArray(node, "scale", 3, [1, 1, 1]) is float[] s
            ? new Vector3(s[0], s[1], s[2])
            : Vector3.One;
        float[] r = ReadArray(node, "rotation", 4, [0, 0, 0, 1]);
        Quaternion rotation = Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3]));
        float[] t = ReadArray(node, "translation", 3, [0, 0, 0]);
        return Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    private static float[] ReadArray(JsonElement parent, string name, int count, float[] fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        float[] result = value.EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (result.Length != count || result.Any(item => !float.IsFinite(item)))
        {
            throw new InvalidDataException($"glTF node {name} must contain {count} finite values.");
        }

        return result;
    }

    private static Vector3[] ReadVector3(JsonElement[] a, JsonElement[] v, byte[][] b, int i) =>
        ReadFloatAccessor(a, v, b, i, "VEC3", 3)
            .Select(value => new Vector3(value[0], value[1], value[2])).ToArray();

    private static Vector2[] ReadVector2(JsonElement[] a, JsonElement[] v, byte[][] b, int i) =>
        ReadFloatAccessor(a, v, b, i, "VEC2", 2)
            .Select(value => new Vector2(value[0], value[1])).ToArray();

    private static Vector4[] ReadVector4(JsonElement[] a, JsonElement[] v, byte[][] b, int i) =>
        ReadFloatAccessor(a, v, b, i, "VEC4", 4)
            .Select(value => new Vector4(value[0], value[1], value[2], value[3])).ToArray();

    private static float[][] ReadFloatAccessor(
        JsonElement[] accessors,
        JsonElement[] views,
        byte[][] buffers,
        int index,
        string expectedType,
        int components)
    {
        JsonElement accessor = Get(accessors, index, "accessor");
        if (accessor.GetProperty("componentType").GetInt32() != 5126 ||
            accessor.GetProperty("type").GetString() != expectedType ||
            accessor.TryGetProperty("sparse", out _))
        {
            throw new NotSupportedException($"Accessor {index} must be non-sparse FLOAT {expectedType}.");
        }

        (byte[] buffer, int offset, int stride, int count) = AccessorRange(accessor, views, buffers, components * 4);
        float[][] result = new float[count][];
        for (int element = 0; element < count; element++)
        {
            float[] values = new float[components];
            int start = offset + (element * stride);
            for (int component = 0; component < components; component++)
            {
                values[component] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(start + (component * 4)));
                if (!float.IsFinite(values[component]))
                {
                    throw new InvalidDataException($"Accessor {index} contains a non-finite value.");
                }
            }

            result[element] = values;
        }

        return result;
    }

    private static uint[] ReadIndices(JsonElement[] accessors, JsonElement[] views, byte[][] buffers, int index)
    {
        JsonElement accessor = Get(accessors, index, "accessor");
        if (accessor.GetProperty("type").GetString() != "SCALAR" || accessor.TryGetProperty("sparse", out _))
        {
            throw new NotSupportedException("Index accessor must be non-sparse SCALAR.");
        }

        int componentType = accessor.GetProperty("componentType").GetInt32();
        int size = componentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
        if (size == 0)
        {
            throw new NotSupportedException($"Index component type {componentType} is unsupported.");
        }

        (byte[] buffer, int offset, int stride, int count) = AccessorRange(accessor, views, buffers, size);
        uint[] result = new uint[count];
        for (int element = 0; element < count; element++)
        {
            ReadOnlySpan<byte> source = buffer.AsSpan(offset + (element * stride));
            uint value = componentType switch
            {
                5121 => source[0],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(source),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(source),
            };
            result[element] = value;
        }

        return result;
    }

    private static (byte[] Buffer, int Offset, int Stride, int Count) AccessorRange(
        JsonElement accessor,
        JsonElement[] views,
        byte[][] buffers,
        int elementSize)
    {
        int viewIndex = accessor.GetProperty("bufferView").GetInt32();
        JsonElement view = Get(views, viewIndex, "bufferView");
        int bufferIndex = view.GetProperty("buffer").GetInt32();
        byte[] buffer = (uint)bufferIndex < (uint)buffers.Length
            ? buffers[bufferIndex]
            : throw new InvalidDataException("Buffer view references an invalid buffer.");
        int offset = checked(
            (view.TryGetProperty("byteOffset", out JsonElement viewOffset) ? viewOffset.GetInt32() : 0) +
            (accessor.TryGetProperty("byteOffset", out JsonElement accessorOffset) ? accessorOffset.GetInt32() : 0));
        int stride = view.TryGetProperty("byteStride", out JsonElement strideValue)
            ? strideValue.GetInt32()
            : elementSize;
        int count = accessor.GetProperty("count").GetInt32();
        if (offset < 0 || stride < elementSize || count < 0 ||
            (long)offset + ((long)Math.Max(count - 1, 0) * stride) + elementSize > buffer.Length)
        {
            throw new InvalidDataException("Accessor range exceeds its buffer bounds.");
        }

        return (buffer, offset, stride, count);
    }

    private static JsonElement Get(JsonElement[] values, int index, string name) =>
        (uint)index < (uint)values.Length
            ? values[index]
            : throw new InvalidDataException($"{name} index {index} is outside its array.");

    private static string? ReadMaterialName(JsonElement root, JsonElement primitive)
    {
        if (!primitive.TryGetProperty("material", out JsonElement materialValue) ||
            !root.TryGetProperty("materials", out JsonElement materials))
        {
            return null;
        }

        JsonElement[] values = materials.EnumerateArray().ToArray();
        int index = materialValue.GetInt32();
        if ((uint)index >= (uint)values.Length)
        {
            throw new InvalidDataException("Primitive material index is outside the materials array.");
        }

        return values[index].TryGetProperty("name", out JsonElement name)
            ? name.GetString()
            : $"material_{index:D2}";
    }
}
