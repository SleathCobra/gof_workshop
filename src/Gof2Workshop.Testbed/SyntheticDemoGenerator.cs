using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.GameData;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Testbed;

internal static class SyntheticDemoGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Generate(string outputDirectory)
    {
        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Textures"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Models"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Imported"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Data"));
        Directory.CreateDirectory(Path.Combine(root, "SampleMod", ".work"));

        RgbaImage atlas = CreateAtlas(16, 16, alpha: false);
        RgbaImage alphaAtlas = CreateAtlas(16, 16, alpha: true);
        PngWriter.Write(atlas, Path.Combine(root, "synthetic-atlas-preview.png"));
        PngWriter.Write(
            CreateAtlas(8, 8, alpha: true),
            Path.Combine(root, "synthetic-region-replacement.png"));
        WriteAei(
            Path.Combine(root, "Assets", "Textures", "synthetic_raw.aei"),
            0x01,
            atlas,
            [(0, 0, 8, 8), (8, 0, 8, 8), (0, 8, 16, 8)]);
        WriteCompressedAei(
            Path.Combine(root, "Assets", "Textures", "synthetic_cube.aei"),
            0x20,
            atlas,
            mipmaps: false);
        WriteCompressedAei(
            Path.Combine(root, "Assets", "Textures", "synthetic_spacecraft_diffuse.aei"),
            0x24,
            alphaAtlas,
            mipmaps: false);
        WriteCompressedAei(
            Path.Combine(root, "Assets", "Textures", "synthetic_bc2_alpha.aei"),
            0x21,
            alphaAtlas,
            mipmaps: false);
        WriteAei(
            Path.Combine(root, "Assets", "Textures", "synthetic_overlap.aei"),
            0x01,
            atlas,
            [(0, 0, 10, 10), (6, 6, 10, 10)]);
        WriteCompressedAei(
            Path.Combine(root, "Assets", "Textures", "synthetic_mips.aei"),
            0x22,
            atlas,
            mipmaps: true);

        WriteCubeAem(Path.Combine(root, "Assets", "Models", "synthetic_cube.aem"));
        WriteSpacecraftAem(
            Path.Combine(root, "Assets", "Models", "synthetic_spacecraft_lod_1.aem"),
            animated: false);
        WriteSpacecraftAem(
            Path.Combine(root, "Assets", "Models", "synthetic_animated.aem"),
            animated: true);
        WriteLegacyAemFixtures(Path.Combine(root, "Assets", "Models"));
        WriteStaticV5Aem(Path.Combine(root, "Assets", "Models", "synthetic_v5.aem"));
        WriteLanguageTable(Path.Combine(root, "Assets", "Data", "synthetic.lang"));
        WriteGameDataFixtures(Path.Combine(root, "Assets", "Data"));
        WriteImportFixtures(root);

        string source = Path.Combine(root, "Assets", "Textures", "synthetic_raw.aei");
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));
        File.WriteAllText(
            Path.Combine(root, "project.gof2workspace"),
            JsonSerializer.Serialize(
                new
                {
                    FormatVersion = 1,
                    Name = "Public Synthetic Demo",
                    ModId = "workshop.synthetic-demo",
                    Author = "GOF2 Workshop contributors",
                    ModVersion = "0.1.0",
                    ProfileId = "gof2-pc-1x",
                    GameAssetRoot = "Assets",
                    ModRoot = "SampleMod",
                    OutputRoot = "SampleMod/Generated",
                    OpenDocuments = Array.Empty<object>(),
                    MaterialOverrides = new Dictionary<string, string>
                    {
                        ["Models/synthetic_cube.aem#primitive=0"] =
                            "Textures/synthetic_cube.aei",
                    },
                },
                JsonOptions));
        File.WriteAllText(
            Path.Combine(root, "SampleMod", ".work", "sample-region-operation.json"),
            JsonSerializer.Serialize(
                new
                {
                    formatVersion = 1,
                    sourceGameRelativePath = "Textures/synthetic_raw.aei",
                    originalSourceSha256 = sourceHash,
                    operation = "replace-region",
                    regionIndex = 0,
                    width = 8,
                    height = 8,
                    note = "Demonstration metadata only; replacement pixels are intentionally omitted.",
                },
                JsonOptions));
        File.WriteAllText(
            Path.Combine(root, "sample-mod.gof2manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    formatVersion = 1,
                    modId = "workshop.synthetic-demo",
                    name = "Public Synthetic Demo",
                    author = "GOF2 Workshop contributors",
                    version = "0.1.0",
                    targetProfile = "gof2-pc-1x",
                    assets = Array.Empty<object>(),
                },
                JsonOptions));
    }

    private static RgbaImage CreateAtlas(int width, int height, bool alpha)
    {
        RgbaImage image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool checker = ((x / 4) + (y / 4)) % 2 == 0;
                image.SetPixel(
                    x,
                    y,
                    new Rgba32(
                        checker ? (byte)240 : (byte)30,
                        (byte)(x * 255 / (width - 1)),
                        (byte)(y * 255 / (height - 1)),
                        alpha ? (byte)(64 + (x * 191 / (width - 1))) : byte.MaxValue));
            }
        }

        return image;
    }

    private static void WriteAei(
        string path,
        byte format,
        RgbaImage image,
        IReadOnlyList<(ushort X, ushort Y, ushort Width, ushort Height)> regions)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("AEimage\0"u8);
        writer.Write(format);
        writer.Write((ushort)image.Width);
        writer.Write((ushort)image.Height);
        writer.Write((ushort)regions.Count);
        foreach ((ushort x, ushort y, ushort width, ushort height) in regions)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(width);
            writer.Write(height);
        }

        writer.Write(image.ReadOnlyPixelBytes);
        writer.Write((ushort)0);
    }

    private static void WriteCompressedAei(
        string path,
        byte format,
        RgbaImage image,
        bool mipmaps)
    {
        AeiPixelEncoder encoder = new();
        AeiCompressionFormat compression = (format & 0xFD) switch
        {
            0x20 => AeiCompressionFormat.Dxt1,
            0x21 => AeiCompressionFormat.Dxt3,
            0x24 => AeiCompressionFormat.Dxt5,
            _ => throw new InvalidOperationException(),
        };
        using MemoryStream payload = new();
        RgbaImage mip = image;
        while (true)
        {
            byte[] bytes = encoder.EncodeSurface(
                mip,
                compression,
                new AeiEncodingOptions(AeiEncodingQuality.Best));
            payload.Write(bytes);
            if (!mipmaps || (mip.Width == 1 && mip.Height == 1))
            {
                break;
            }

            mip = Resize(mip, Math.Max(1, mip.Width / 2), Math.Max(1, mip.Height / 2));
        }

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("AEimage\0"u8);
        writer.Write(format);
        writer.Write((ushort)image.Width);
        writer.Write((ushort)image.Height);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)image.Width);
        writer.Write((ushort)image.Height);
        writer.Write((uint)payload.Length);
        writer.Write(payload.ToArray());
        writer.Write((ushort)0);
    }

    private static RgbaImage Resize(RgbaImage source, int width, int height)
    {
        RgbaImage result = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                result.SetPixel(
                    x,
                    y,
                    source.GetPixel(
                        Math.Min(source.Width - 1, x * source.Width / width),
                        Math.Min(source.Height - 1, y * source.Height / height)));
            }
        }

        return result;
    }

    private static void WriteCubeAem(string path)
    {
        Vector3[] positions =
        [
            new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
        ];
        ushort[] indices =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
            0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
        ];
        WriteAem(path, [(Vector3.Zero, positions, indices)], animated: false);
    }

    private static void WriteLegacyAemFixtures(string directory)
    {
        using (FileStream stream = File.Create(Path.Combine(directory, "synthetic_v1.aem")))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write("AEMesh\0"u8);
            writer.Write((byte)0x17);
            writer.Write((ushort)4);
            foreach (ushort index in new ushort[] { 0, 1, 2, 3 }) writer.Write(index);
            writer.Write((ushort)1);
            writer.Write((ushort)4);
            writer.Write((ushort)4);
            foreach (short value in new short[]
            {
                0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0,
                0, 0, 256, 0, 0, 256, 256, 256,
            }) writer.Write(value);
            for (int index = 0; index < 4; index++)
            {
                writer.Write((short)0);
                writer.Write((short)0);
                writer.Write((short)256);
            }

            writer.Write((byte)1);
        }

        using (FileStream stream = File.Create(Path.Combine(directory, "synthetic_v2.aem")))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write("V2AEMesh\0"u8);
            writer.Write((byte)0x17);
            WriteLegacyTriangle(writer);
            writer.Write((byte)0);
        }

        using (FileStream stream = File.Create(Path.Combine(directory, "synthetic_v3.aem")))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write("V3AEMesh\0"u8);
            writer.Write((byte)0x17);
            writer.Write((ushort)1);
            WriteVector3(writer, Vector3.Zero);
            WriteLegacyTriangle(writer);
            WriteVector4(writer, new Vector4(0.5f, 0.5f, 0, 2));
            WriteStaticTransform(writer);
            writer.Write((short)0);
        }
    }

    private static void WriteLegacyTriangle(BinaryWriter writer)
    {
        writer.Write((ushort)3);
        foreach (ushort index in new ushort[] { 0, 1, 2 }) writer.Write(index);
        writer.Write((ushort)3);
        foreach (int value in new[]
        {
            0, 0, 0, 65536, 0, 0, 0, 65536, 0,
        }) writer.Write(value);
        foreach (short value in new short[] { 0, 0, 4096, 0, 0, 4096 }) writer.Write(value);
        for (int index = 0; index < 3; index++)
        {
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write(short.MaxValue);
        }
    }

    private static void WriteStaticV5Aem(string path)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("V5AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)1);
        WriteVector3(writer, Vector3.Zero);
        writer.Write((ushort)3);
        foreach (ushort index in new ushort[] { 0, 1, 2 }) writer.Write(index);
        writer.Write((ushort)3);
        foreach (Vector3 point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }) WriteVector3(writer, point);
        writer.Write(0f); writer.Write(0f);
        writer.Write(1f); writer.Write(0f);
        writer.Write(0f); writer.Write(1f);
        for (int index = 0; index < 3; index++) WriteVector3(writer, Vector3.UnitZ);
        WriteVector4(writer, new Vector4(0.5f, 0.5f, 0, 2));
        WriteStaticTransform(writer);
        writer.Write((short)-1);
        writer.Write((short)0);
        writer.Write((short)0);
    }

    private static void WriteLanguageTable(string path)
    {
        LanguageTable table = new([
            new LanguageEntry(0, "Language", 0),
            new LanguageEntry(1, "Synthetic English", 0),
            new LanguageEntry(2, "Welcome to the public Workshop sample.", 0),
            new LanguageEntry(3, "Texture", 0),
            new LanguageEntry(4, "Model", 0),
        ]);
        using FileStream output = File.Create(path);
        new LanguageTableWriter().Write(table, output);
    }

    private static void WriteGameDataFixtures(string directory)
    {
        WriteBinary("names_synthetic_0.bin", writer =>
        {
            WriteInt32Big(writer, 2);
            WriteModifiedUtf(writer, "Ayla");
            WriteModifiedUtf(writer, "Boro");
        });
        WriteBinary("items.bin", writer =>
        {
            WriteInt32Big(writer, 0);
            WriteInt32Big(writer, 0);
            WriteInt32Big(writer, 0);
        });
        WriteBinary("ships.bin", writer =>
        {
            for (int value = 0; value < 9; value++) WriteInt32Big(writer, 100 + value);
        });
        WriteBinary("systems.bin", writer =>
        {
            WriteModifiedUtf(writer, "Aurora");
            for (int value = 0; value < 8; value++) WriteInt32Big(writer, value);
            for (int value = 0; value < 4; value++) WriteInt32Big(writer, 0);
        });
        WriteBinary("stations.bin", writer =>
        {
            WriteModifiedUtf(writer, "Horizon");
            for (int value = 0; value < 4; value++) WriteInt32Big(writer, value + 1);
        });
        WriteBinary("agents.bin", writer =>
        {
            WriteModifiedUtf(writer, "Nia");
            for (int value = 0; value < 8; value++) WriteInt32Big(writer, value);
            WriteInt32Big(writer, 0);
        });
        WriteBinary("wanted.bin", writer =>
        {
            WriteModifiedUtf(writer, "Rook");
            for (int value = 0; value < 13; value++) WriteInt32Big(writer, value);
            WriteInt32Big(writer, 0);
        });
        WriteBinary("ticker.bin", writer =>
        {
            for (int value = 0; value < 7; value++) WriteInt32Big(writer, value);
        });
        WriteBinary("shipparts.bin", writer =>
        {
            writer.WriteByte(1);
            writer.WriteByte(0);
        });
        WriteBinary("stationparts.bin", writer =>
        {
            writer.WriteByte(1);
            WriteInt16Big(writer, 5);
            writer.WriteByte(0);
        });
        WriteBinary("synthetic_weapons.bin", writer =>
        {
            WriteInt16Little(writer, 7);
            WriteInt16Little(writer, 1);
            WriteInt16Little(writer, 1);
            WriteInt16Little(writer, 10);
            WriteInt16Little(writer, 20);
            WriteInt16Little(writer, 30);
        });
        WriteBinary("collision.bin", writer => writer.Write([0x43, 0x43, 0x30, 0x31]));
        WriteBinary("docks.bin", writer => writer.Write([0x44, 0x4F, 0x43, 0x4B]));
        WriteBinary("weapons.bin", writer => writer.Write([0x57, 0x50, 0x4E, 0x31]));

        void WriteBinary(string name, Action<Stream> write)
        {
            using FileStream output = File.Create(Path.Combine(directory, name));
            write(output);
        }
    }

    private static void WriteInt32Big(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt16Big(Stream output, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt16Little(Stream output, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteModifiedUtf(Stream output, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteInt16Big(output, checked((short)bytes.Length));
        output.Write(bytes);
    }

    private static void WriteImportFixtures(string root)
    {
        string sourceAem = Path.Combine(root, "Assets", "Models", "synthetic_cube.aem");
        AemFile parsed = new AemParser().Parse(sourceAem);
        SceneDocument scene = new AemSceneConverter().Convert(parsed);
        string importDirectory = Path.Combine(root, "Assets", "Imported");
        _ = new GltfExporter().Export(scene, importDirectory, "synthetic_cube_import");
        _ = new ObjExporter().Export(scene, importDirectory, "synthetic_cube_import");
    }

    private static void WriteSpacecraftAem(string path, bool animated)
    {
        Vector3[] hull =
        [
            new(-2, 0, -3), new(2, 0, -3), new(0, 0.6f, 3),
            new(-2, 0, -3), new(0, -0.4f, 3), new(2, 0, -3),
        ];
        Vector3[] wing =
        [
            new(-5, 0, -1), new(5, 0, -1), new(0, 0.1f, 1.5f),
        ];
        WriteAem(
            path,
            [
                (Vector3.Zero, hull, new ushort[] { 0, 1, 2, 3, 4, 5 }),
                (new Vector3(0, 0, -0.5f), wing, new ushort[] { 0, 1, 2 }),
            ],
            animated);
    }

    private static void WriteAem(
        string path,
        IReadOnlyList<(Vector3 Pivot, Vector3[] Positions, ushort[] Indices)> meshes,
        bool animated)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("V4AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)meshes.Count);
        for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            (Vector3 pivot, Vector3[] positions, ushort[] indices) = meshes[meshIndex];
            WriteVector3(writer, pivot);
            writer.Write((ushort)indices.Length);
            foreach (ushort index in indices)
            {
                writer.Write(index);
            }

            writer.Write((ushort)positions.Length);
            foreach (Vector3 position in positions)
            {
                WriteVector3(writer, position);
            }

            foreach (Vector3 position in positions)
            {
                writer.Write((position.X + 5) / 10);
                writer.Write((position.Z + 5) / 10);
            }

            foreach (Vector3 position in positions)
            {
                Vector3 normal = Vector3.Normalize(position == Vector3.Zero ? Vector3.UnitY : position);
                WriteVector3(writer, normal);
            }

            WriteVector4(writer, new Vector4(0, 0, 0, 6));
            if (animated && meshIndex == 0)
            {
                WriteAnimatedTransform(writer);
            }
            else
            {
                WriteStaticTransform(writer);
            }

            writer.Write((short)-1);
            writer.Write((short)0);
        }
    }

    private static void WriteStaticTransform(BinaryWriter writer)
    {
        for (int index = 0; index < 8; index++)
        {
            writer.Write((ushort)0);
        }

        writer.Write((ushort)1);
        writer.Write((ushort)0);
    }

    private static void WriteAnimatedTransform(BinaryWriter writer)
    {
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write(0f);
        WriteVector3(writer, Vector3.Zero);
        writer.Write(1000f);
        WriteVector3(writer, new Vector3(0, 1, 0));
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }
}
