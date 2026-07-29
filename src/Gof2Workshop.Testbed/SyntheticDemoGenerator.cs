using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;

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
                    ProfileId = "pc-1x",
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
                    targetProfile = "pc-1x",
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
