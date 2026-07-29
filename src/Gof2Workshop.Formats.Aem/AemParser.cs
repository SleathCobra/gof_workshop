using System.Numerics;
using System.Text;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aem;

public sealed class AemParser
{
    public AemFile Parse(
        string path,
        AemParserOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Parse(stream, path, options, cancellationToken);
    }

    public AemFile Parse(
        Stream stream,
        string? sourcePath = null,
        AemParserOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= AemParserOptions.Pc1X;
        ValidateOptions(options);

        ParseTrace? trace = options.ResearchDiagnostics ? new ParseTrace() : null;
        List<FormatDiagnostic> diagnostics = [];
        using BoundedBinaryReader reader = new(
            stream,
            sourcePath,
            BinaryEndianness.Little,
            trace,
            leaveOpen: true);

        try
        {
            string signature = reader.ReadNullTerminatedString(
                Encoding.ASCII,
                10,
                "signature",
                "header");
            AemVersion version = signature switch
            {
                "AEMesh" => AemVersion.V1,
                "V2AEMesh" => AemVersion.V2,
                "V3AEMesh" => AemVersion.V3,
                "V4AEMesh" => AemVersion.V4,
                "V5AEMesh" => AemVersion.V5,
                _ => throw reader.Unsupported(
                    "signature",
                    $"Unknown AEM signature '{signature}'.",
                    0),
            };

            AemFlags flags = (AemFlags)reader.ReadByte("flags", "header");
            if ((flags & AemFlags.BaseMesh) == 0)
            {
                throw reader.Corrupt("flags", "Base-mesh flag is not set.");
            }

            if ((flags & ~(AemFlags.BaseMesh
                | AemFlags.TextureCoordinates
                | AemFlags.Normals
                | AemFlags.AuxiliaryFloat4
                | AemFlags.Indices)) != 0)
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEM_UNKNOWN_FLAGS",
                    $"Unknown AEM flag bits 0x{((byte)flags & ~0x1F):X2} are preserved.",
                    reader.Position - 1,
                    "header"));
            }

            int submeshCount = version >= AemVersion.V3
                ? reader.ReadUInt16("submeshCount", "header")
                : 1;
            if (submeshCount <= 0 || submeshCount > options.MaximumSubmeshCount)
            {
                throw reader.Corrupt(
                    "submeshCount",
                    $"Submesh count {submeshCount} is outside 1..{options.MaximumSubmeshCount}.");
            }

            long headerLength = reader.Position;
            byte[] rawHeader = reader.ReadRange(
                0,
                checked((int)headerLength),
                "rawHeader",
                "preserved");

            if (!options.Profile.SupportedAemVersions.Contains((int)version))
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEM_PROFILE_MISMATCH",
                    $"Version {(int)version} is not expected by profile '{options.Profile.Id}'.",
                    0,
                    "header"));
            }

            List<AemSubmesh> submeshes = new(submeshCount);
            for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                submeshes.Add(version >= AemVersion.V4
                    ? ParseSubmesh(
                        reader,
                        version,
                        flags,
                        submeshIndex,
                        options,
                        diagnostics,
                        cancellationToken)
                    : ParseLegacySubmesh(
                        reader,
                        version,
                        flags,
                        submeshIndex,
                        options,
                        diagnostics,
                        cancellationToken));
            }

            byte[] unknownTrailingData = [];
            if (reader.Remaining > 0)
            {
                if (reader.Remaining > options.MaximumTrailingBytes)
                {
                    throw reader.Corrupt(
                        "unknownTrailingData",
                        $"Trailing data length {reader.Remaining} exceeds limit {options.MaximumTrailingBytes}.");
                }

                unknownTrailingData = reader.ReadBytes(
                    checked((int)reader.Remaining),
                    "unknownTrailingData",
                    "footer");
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEM_UNKNOWN_TRAILING_DATA",
                    $"Preserved {unknownTrailingData.Length} uninterpreted trailing bytes.",
                    reader.Position - unknownTrailingData.Length,
                    "footer"));
            }

            return new AemFile(
                sourcePath,
                options.Profile.Id,
                signature,
                version,
                flags,
                submeshes,
                rawHeader,
                unknownTrailingData,
                diagnostics,
                trace,
                reader.ReadRange(
                    0,
                    checked((int)reader.Length),
                    "originalData",
                    "preserved"));
        }
        catch (FormatParseException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new FormatParseException(
                FormatFailureKind.Corrupt,
                sourcePath,
                reader.Position,
                "size",
                "An integer overflow occurred while validating file-controlled sizes.",
                exception);
        }
    }

    private static AemSubmesh ParseLegacySubmesh(
        BoundedBinaryReader reader,
        AemVersion version,
        AemFlags flags,
        int submeshIndex,
        AemParserOptions options,
        List<FormatDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        long offset = reader.Position;
        string prefix = $"submeshes[{submeshIndex}]";
        Vector3 pivot = version == AemVersion.V3
            ? reader.ReadVector3($"{prefix}.pivot", "mesh")
            : Vector3.Zero;
        ValidateFinite(reader, pivot, $"{prefix}.pivot");

        ushort rawIndexCount = (flags & AemFlags.Indices) != 0
            ? reader.ReadUInt16($"{prefix}.indexCount", "mesh")
            : (ushort)0;
        if (rawIndexCount > options.MaximumIndexCountPerSubmesh)
        {
            throw reader.Corrupt(
                $"{prefix}.indexCount",
                $"Index count {rawIndexCount} exceeds limit {options.MaximumIndexCountPerSubmesh}.");
        }

        ushort[] storedIndices = reader.ReadUInt16Array(
            rawIndexCount,
            $"{prefix}.indices",
            "mesh",
            cancellationToken);
        AemPrimitiveTopology topology = AemPrimitiveTopology.Triangles;
        ushort[]? sourceStripLengths = null;
        ushort[] indices = storedIndices;
        if (version == AemVersion.V1 && (flags & AemFlags.Indices) != 0)
        {
            ushort stripCount = reader.ReadUInt16($"{prefix}.stripCount", "mesh");
            if (stripCount > rawIndexCount)
            {
                throw reader.Corrupt(
                    $"{prefix}.stripCount",
                    $"Triangle-strip count {stripCount} exceeds stored index count {rawIndexCount}.");
            }

            sourceStripLengths = reader.ReadUInt16Array(
                stripCount,
                $"{prefix}.stripLengths",
                "mesh",
                cancellationToken);
            indices = ExpandTriangleStrips(
                reader,
                storedIndices,
                sourceStripLengths,
                $"{prefix}.stripLengths");
            topology = AemPrimitiveTopology.TriangleStrips;
        }

        ushort vertexCount = reader.ReadUInt16($"{prefix}.vertexCount", "mesh");
        if (vertexCount == 0 || vertexCount > options.MaximumVertexCountPerSubmesh)
        {
            throw reader.Corrupt(
                $"{prefix}.vertexCount",
                $"Vertex count {vertexCount} is outside 1..{options.MaximumVertexCountPerSubmesh}.");
        }

        Vector3[] positions = new Vector3[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (version == AemVersion.V1)
            {
                positions[vertex] = new Vector3(
                    reader.ReadInt16($"{prefix}.positions[{vertex}].x", "mesh"),
                    reader.ReadInt16($"{prefix}.positions[{vertex}].y", "mesh"),
                    reader.ReadInt16($"{prefix}.positions[{vertex}].z", "mesh"));
            }
            else
            {
                positions[vertex] = new Vector3(
                    ReadFixed16_16(reader, $"{prefix}.positions[{vertex}].x"),
                    ReadFixed16_16(reader, $"{prefix}.positions[{vertex}].y"),
                    ReadFixed16_16(reader, $"{prefix}.positions[{vertex}].z"));
            }
        }

        Vector2[]? textureCoordinates = null;
        if ((flags & AemFlags.TextureCoordinates) != 0)
        {
            float divisor = version == AemVersion.V1 ? 256f : 4096f;
            textureCoordinates = new Vector2[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                textureCoordinates[vertex] = new Vector2(
                    reader.ReadInt16($"{prefix}.textureCoordinates[{vertex}].u", "mesh") / divisor,
                    reader.ReadInt16($"{prefix}.textureCoordinates[{vertex}].v", "mesh") / divisor);
            }
        }

        Vector3[]? normals = null;
        if ((flags & AemFlags.Normals) != 0)
        {
            float divisor = version == AemVersion.V1 ? 256f : 32768f;
            normals = new Vector3[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                normals[vertex] = new Vector3(
                    reader.ReadInt16($"{prefix}.normals[{vertex}].x", "mesh") / divisor,
                    reader.ReadInt16($"{prefix}.normals[{vertex}].y", "mesh") / divisor,
                    reader.ReadInt16($"{prefix}.normals[{vertex}].z", "mesh") / divisor);
            }
        }

        Vector4[]? auxiliary = null;
        if ((flags & AemFlags.AuxiliaryFloat4) != 0)
        {
            auxiliary = new Vector4[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                auxiliary[vertex] = new Vector4(
                    reader.ReadInt16($"{prefix}.auxiliary[{vertex}].x", "mesh"),
                    reader.ReadInt16($"{prefix}.auxiliary[{vertex}].y", "mesh"),
                    0,
                    0);
            }
        }

        bool isTransparent = version <= AemVersion.V2
            && reader.ReadByte($"{prefix}.transparent", "mesh") != 0;
        AemBoundingSphere sphere;
        AemAnimation animation;
        if (version == AemVersion.V3)
        {
            Vector4 rawSphere = reader.ReadVector4($"{prefix}.boundingSphere", "bounds");
            ValidateFinite(reader, rawSphere, $"{prefix}.boundingSphere");
            sphere = new AemBoundingSphere(rawSphere.AsVector3(), rawSphere.W);
            animation = ParseAnimation(
                reader,
                version,
                submeshIndex,
                options,
                diagnostics,
                cancellationToken);
        }
        else
        {
            sphere = CalculateBoundingSphere(positions);
            animation = EmptyAnimation(reader.Position);
        }

        if ((flags & AemFlags.Indices) == 0)
        {
            indices = Enumerable.Range(0, vertexCount)
                .Select(value => checked((ushort)value))
                .ToArray();
            diagnostics.Add(new FormatDiagnostic(
                DiagnosticSeverity.Info,
                "AEM_IMPLICIT_INDICES",
                $"AEM v{(int)version} submesh {submeshIndex} uses sequential draw-array indices.",
                offset,
                "mesh"));
        }

        ValidateIndices(reader, indices, vertexCount, prefix);
        diagnostics.Add(new FormatDiagnostic(
            DiagnosticSeverity.Info,
            "AEM_LEGACY_FIXED_POINT",
            version == AemVersion.V1
                ? "AEM v1 positions use signed 16-bit source units; UVs and normals use 8-bit fractional scaling."
                : $"AEM v{(int)version} positions use signed 16.16 fixed point; UVs use 4.12 and normals use signed 1.15.",
            offset,
            "mesh"));

        return new AemSubmesh(
            submeshIndex,
            pivot,
            indices,
            positions,
            textureCoordinates,
            normals,
            auxiliary,
            sphere,
            animation,
            offset,
            topology,
            isTransparent,
            storedIndices,
            sourceStripLengths);
    }

    private static ushort[] ExpandTriangleStrips(
        BoundedBinaryReader reader,
        ushort[] source,
        ushort[] stripLengths,
        string field)
    {
        int consumed = 0;
        List<ushort> triangles = [];
        foreach (ushort stripLength in stripLengths)
        {
            if (stripLength < 3 || consumed + stripLength > source.Length)
            {
                throw reader.Corrupt(
                    field,
                    $"Triangle strip length {stripLength} exceeds the remaining {source.Length - consumed} indices.");
            }

            for (int index = 0; index < stripLength - 2; index++)
            {
                ushort first = source[consumed + index];
                ushort second = source[consumed + index + 1];
                ushort third = source[consumed + index + 2];
                triangles.Add(first);
                triangles.Add(index % 2 == 0 ? second : third);
                triangles.Add(index % 2 == 0 ? third : second);
            }

            consumed += stripLength;
        }

        if (consumed != source.Length)
        {
            throw reader.Corrupt(
                field,
                $"Triangle strips consume {consumed} of {source.Length} stored indices.");
        }

        return [.. triangles];
    }

    private static float ReadFixed16_16(BoundedBinaryReader reader, string field)
    {
        ushort low = reader.ReadUInt16($"{field}.low", "mesh");
        ushort high = reader.ReadUInt16($"{field}.high", "mesh");
        int raw = unchecked((int)((uint)low | ((uint)high << 16)));
        return raw / 65536f;
    }

    private static void ValidateIndices(
        BoundedBinaryReader reader,
        ushort[] indices,
        int vertexCount,
        string prefix)
    {
        for (int index = 0; index < indices.Length; index++)
        {
            if (indices[index] >= vertexCount)
            {
                throw reader.Corrupt(
                    $"{prefix}.indices[{index}]",
                    $"Index {indices[index]} is outside vertex range 0..{vertexCount - 1}.");
            }
        }
    }

    private static AemBoundingSphere CalculateBoundingSphere(Vector3[] positions)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        Vector3 center = (minimum + maximum) * 0.5f;
        float radius = positions.Max(position => Vector3.Distance(center, position));
        return new AemBoundingSphere(center, radius);
    }

    private static AemAnimation EmptyAnimation(long offset)
    {
        return new AemAnimation(0, 0, 0, -1, null, 0, [], [], offset);
    }

    private static AemSubmesh ParseSubmesh(
        BoundedBinaryReader reader,
        AemVersion version,
        AemFlags flags,
        int submeshIndex,
        AemParserOptions options,
        List<FormatDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        long submeshOffset = reader.Position;
        string prefix = $"submeshes[{submeshIndex}]";
        Vector3 pivot = reader.ReadVector3($"{prefix}.pivot", "mesh");
        ValidateFinite(reader, pivot, $"{prefix}.pivot");

        ushort indexCount = reader.ReadUInt16($"{prefix}.indexCount", "mesh");
        if (indexCount > options.MaximumIndexCountPerSubmesh)
        {
            throw reader.Corrupt(
                $"{prefix}.indexCount",
                $"Index count {indexCount} exceeds limit {options.MaximumIndexCountPerSubmesh}.");
        }

        ushort[] indices = reader.ReadUInt16Array(
            indexCount,
            $"{prefix}.indices",
            "mesh",
            cancellationToken);

        ushort vertexCount = reader.ReadUInt16($"{prefix}.vertexCount", "mesh");
        if (vertexCount == 0 || vertexCount > options.MaximumVertexCountPerSubmesh)
        {
            throw reader.Corrupt(
                $"{prefix}.vertexCount",
                $"Vertex count {vertexCount} is outside 1..{options.MaximumVertexCountPerSubmesh}.");
        }

        Vector3[] positions = reader.ReadVector3Array(
            vertexCount,
            $"{prefix}.positions",
            "mesh",
            cancellationToken);
        ValidateFinite(reader, positions, $"{prefix}.positions");

        Vector2[]? textureCoordinates = null;
        if ((flags & AemFlags.TextureCoordinates) != 0)
        {
            textureCoordinates = reader.ReadVector2Array(
                vertexCount,
                $"{prefix}.textureCoordinates",
                "mesh",
                cancellationToken);
            ValidateFinite(reader, textureCoordinates, $"{prefix}.textureCoordinates");
        }

        Vector3[]? normals = null;
        if ((flags & AemFlags.Normals) != 0)
        {
            normals = reader.ReadVector3Array(
                vertexCount,
                $"{prefix}.normals",
                "mesh",
                cancellationToken);
            ValidateFinite(reader, normals, $"{prefix}.normals");
        }

        Vector4[]? auxiliary = null;
        if ((flags & AemFlags.AuxiliaryFloat4) != 0)
        {
            auxiliary = reader.ReadVector4Array(
                vertexCount,
                $"{prefix}.auxiliaryFloat4",
                "mesh",
                cancellationToken);
            ValidateFinite(reader, auxiliary, $"{prefix}.auxiliaryFloat4");
        }

        for (int index = 0; index < indices.Length; index++)
        {
            if (indices[index] >= vertexCount)
            {
                throw reader.Corrupt(
                    $"{prefix}.indices[{index}]",
                    $"Index {indices[index]} is outside vertex range 0..{vertexCount - 1}.");
            }
        }

        if (indices.Length % 3 != 0)
        {
            diagnostics.Add(new FormatDiagnostic(
                DiagnosticSeverity.Warning,
                "AEM_NON_TRIANGLE_INDEX_COUNT",
                $"Submesh {submeshIndex} index count {indices.Length} is not divisible by three.",
                submeshOffset + 12,
                "mesh"));
        }

        Vector4 rawSphere = reader.ReadVector4($"{prefix}.boundingSphere", "bounds");
        ValidateFinite(reader, rawSphere, $"{prefix}.boundingSphere");
        if (rawSphere.W < 0)
        {
            diagnostics.Add(new FormatDiagnostic(
                DiagnosticSeverity.Warning,
                "AEM_NEGATIVE_BOUND_RADIUS",
                $"Submesh {submeshIndex} has negative bounding-sphere radius {rawSphere.W}.",
                reader.Position - 4,
                "bounds"));
        }

        AemBoundingSphere sphere = new(
            new Vector3(rawSphere.X, rawSphere.Y, rawSphere.Z),
            rawSphere.W);
        AemAnimation animation = ParseAnimation(
            reader,
            version,
            submeshIndex,
            options,
            diagnostics,
            cancellationToken);

        return new AemSubmesh(
            submeshIndex,
            pivot,
            indices,
            positions,
            textureCoordinates,
            normals,
            auxiliary,
            sphere,
            animation,
            submeshOffset,
            SourceIndices: indices);
    }

    private static AemAnimation ParseAnimation(
        BoundedBinaryReader reader,
        AemVersion version,
        int submeshIndex,
        AemParserOptions options,
        List<FormatDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        long offset = reader.Position;
        string prefix = $"submeshes[{submeshIndex}].animation";
        List<AemAnimationCurve> curves = [];

        ushort translationStorage = ParseTransformGroup(
            reader,
            $"{prefix}.translation",
            [
                AemAnimationChannel.TranslationX,
                AemAnimationChannel.TranslationY,
                AemAnimationChannel.TranslationZ,
            ],
            AemAnimationChannel.TranslationXyz,
            curves,
            options,
            cancellationToken);
        ushort rotationStorage = ParseTransformGroup(
            reader,
            $"{prefix}.rotation",
            [
                AemAnimationChannel.RotationX,
                AemAnimationChannel.RotationY,
                AemAnimationChannel.RotationZ,
            ],
            AemAnimationChannel.RotationXyz,
            curves,
            options,
            cancellationToken);
        ushort scaleStorage = ParseTransformGroup(
            reader,
            $"{prefix}.scale",
            [
                AemAnimationChannel.ScaleX,
                AemAnimationChannel.ScaleY,
                AemAnimationChannel.ScaleZ,
            ],
            AemAnimationChannel.ScaleXyz,
            curves,
            options,
            cancellationToken);

        short specialType = -1;
        if (version >= AemVersion.V4)
        {
            specialType = reader.ReadInt16($"{prefix}.specialV4Type", "animation");
            if (specialType == 2)
            {
                curves.Add(ParseScalarCurve(
                    reader,
                    $"{prefix}.specialV4",
                    AemAnimationChannel.SpecialV4,
                    options,
                    cancellationToken));
            }
            else if (specialType is not (-1 or 0 or 1))
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEM_UNKNOWN_V4_ANIMATION_MARKER",
                    $"Submesh {submeshIndex} uses unrecognized v4 animation marker {specialType}.",
                    reader.Position - 2,
                    "animation"));
            }
        }

        short? v5UvMarker = null;
        if (version == AemVersion.V5)
        {
            v5UvMarker = reader.ReadInt16($"{prefix}.v5UvMarker", "animation");
            if (v5UvMarker != 0)
            {
                AemAnimationChannel[] channels =
                [
                    AemAnimationChannel.UvOffsetX,
                    AemAnimationChannel.UvOffsetY,
                    AemAnimationChannel.UvScaleX,
                    AemAnimationChannel.UvScaleY,
                    AemAnimationChannel.UnknownV5A,
                    AemAnimationChannel.UnknownV5B,
                    AemAnimationChannel.UvRotationZ,
                ];

                foreach (AemAnimationChannel channel in channels)
                {
                    curves.Add(ParseScalarCurve(
                        reader,
                        $"{prefix}.{channel}",
                        channel,
                        options,
                        cancellationToken));
                }
            }
        }

        short padding = reader.ReadInt16($"{prefix}.padding", "animation");
        if (padding != 0)
        {
            diagnostics.Add(new FormatDiagnostic(
                DiagnosticSeverity.Warning,
                "AEM_NONZERO_ANIMATION_PADDING",
                $"Submesh {submeshIndex} animation padding is 0x{(ushort)padding:X4}.",
                reader.Position - 2,
                "animation"));
        }

        int rawLength = checked((int)(reader.Position - offset));
        byte[] rawData = reader.ReadRange(offset, rawLength, $"{prefix}.rawData", "preserved");
        return new AemAnimation(
            translationStorage,
            rotationStorage,
            scaleStorage,
            specialType,
            v5UvMarker,
            padding,
            curves,
            rawData,
            offset);
    }

    private static ushort ParseTransformGroup(
        BoundedBinaryReader reader,
        string field,
        IReadOnlyList<AemAnimationChannel> scalarChannels,
        AemAnimationChannel vectorChannel,
        List<AemAnimationCurve> output,
        AemParserOptions options,
        CancellationToken cancellationToken)
    {
        ushort storage = reader.ReadUInt16($"{field}.storage", "animation");
        if (storage == 0)
        {
            foreach (AemAnimationChannel channel in scalarChannels)
            {
                output.Add(ParseScalarCurve(
                    reader,
                    $"{field}.{channel}",
                    channel,
                    options,
                    cancellationToken));
            }
        }
        else if (storage == 1)
        {
            output.Add(ParseVectorCurve(
                reader,
                field,
                vectorChannel,
                options,
                cancellationToken));
        }
        else
        {
            throw reader.Unsupported(
                $"{field}.storage",
                $"Animation storage type {storage} is not understood.");
        }

        return storage;
    }

    private static AemAnimationCurve ParseScalarCurve(
        BoundedBinaryReader reader,
        string field,
        AemAnimationChannel channel,
        AemParserOptions options,
        CancellationToken cancellationToken)
    {
        ushort keyCount = reader.ReadUInt16($"{field}.keyCount", "animation");
        if (keyCount > options.MaximumAnimationKeysPerCurve)
        {
            throw reader.Corrupt(
                $"{field}.keyCount",
                $"Key count {keyCount} exceeds limit {options.MaximumAnimationKeysPerCurve}.");
        }

        List<AemAnimationKey> keys = new(keyCount);
        for (int index = 0; index < keyCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float time = reader.ReadSingle($"{field}.keys[{index}].time", "animation");
            float value = reader.ReadSingle($"{field}.keys[{index}].value", "animation");
            ValidateFinite(reader, time, $"{field}.keys[{index}].time");
            ValidateFinite(reader, value, $"{field}.keys[{index}].value");
            keys.Add(new AemAnimationKey(time, new Vector3(value, 0, 0), 1));
        }

        return new AemAnimationCurve(channel, keys);
    }

    private static AemAnimationCurve ParseVectorCurve(
        BoundedBinaryReader reader,
        string field,
        AemAnimationChannel channel,
        AemParserOptions options,
        CancellationToken cancellationToken)
    {
        ushort keyCount = reader.ReadUInt16($"{field}.keyCount", "animation");
        if (keyCount > options.MaximumAnimationKeysPerCurve)
        {
            throw reader.Corrupt(
                $"{field}.keyCount",
                $"Key count {keyCount} exceeds limit {options.MaximumAnimationKeysPerCurve}.");
        }

        List<AemAnimationKey> keys = new(keyCount);
        for (int index = 0; index < keyCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float time = reader.ReadSingle($"{field}.keys[{index}].time", "animation");
            Vector3 value = reader.ReadVector3($"{field}.keys[{index}].value", "animation");
            ValidateFinite(reader, time, $"{field}.keys[{index}].time");
            ValidateFinite(reader, value, $"{field}.keys[{index}].value");
            keys.Add(new AemAnimationKey(time, value, 3));
        }

        return new AemAnimationCurve(channel, keys);
    }

    private static void ValidateFinite(BoundedBinaryReader reader, float value, string field)
    {
        if (!float.IsFinite(value))
        {
            throw reader.Corrupt(field, $"Non-finite floating-point value {value}.");
        }
    }

    private static void ValidateFinite(BoundedBinaryReader reader, Vector2 value, string field)
    {
        ValidateFinite(reader, value.X, $"{field}.x");
        ValidateFinite(reader, value.Y, $"{field}.y");
    }

    private static void ValidateFinite(BoundedBinaryReader reader, Vector3 value, string field)
    {
        ValidateFinite(reader, value.X, $"{field}.x");
        ValidateFinite(reader, value.Y, $"{field}.y");
        ValidateFinite(reader, value.Z, $"{field}.z");
    }

    private static void ValidateFinite(BoundedBinaryReader reader, Vector4 value, string field)
    {
        ValidateFinite(reader, value.X, $"{field}.x");
        ValidateFinite(reader, value.Y, $"{field}.y");
        ValidateFinite(reader, value.Z, $"{field}.z");
        ValidateFinite(reader, value.W, $"{field}.w");
    }

    private static void ValidateFinite(
        BoundedBinaryReader reader,
        IEnumerable<Vector2> values,
        string field)
    {
        int index = 0;
        foreach (Vector2 value in values)
        {
            ValidateFinite(reader, value, $"{field}[{index}]");
            index++;
        }
    }

    private static void ValidateFinite(
        BoundedBinaryReader reader,
        IEnumerable<Vector3> values,
        string field)
    {
        int index = 0;
        foreach (Vector3 value in values)
        {
            ValidateFinite(reader, value, $"{field}[{index}]");
            index++;
        }
    }

    private static void ValidateFinite(
        BoundedBinaryReader reader,
        IEnumerable<Vector4> values,
        string field)
    {
        int index = 0;
        foreach (Vector4 value in values)
        {
            ValidateFinite(reader, value, $"{field}[{index}]");
            index++;
        }
    }

    private static void ValidateOptions(AemParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumSubmeshCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumVertexCountPerSubmesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumIndexCountPerSubmesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumAnimationKeysPerCurve);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTrailingBytes);
    }
}
