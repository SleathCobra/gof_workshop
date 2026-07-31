using System.Numerics;
using System.Text;

namespace Gof2Workshop.Formats.Aem;

/// <summary>
/// Writes validated AEM v1-v5 models using the version-specific source encoding.
/// The separate snapshot methods remain available when a byte-exact immutable copy is desired.
/// </summary>
public sealed class AemWriter
{
    public void Write(
        AemFile file,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                Write(file, output, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Write(
        AemFile file,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        }

        ValidateFile(file);
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(file.Signature));
        writer.Write((byte)0);
        writer.Write((byte)file.Flags);
        if (file.Version >= AemVersion.V3)
        {
            writer.Write(checked((ushort)file.Submeshes.Count));
        }

        foreach (AemSubmesh submesh in file.Submeshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Version >= AemVersion.V4)
            {
                WriteModernSubmesh(writer, file, submesh, cancellationToken);
            }
            else
            {
                WriteLegacySubmesh(writer, file, submesh, cancellationToken);
            }
        }

        writer.Write(file.UnknownTrailingData);
    }

    public void WriteSnapshot(AemFile file, string path)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(fullPath, file.OriginalData);
    }

    public void WriteSnapshot(AemFile file, Stream output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        }

        output.Write(file.OriginalData);
    }

    private static void WriteLegacySubmesh(
        BinaryWriter writer,
        AemFile file,
        AemSubmesh mesh,
        CancellationToken cancellationToken)
    {
        if (file.Version == AemVersion.V3)
        {
            WriteVector3(writer, mesh.Pivot);
        }

        if ((file.Flags & AemFlags.Indices) != 0)
        {
            ushort[] storedIndices = file.Version == AemVersion.V1
                ? mesh.SourceIndices
                    ?? throw new InvalidDataException(
                        "AEM v1 writing requires preserved source triangle-strip indices.")
                : mesh.Indices;
            writer.Write(checked((ushort)storedIndices.Length));
            WriteUInt16Array(writer, storedIndices, cancellationToken);
            if (file.Version == AemVersion.V1)
            {
                ushort[] stripLengths = mesh.SourceStripLengths
                    ?? throw new InvalidDataException(
                        "AEM v1 writing requires preserved source triangle-strip lengths.");
                if (stripLengths.Sum(value => (int)value) != storedIndices.Length)
                {
                    throw new InvalidDataException(
                        "AEM v1 strip lengths do not consume all preserved source indices.");
                }

                writer.Write(checked((ushort)stripLengths.Length));
                WriteUInt16Array(writer, stripLengths, cancellationToken);
            }
        }

        writer.Write(checked((ushort)mesh.Positions.Length));
        foreach (Vector3 position in mesh.Positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Version == AemVersion.V1)
            {
                writer.Write(QuantizeInt16(position.X, 1, "position.x"));
                writer.Write(QuantizeInt16(position.Y, 1, "position.y"));
                writer.Write(QuantizeInt16(position.Z, 1, "position.z"));
            }
            else
            {
                writer.Write(QuantizeInt32(position.X, 65_536, "position.x"));
                writer.Write(QuantizeInt32(position.Y, 65_536, "position.y"));
                writer.Write(QuantizeInt32(position.Z, 65_536, "position.z"));
            }
        }

        if (mesh.TextureCoordinates is not null)
        {
            int scale = file.Version == AemVersion.V1 ? 256 : 4_096;
            foreach (Vector2 textureCoordinate in mesh.TextureCoordinates)
            {
                writer.Write(QuantizeInt16(textureCoordinate.X, scale, "textureCoordinate.u"));
                writer.Write(QuantizeInt16(textureCoordinate.Y, scale, "textureCoordinate.v"));
            }
        }

        if (mesh.Normals is not null)
        {
            int scale = file.Version == AemVersion.V1 ? 256 : 32_768;
            foreach (Vector3 normal in mesh.Normals)
            {
                writer.Write(QuantizeNormalizedInt16(normal.X, scale, "normal.x"));
                writer.Write(QuantizeNormalizedInt16(normal.Y, scale, "normal.y"));
                writer.Write(QuantizeNormalizedInt16(normal.Z, scale, "normal.z"));
            }
        }

        if (mesh.AuxiliaryFloat4 is not null)
        {
            foreach (Vector4 auxiliary in mesh.AuxiliaryFloat4)
            {
                if (auxiliary.Z != 0 || auxiliary.W != 0)
                {
                    throw new InvalidDataException(
                        "AEM v1-v3 auxiliary values only encode two signed 16-bit components.");
                }

                writer.Write(QuantizeInt16(auxiliary.X, 1, "auxiliary.x"));
                writer.Write(QuantizeInt16(auxiliary.Y, 1, "auxiliary.y"));
            }
        }

        if (file.Version <= AemVersion.V2)
        {
            if (mesh.HasLegacyTransparencyByte)
            {
                writer.Write(mesh.IsTransparent ? (byte)1 : (byte)0);
            }
        }
        else
        {
            WriteBoundingSphere(writer, mesh.BoundingSphere);
            WriteAnimation(writer, file.Version, mesh.Animation, cancellationToken);
        }
    }

    private static void WriteModernSubmesh(
        BinaryWriter writer,
        AemFile file,
        AemSubmesh mesh,
        CancellationToken cancellationToken)
    {
        WriteVector3(writer, mesh.Pivot);
        writer.Write(checked((ushort)mesh.Indices.Length));
        WriteUInt16Array(writer, mesh.Indices, cancellationToken);
        writer.Write(checked((ushort)mesh.Positions.Length));
        WriteVector3Array(writer, mesh.Positions, cancellationToken);

        if (mesh.TextureCoordinates is not null)
        {
            foreach (Vector2 textureCoordinate in mesh.TextureCoordinates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(textureCoordinate.X);
                writer.Write(textureCoordinate.Y);
            }
        }

        if (mesh.Normals is not null)
        {
            WriteVector3Array(writer, mesh.Normals, cancellationToken);
        }

        if (mesh.AuxiliaryFloat4 is not null)
        {
            foreach (Vector4 auxiliary in mesh.AuxiliaryFloat4)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(auxiliary.X);
                writer.Write(auxiliary.Y);
                writer.Write(auxiliary.Z);
                writer.Write(auxiliary.W);
            }
        }

        WriteBoundingSphere(writer, mesh.BoundingSphere);
        WriteAnimation(writer, file.Version, mesh.Animation, cancellationToken);
    }

    private static void WriteAnimation(
        BinaryWriter writer,
        AemVersion version,
        AemAnimation animation,
        CancellationToken cancellationToken)
    {
        HashSet<AemAnimationChannel> consumed = [];
        WriteTransformGroup(
            writer,
            animation,
            animation.TranslationStorage,
            [
                AemAnimationChannel.TranslationX,
                AemAnimationChannel.TranslationY,
                AemAnimationChannel.TranslationZ,
            ],
            AemAnimationChannel.TranslationXyz,
            consumed,
            cancellationToken);
        WriteTransformGroup(
            writer,
            animation,
            animation.RotationStorage,
            [
                AemAnimationChannel.RotationX,
                AemAnimationChannel.RotationY,
                AemAnimationChannel.RotationZ,
            ],
            AemAnimationChannel.RotationXyz,
            consumed,
            cancellationToken);
        WriteTransformGroup(
            writer,
            animation,
            animation.ScaleStorage,
            [
                AemAnimationChannel.ScaleX,
                AemAnimationChannel.ScaleY,
                AemAnimationChannel.ScaleZ,
            ],
            AemAnimationChannel.ScaleXyz,
            consumed,
            cancellationToken);

        if (version >= AemVersion.V4)
        {
            writer.Write(animation.SpecialV4Type);
            if (animation.SpecialV4Type == 2)
            {
                WriteScalarCurve(
                    writer,
                    RequireCurve(animation, AemAnimationChannel.SpecialV4),
                    cancellationToken);
                consumed.Add(AemAnimationChannel.SpecialV4);
            }
        }

        if (version == AemVersion.V5)
        {
            short marker = animation.V5UvMarker
                ?? throw new InvalidDataException("AEM v5 animation is missing its UV marker.");
            writer.Write(marker);
            if (marker != 0)
            {
                foreach (AemAnimationChannel channel in new[]
                {
                    AemAnimationChannel.UvOffsetX,
                    AemAnimationChannel.UvOffsetY,
                    AemAnimationChannel.UvScaleX,
                    AemAnimationChannel.UvScaleY,
                    AemAnimationChannel.UnknownV5A,
                    AemAnimationChannel.UnknownV5B,
                    AemAnimationChannel.UvRotationZ,
                })
                {
                    WriteScalarCurve(writer, RequireCurve(animation, channel), cancellationToken);
                    consumed.Add(channel);
                }
            }
        }

        AemAnimationChannel[] unexpected = animation.Curves
            .Select(curve => curve.Channel)
            .Where(channel => !consumed.Contains(channel))
            .Distinct()
            .ToArray();
        if (unexpected.Length != 0)
        {
            throw new InvalidDataException(
                $"Animation contains channels that its storage markers cannot encode: " +
                $"{string.Join(", ", unexpected)}.");
        }

        writer.Write(animation.Padding);
    }

    private static void WriteTransformGroup(
        BinaryWriter writer,
        AemAnimation animation,
        ushort storage,
        IReadOnlyList<AemAnimationChannel> scalarChannels,
        AemAnimationChannel vectorChannel,
        HashSet<AemAnimationChannel> consumed,
        CancellationToken cancellationToken)
    {
        writer.Write(storage);
        if (storage == 0)
        {
            foreach (AemAnimationChannel channel in scalarChannels)
            {
                WriteScalarCurve(writer, RequireCurve(animation, channel), cancellationToken);
                consumed.Add(channel);
            }

            return;
        }

        if (storage == 1)
        {
            WriteVectorCurve(writer, RequireCurve(animation, vectorChannel), cancellationToken);
            consumed.Add(vectorChannel);
            return;
        }

        throw new NotSupportedException($"AEM animation storage type {storage} cannot be written.");
    }

    private static AemAnimationCurve RequireCurve(
        AemAnimation animation,
        AemAnimationChannel channel)
    {
        AemAnimationCurve[] matches = animation.Curves
            .Where(curve => curve.Channel == channel)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Animation channel {channel} must occur exactly once; found {matches.Length}.");
        }

        return matches[0];
    }

    private static void WriteScalarCurve(
        BinaryWriter writer,
        AemAnimationCurve curve,
        CancellationToken cancellationToken)
    {
        writer.Write(checked((ushort)curve.Keys.Count));
        foreach (AemAnimationKey key in curve.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.ComponentCount != 1)
            {
                throw new InvalidDataException(
                    $"Animation channel {curve.Channel} requires scalar keys.");
            }

            ValidateFinite(key.Time, $"{curve.Channel}.time");
            ValidateFinite(key.Value.X, $"{curve.Channel}.value");
            writer.Write(key.Time);
            writer.Write(key.Value.X);
        }
    }

    private static void WriteVectorCurve(
        BinaryWriter writer,
        AemAnimationCurve curve,
        CancellationToken cancellationToken)
    {
        writer.Write(checked((ushort)curve.Keys.Count));
        foreach (AemAnimationKey key in curve.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.ComponentCount != 3)
            {
                throw new InvalidDataException(
                    $"Animation channel {curve.Channel} requires three-component keys.");
            }

            ValidateFinite(key.Time, $"{curve.Channel}.time");
            ValidateFinite(key.Value, $"{curve.Channel}.value");
            writer.Write(key.Time);
            WriteVector3(writer, key.Value);
        }
    }

    private static void ValidateFile(AemFile file)
    {
        string expectedSignature = file.Version switch
        {
            AemVersion.V1 => "AEMesh",
            AemVersion.V2 => "V2AEMesh",
            AemVersion.V3 => "V3AEMesh",
            AemVersion.V4 => "V4AEMesh",
            AemVersion.V5 => "V5AEMesh",
            _ => throw new NotSupportedException($"AEM version {(int)file.Version} cannot be written."),
        };
        if (!file.Signature.Equals(expectedSignature, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"AEM signature '{file.Signature}' does not match version {(int)file.Version}.");
        }

        if ((file.Flags & AemFlags.BaseMesh) == 0)
        {
            throw new InvalidDataException("AEM writing requires the base-mesh flag.");
        }

        if (file.Submeshes.Count == 0 || file.Submeshes.Count > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"AEM submesh count {file.Submeshes.Count} is outside 1..{ushort.MaxValue}.");
        }

        if (file.Version <= AemVersion.V2 && file.Submeshes.Count != 1)
        {
            throw new InvalidDataException($"AEM v{(int)file.Version} stores exactly one submesh.");
        }

        foreach (AemSubmesh mesh in file.Submeshes)
        {
            ValidateSubmesh(file, mesh);
        }
    }

    private static void ValidateSubmesh(AemFile file, AemSubmesh mesh)
    {
        if (mesh.Positions.Length > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"Submesh {mesh.Index} vertex count exceeds {ushort.MaxValue}.");
        }

        if (mesh.Positions.Length == 0 && mesh.Indices.Length != 0)
        {
            throw new InvalidDataException(
                $"Empty submesh {mesh.Index} cannot contain indices.");
        }

        ValidateFinite(mesh.Pivot, $"submesh {mesh.Index} pivot");
        foreach (Vector3 position in mesh.Positions)
        {
            ValidateFinite(position, $"submesh {mesh.Index} position");
        }

        ValidateOptionalChannel(
            file,
            mesh,
            AemFlags.TextureCoordinates,
            mesh.TextureCoordinates,
            "texture coordinates");
        ValidateOptionalChannel(file, mesh, AemFlags.Normals, mesh.Normals, "normals");
        ValidateOptionalChannel(
            file,
            mesh,
            AemFlags.AuxiliaryFloat4,
            mesh.AuxiliaryFloat4,
            "auxiliary values");
        ValidateFinite(mesh.BoundingSphere.Center, $"submesh {mesh.Index} bounding-sphere center");
        ValidateFinite(mesh.BoundingSphere.Radius, $"submesh {mesh.Index} bounding-sphere radius");

        if ((file.Flags & AemFlags.Indices) != 0 || file.Version >= AemVersion.V4)
        {
            ushort[] indices = file.Version == AemVersion.V1
                ? mesh.SourceIndices ?? []
                : mesh.Indices;
            if (indices.Length > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"Submesh {mesh.Index} index count exceeds {ushort.MaxValue}.");
            }

            foreach (ushort index in indices)
            {
                if (index >= mesh.Positions.Length)
                {
                    throw new InvalidDataException(
                        $"Submesh {mesh.Index} index {index} exceeds its vertex count.");
                }
            }
        }
        else if (!mesh.Indices.SequenceEqual(
            Enumerable.Range(0, mesh.Positions.Length).Select(value => checked((ushort)value))))
        {
            throw new InvalidDataException(
                $"Submesh {mesh.Index} has edited indices but its flags require implicit draw-array order.");
        }
    }

    private static void ValidateOptionalChannel<T>(
        AemFile file,
        AemSubmesh mesh,
        AemFlags flag,
        T[]? values,
        string name)
        where T : struct
    {
        bool expected = (file.Flags & flag) != 0;
        if (expected && values is null)
        {
            throw new InvalidDataException(
                $"Submesh {mesh.Index} is missing {name} required by flag 0x{(byte)flag:X2}.");
        }

        if (!expected && values is not null)
        {
            throw new InvalidDataException(
                $"Submesh {mesh.Index} has {name} but flag 0x{(byte)flag:X2} is not set.");
        }

        if (values is not null && values.Length != mesh.Positions.Length)
        {
            throw new InvalidDataException(
                $"Submesh {mesh.Index} has {values.Length} {name} for " +
                $"{mesh.Positions.Length} vertices.");
        }

        if (values is IEnumerable<Vector2> vectors2)
        {
            foreach (Vector2 value in vectors2)
            {
                ValidateFinite(value, $"submesh {mesh.Index} {name}");
            }
        }
        else if (values is IEnumerable<Vector3> vectors3)
        {
            foreach (Vector3 value in vectors3)
            {
                ValidateFinite(value, $"submesh {mesh.Index} {name}");
            }
        }
        else if (values is IEnumerable<Vector4> vectors4)
        {
            foreach (Vector4 value in vectors4)
            {
                ValidateFinite(value, $"submesh {mesh.Index} {name}");
            }
        }
    }

    private static void WriteUInt16Array(
        BinaryWriter writer,
        IEnumerable<ushort> values,
        CancellationToken cancellationToken)
    {
        foreach (ushort value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(value);
        }
    }

    private static void WriteVector3Array(
        BinaryWriter writer,
        IEnumerable<Vector3> values,
        CancellationToken cancellationToken)
    {
        foreach (Vector3 value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteVector3(writer, value);
        }
    }

    private static void WriteBoundingSphere(BinaryWriter writer, AemBoundingSphere sphere)
    {
        WriteVector3(writer, sphere.Center);
        writer.Write(sphere.Radius);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static short QuantizeNormalizedInt16(float value, int scale, string field)
    {
        if (scale == 32_768 && value == 1f)
        {
            return short.MaxValue;
        }

        return QuantizeInt16(value, scale, field);
    }

    private static short QuantizeInt16(float value, int scale, string field)
    {
        ValidateFinite(value, field);
        double scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
        if (scaled < short.MinValue || scaled > short.MaxValue)
        {
            throw new InvalidDataException(
                $"{field} value {value} cannot be represented with scale {scale}.");
        }

        return (short)scaled;
    }

    private static int QuantizeInt32(float value, int scale, string field)
    {
        ValidateFinite(value, field);
        double scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
        if (scaled < int.MinValue || scaled > int.MaxValue)
        {
            throw new InvalidDataException(
                $"{field} value {value} cannot be represented with scale {scale}.");
        }

        return (int)scaled;
    }

    private static void ValidateFinite(float value, string field)
    {
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException($"{field} is not finite.");
        }
    }

    private static void ValidateFinite(Vector2 value, string field)
    {
        ValidateFinite(value.X, $"{field}.x");
        ValidateFinite(value.Y, $"{field}.y");
    }

    private static void ValidateFinite(Vector3 value, string field)
    {
        ValidateFinite(value.X, $"{field}.x");
        ValidateFinite(value.Y, $"{field}.y");
        ValidateFinite(value.Z, $"{field}.z");
    }

    private static void ValidateFinite(Vector4 value, string field)
    {
        ValidateFinite(value.X, $"{field}.x");
        ValidateFinite(value.Y, $"{field}.y");
        ValidateFinite(value.Z, $"{field}.z");
        ValidateFinite(value.W, $"{field}.w");
    }
}
