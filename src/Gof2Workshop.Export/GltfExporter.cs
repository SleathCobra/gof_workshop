using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Core;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Export;

public sealed record GltfExportResult(
    string GltfPath,
    string BinaryPath,
    int PrimitiveCount,
    string AnimationStatus,
    IReadOnlyList<string>? TexturePaths = null,
    IReadOnlyList<int>? UnresolvedMaterialPrimitives = null);

public sealed record GltfTextureAssignment(
    int PrimitiveIndex,
    string CacheKey,
    string Name,
    RgbaImage Image,
    bool HasAlpha);

public sealed class GltfExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public GltfExportResult Export(
        SceneDocument scene,
        string outputDirectory,
        string? baseName = null,
        CancellationToken cancellationToken = default)
    {
        return ExportWithMaterials(
            scene,
            outputDirectory,
            baseName,
            [],
            cancellationToken);
    }

    public GltfExportResult ExportWithMaterials(
        SceneDocument scene,
        string outputDirectory,
        string? baseName,
        IReadOnlyList<GltfTextureAssignment> textureAssignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string safeBaseName = ObjExporter.SanitizeFileName(baseName ?? scene.Name);
        string gltfPath = Path.Combine(outputDirectory, safeBaseName + ".gltf");
        string binaryPath = Path.Combine(outputDirectory, safeBaseName + ".bin");

        ArgumentNullException.ThrowIfNull(textureAssignments);
        Dictionary<int, ExportedTexture> materialTextures = [];
        Dictionary<string, ExportedTexture> exportedByKey =
            new(StringComparer.OrdinalIgnoreCase);
        List<string> texturePaths = [];
        foreach (GltfTextureAssignment assignment in textureAssignments
                     .OrderBy(value => value.PrimitiveIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!exportedByKey.TryGetValue(assignment.CacheKey, out ExportedTexture? exported))
            {
                string suffix = assignment.CacheKey.Length >= 10
                    ? assignment.CacheKey[..10]
                    : assignment.CacheKey;
                string textureName = $"{safeBaseName}_texture_{suffix}.png";
                string texturePath = Path.Combine(outputDirectory, textureName);
                PngWriter.Write(assignment.Image, texturePath, cancellationToken);
                exported = new ExportedTexture(
                    assignment.Name,
                    textureName,
                    assignment.HasAlpha);
                exportedByKey.Add(assignment.CacheKey, exported);
                texturePaths.Add(texturePath);
            }

            materialTextures[assignment.PrimitiveIndex] = exported;
        }

        using GltfBuilder builder = new();
        for (int primitiveIndex = 0; primitiveIndex < scene.Primitives.Count; primitiveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            materialTextures.TryGetValue(primitiveIndex, out ExportedTexture? texture);
            builder.AddPrimitive(
                scene.Primitives[primitiveIndex],
                primitiveIndex,
                texture);
        }

        builder.AddAnimations(scene);
        byte[] binary = builder.GetBinary();
        File.WriteAllBytes(binaryPath, binary);
        GltfRoot root = builder.BuildRoot(
            scene,
            Path.GetFileName(binaryPath),
            binary.Length);
        File.WriteAllText(gltfPath, JsonSerializer.Serialize(root, JsonOptions));

        return new GltfExportResult(
            gltfPath,
            binaryPath,
            scene.Primitives.Count,
            scene.Animations.Count == 0
                ? "Static geometry exported; no transform animation keys were present."
                : $"{scene.Animations.Count} transform animation clip(s) exported with millisecond source times converted to seconds.",
            texturePaths,
            Enumerable.Range(0, scene.Primitives.Count)
                .Where(index => !materialTextures.ContainsKey(index))
                .ToArray());
    }

    private sealed class GltfBuilder : IDisposable
    {
        private readonly MemoryStream binary = new();
        private readonly List<GltfBufferView> bufferViews = [];
        private readonly List<GltfAccessor> accessors = [];
        private readonly List<GltfMaterial> materials = [];
        private readonly List<GltfMesh> meshes = [];
        private readonly List<GltfNode> nodes = [];
        private readonly List<GltfAnimation> animations = [];
        private readonly List<GltfImage> images = [];
        private readonly List<GltfTexture> textures = [];
        private readonly List<GltfSampler> samplers = [];
        private readonly Dictionary<string, int> textureByUri =
            new(StringComparer.OrdinalIgnoreCase);

        public void AddPrimitive(
            ScenePrimitive primitive,
            int primitiveIndex,
            ExportedTexture? exportedTexture)
        {
            Vector3[] localPositions = primitive.Positions
                .Select(position => position - primitive.SourcePivot)
                .ToArray();
            int positionAccessor = AddVector3Accessor(
                localPositions,
                target: 34962,
                includeBounds: true);
            Dictionary<string, int> attributes = new(StringComparer.Ordinal)
            {
                ["POSITION"] = positionAccessor,
            };

            if (primitive.Normals is not null)
            {
                attributes["NORMAL"] = AddVector3Accessor(
                    primitive.Normals,
                    target: 34962,
                    includeBounds: false);
            }

            if (primitive.TextureCoordinates is not null)
            {
                attributes["TEXCOORD_0"] = AddVector2Accessor(
                    primitive.TextureCoordinates,
                    target: 34962);
            }

            if (primitive.Colors is not null)
            {
                attributes["COLOR_0"] = AddVector4Accessor(
                    primitive.Colors,
                    target: 34962);
            }

            int indexAccessor = AddIndexAccessor(primitive.Indices);
            int materialIndex = materials.Count;
            GltfTextureInfo? baseColorTexture = exportedTexture is null
                ? null
                : new GltfTextureInfo(GetOrAddTexture(exportedTexture), 0);
            materials.Add(new GltfMaterial(
                primitive.Material.Name,
                new GltfPbr(
                    exportedTexture is null
                        ? [
                            primitive.Material.BaseColor.X,
                            primitive.Material.BaseColor.Y,
                            primitive.Material.BaseColor.Z,
                            primitive.Material.BaseColor.W,
                        ]
                        : [1f, 1f, 1f, 1f],
                    MetallicFactor: 0,
                    RoughnessFactor: 0.8f,
                    BaseColorTexture: baseColorTexture),
                DoubleSided: true,
                AlphaMode: exportedTexture?.HasAlpha == true ? "BLEND" : "OPAQUE"));

            int meshIndex = meshes.Count;
            meshes.Add(new GltfMesh(
                primitive.Name,
                [new GltfPrimitive(attributes, indexAccessor, materialIndex, Mode: 4)]));
            nodes.Add(new GltfNode(
                primitive.Name,
                meshIndex,
                [
                    primitive.SourcePivot.X,
                    primitive.SourcePivot.Y,
                    primitive.SourcePivot.Z,
                ],
                null,
                null,
                new Dictionary<string, object?>
                {
                    ["sourcePivot"] = new[]
                    {
                        primitive.SourcePivot.X,
                        primitive.SourcePivot.Y,
                        primitive.SourcePivot.Z,
                    },
                    ["boundingSphere"] = new[]
                    {
                        primitive.BoundingSphereCenter.X,
                        primitive.BoundingSphereCenter.Y,
                        primitive.BoundingSphereCenter.Z,
                        primitive.BoundingSphereRadius,
                    },
                    ["sourceSubmeshIndex"] = primitiveIndex,
                }));
        }

        private int GetOrAddTexture(ExportedTexture exported)
        {
            if (textureByUri.TryGetValue(exported.Uri, out int existing))
            {
                return existing;
            }

            int sampler = samplers.Count;
            samplers.Add(new GltfSampler(
                MagFilter: 9729,
                MinFilter: 9987,
                WrapS: 10497,
                WrapT: 10497));
            int image = images.Count;
            images.Add(new GltfImage(exported.Name, exported.Uri));
            int texture = textures.Count;
            textures.Add(new GltfTexture(sampler, image, exported.Name));
            textureByUri.Add(exported.Uri, texture);
            return texture;
        }

        public void AddAnimations(SceneDocument scene)
        {
            foreach (SceneAnimationClip clip in scene.Animations)
            {
                List<GltfAnimationSampler> samplers = [];
                List<GltfAnimationChannel> channels = [];
                foreach (SceneAnimationTrack track in clip.Tracks)
                {
                    if (track.Keys.Count == 0 ||
                        track.PrimitiveIndex < 0 ||
                        track.PrimitiveIndex >= nodes.Count)
                    {
                        continue;
                    }

                    float[] times = track.Keys.Select(key => key.TimeSeconds).ToArray();
                    int input = AddScalarAccessor(times);
                    if (track.HasTranslation)
                    {
                        Vector3 pivot = scene.Primitives[track.PrimitiveIndex].SourcePivot;
                        int output = AddVector3Accessor(
                            track.Keys.Select(key => key.Translation + pivot).ToArray(),
                            target: null,
                            includeBounds: false);
                        AddAnimationChannel(
                            samplers,
                            channels,
                            input,
                            output,
                            track.PrimitiveIndex,
                            "translation");
                    }

                    if (track.HasRotation)
                    {
                        int output = AddQuaternionAccessor(
                            track.Keys.Select(key => key.Rotation).ToArray());
                        AddAnimationChannel(
                            samplers,
                            channels,
                            input,
                            output,
                            track.PrimitiveIndex,
                            "rotation");
                    }

                    if (track.HasScale)
                    {
                        int output = AddVector3Accessor(
                            track.Keys.Select(key => key.Scale).ToArray(),
                            target: null,
                            includeBounds: false);
                        AddAnimationChannel(
                            samplers,
                            channels,
                            input,
                            output,
                            track.PrimitiveIndex,
                            "scale");
                    }
                }

                if (channels.Count > 0)
                {
                    animations.Add(new GltfAnimation(
                        clip.Name,
                        samplers,
                        channels,
                        new Dictionary<string, object?>
                        {
                            ["sourceTimeUnit"] = clip.SourceTimeUnit,
                            ["limitations"] = clip.Limitations,
                        }));
                }
            }
        }

        private static void AddAnimationChannel(
            List<GltfAnimationSampler> samplers,
            List<GltfAnimationChannel> channels,
            int input,
            int output,
            int node,
            string path)
        {
            int sampler = samplers.Count;
            samplers.Add(new GltfAnimationSampler(input, output, "LINEAR"));
            channels.Add(new GltfAnimationChannel(
                sampler,
                new GltfAnimationTarget(node, path)));
        }

        public byte[] GetBinary() => binary.ToArray();

        public void Dispose()
        {
            binary.Dispose();
        }

        public GltfRoot BuildRoot(SceneDocument scene, string binaryFileName, int byteLength)
        {
            return new GltfRoot(
                new GltfAsset("2.0", "Galaxy on Fire 2 Workshop"),
                Scene: 0,
                Scenes: [new GltfScene(scene.Name, Enumerable.Range(0, nodes.Count).ToArray())],
                Nodes: nodes,
                Meshes: meshes,
                Materials: materials,
                Images: images.Count == 0 ? null : images,
                Textures: textures.Count == 0 ? null : textures,
                Samplers: samplers.Count == 0 ? null : samplers,
                Animations: animations.Count == 0 ? null : animations,
                Buffers: [new GltfBuffer(binaryFileName, byteLength)],
                BufferViews: bufferViews,
                Accessors: accessors,
                Extras: new Dictionary<string, object?>
                {
                    ["sourceCoordinateConvention"] = scene.SourceCoordinateConvention,
                    ["normalizedCoordinateConvention"] = scene.NormalizedCoordinateConvention,
                    ["animationStatus"] = animations.Count == 0
                        ? "No transform animation keys."
                        : "Transform animation exported; source milliseconds converted to seconds.",
                });
        }

        private int AddVector3Accessor(Vector3[] values, int? target, bool includeBounds)
        {
            AlignBinary();
            int offset = checked((int)binary.Position);
            Vector3 minimum = new(float.PositiveInfinity);
            Vector3 maximum = new(float.NegativeInfinity);
            foreach (Vector3 value in values)
            {
                WriteSingle(value.X);
                WriteSingle(value.Y);
                WriteSingle(value.Z);
                minimum = Vector3.Min(minimum, value);
                maximum = Vector3.Max(maximum, value);
            }

            int viewIndex = AddBufferView(offset, checked(values.Length * 12), target);
            int accessorIndex = accessors.Count;
            accessors.Add(new GltfAccessor(
                viewIndex,
                ByteOffset: 0,
                ComponentType: 5126,
                Count: values.Length,
                Type: "VEC3",
                Min: includeBounds ? [minimum.X, minimum.Y, minimum.Z] : null,
                Max: includeBounds ? [maximum.X, maximum.Y, maximum.Z] : null));
            return accessorIndex;
        }

        private int AddQuaternionAccessor(Quaternion[] values)
        {
            AlignBinary();
            int offset = checked((int)binary.Position);
            foreach (Quaternion value in values)
            {
                Quaternion normalized = Quaternion.Normalize(value);
                WriteSingle(normalized.X);
                WriteSingle(normalized.Y);
                WriteSingle(normalized.Z);
                WriteSingle(normalized.W);
            }

            int viewIndex = AddBufferView(offset, checked(values.Length * 16), target: null);
            int accessorIndex = accessors.Count;
            accessors.Add(new GltfAccessor(
                viewIndex,
                0,
                5126,
                values.Length,
                "VEC4",
                null,
                null));
            return accessorIndex;
        }

        private int AddScalarAccessor(float[] values)
        {
            AlignBinary();
            int offset = checked((int)binary.Position);
            foreach (float value in values)
            {
                WriteSingle(value);
            }

            int viewIndex = AddBufferView(offset, checked(values.Length * 4), target: null);
            int accessorIndex = accessors.Count;
            accessors.Add(new GltfAccessor(
                viewIndex,
                0,
                5126,
                values.Length,
                "SCALAR",
                values.Length == 0 ? null : [values.Min()],
                values.Length == 0 ? null : [values.Max()]));
            return accessorIndex;
        }

        private int AddVector2Accessor(Vector2[] values, int target)
        {
            AlignBinary();
            int offset = checked((int)binary.Position);
            foreach (Vector2 value in values)
            {
                WriteSingle(value.X);
                WriteSingle(value.Y);
            }

            int viewIndex = AddBufferView(offset, checked(values.Length * 8), target);
            int accessorIndex = accessors.Count;
            accessors.Add(new GltfAccessor(
                viewIndex,
                0,
                5126,
                values.Length,
                "VEC2",
                null,
                null));
            return accessorIndex;
        }

        private int AddVector4Accessor(Vector4[] values, int target)
        {
            AlignBinary();
            int offset = checked((int)binary.Position);
            foreach (Vector4 value in values)
            {
                WriteSingle(value.X);
                WriteSingle(value.Y);
                WriteSingle(value.Z);
                WriteSingle(value.W);
            }

            int viewIndex = AddBufferView(offset, checked(values.Length * 16), target);
            int accessorIndex = accessors.Count;
            accessors.Add(new GltfAccessor(
                viewIndex,
                0,
                5126,
                values.Length,
                "VEC4",
                null,
                null));
            return accessorIndex;
        }

        private int AddIndexAccessor(ushort[] values)
        {
            AlignBinary();
            int offset = checked((int)binary.Position);
            ushort minimum = ushort.MaxValue;
            ushort maximum = ushort.MinValue;
            foreach (ushort value in values)
            {
                WriteUInt16(value);
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }

            int viewIndex = AddBufferView(offset, checked(values.Length * 2), target: 34963);
            int accessorIndex = accessors.Count;
            accessors.Add(new GltfAccessor(
                viewIndex,
                0,
                5123,
                values.Length,
                "SCALAR",
                [minimum],
                [maximum]));
            return accessorIndex;
        }

        private int AddBufferView(int offset, int length, int? target)
        {
            int index = bufferViews.Count;
            bufferViews.Add(new GltfBufferView(0, offset, length, target));
            return index;
        }

        private void AlignBinary()
        {
            while ((binary.Position & 3) != 0)
            {
                binary.WriteByte(0);
            }
        }

        private void WriteSingle(float value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
            binary.Write(bytes);
        }

        private void WriteUInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            binary.Write(bytes);
        }
    }

    private sealed record GltfRoot(
        GltfAsset Asset,
        int Scene,
        IReadOnlyList<GltfScene> Scenes,
        IReadOnlyList<GltfNode> Nodes,
        IReadOnlyList<GltfMesh> Meshes,
        IReadOnlyList<GltfMaterial> Materials,
        IReadOnlyList<GltfImage>? Images,
        IReadOnlyList<GltfTexture>? Textures,
        IReadOnlyList<GltfSampler>? Samplers,
        IReadOnlyList<GltfAnimation>? Animations,
        IReadOnlyList<GltfBuffer> Buffers,
        IReadOnlyList<GltfBufferView> BufferViews,
        IReadOnlyList<GltfAccessor> Accessors,
        IReadOnlyDictionary<string, object?> Extras);

    private sealed record GltfAsset(string Version, string Generator);

    private sealed record GltfScene(string Name, int[] Nodes);

    private sealed record GltfNode(
        string Name,
        int Mesh,
        float[]? Translation,
        float[]? Rotation,
        float[]? Scale,
        IReadOnlyDictionary<string, object?> Extras);

    private sealed record GltfMesh(string Name, IReadOnlyList<GltfPrimitive> Primitives);

    private sealed record GltfPrimitive(
        IReadOnlyDictionary<string, int> Attributes,
        int Indices,
        int Material,
        int Mode);

    private sealed record GltfMaterial(
        string Name,
        [property: JsonPropertyName("pbrMetallicRoughness")] GltfPbr Pbr,
        bool DoubleSided,
        string AlphaMode);

    private sealed record GltfAnimation(
        string Name,
        IReadOnlyList<GltfAnimationSampler> Samplers,
        IReadOnlyList<GltfAnimationChannel> Channels,
        IReadOnlyDictionary<string, object?> Extras);

    private sealed record GltfAnimationSampler(
        int Input,
        int Output,
        string Interpolation);

    private sealed record GltfAnimationChannel(
        int Sampler,
        GltfAnimationTarget Target);

    private sealed record GltfAnimationTarget(
        int Node,
        string Path);

    private sealed record GltfPbr(
        float[] BaseColorFactor,
        float MetallicFactor,
        float RoughnessFactor,
        GltfTextureInfo? BaseColorTexture);

    private sealed record GltfTextureInfo(int Index, int TexCoord);

    private sealed record GltfImage(string Name, string Uri);

    private sealed record GltfTexture(int Sampler, int Source, string Name);

    private sealed record GltfSampler(
        int MagFilter,
        int MinFilter,
        int WrapS,
        int WrapT);

    private sealed record GltfBuffer(string Uri, int ByteLength);

    private sealed record GltfBufferView(
        int Buffer,
        int ByteOffset,
        int ByteLength,
        int? Target);

    private sealed record GltfAccessor(
        int BufferView,
        int ByteOffset,
        int ComponentType,
        int Count,
        string Type,
        float[]? Min,
        float[]? Max);

    private sealed record ExportedTexture(
        string Name,
        string Uri,
        bool HasAlpha);
}
