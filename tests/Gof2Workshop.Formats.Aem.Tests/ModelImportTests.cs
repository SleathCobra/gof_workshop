using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Import;

namespace Gof2Workshop.Formats.Aem.Tests;

[TestClass]
public sealed class ModelImportTests
{
    [TestMethod]
    public void EmbeddedGltfImportsAndAuthorsValidatedV4Aem()
    {
        byte[] gltf = CreateGltf(includeDataUri: true, out _);

        ImportedScene imported = new GltfModelImporter().Import(gltf, "triangle.gltf");
        AemAuthoringResult authored = new AemAuthoringService().Author(imported);

        Assert.HasCount(1, imported.Primitives);
        Assert.AreEqual(AemVersion.V4, authored.Reparsed.Version);
        Assert.HasCount(1, authored.Reparsed.Submeshes);
        Assert.AreEqual(3, authored.Reparsed.Submeshes[0].Positions.Length);
        Assert.AreEqual(3, authored.Reparsed.Submeshes[0].Indices.Length);
        Assert.AreEqual(1, authored.Scene.Primitives.Count);
    }

    [TestMethod]
    public void GlbImportsSameTriangle()
    {
        byte[] json = CreateGltf(includeDataUri: false, out byte[] binary);
        byte[] glb = CreateGlb(json, binary);

        ImportedScene imported = new GltfModelImporter().Import(glb, "triangle.glb");

        Assert.HasCount(1, imported.Primitives);
        CollectionAssert.AreEqual(new ushort[] { 0, 1, 2 }, imported.Primitives[0].Indices);
    }

    [TestMethod]
    public void ObjTriangulatesAndAuthorsV5Aem()
    {
        const string obj = """
            v -1 0 0
            v 1 0 0
            v 1 1 0
            v -1 1 0
            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            vn 0 0 1
            usemtl synthetic
            f 1/1/1 2/2/1 3/3/1 4/4/1
            """;

        ImportedScene imported = new ObjModelImporter().Import(obj, "quad");
        AemAuthoringResult authored = new AemAuthoringService().Author(
            imported,
            new AemAuthoringOptions(AemVersion.V5));

        Assert.AreEqual(6, imported.Primitives[0].Indices.Length);
        Assert.AreEqual(AemVersion.V5, authored.Reparsed.Version);
        Assert.AreEqual(4, authored.Reparsed.Submeshes[0].Positions.Length);
    }

    [TestMethod]
    public void NonTriangleGltfModeIsRejectedWithoutSilentConversion()
    {
        byte[] gltf = CreateGltf(includeDataUri: true, out _);
        string json = Encoding.UTF8.GetString(gltf).Replace(
            "\"mode\":4",
            "\"mode\":5",
            StringComparison.Ordinal);

        Assert.Throws<NotSupportedException>(() =>
            new GltfModelImporter().Import(Encoding.UTF8.GetBytes(json), "strip.gltf"));
    }

    private static byte[] CreateGltf(bool includeDataUri, out byte[] binary)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (Vector3 value in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY })
            {
                writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
            }

            for (int index = 0; index < 3; index++)
            {
                writer.Write(0f); writer.Write(0f); writer.Write(1f);
            }

            foreach (Vector2 value in new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY })
            {
                writer.Write(value.X); writer.Write(value.Y);
            }

            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
        }

        binary = stream.ToArray();
        object buffer = includeDataUri
            ? new { byteLength = binary.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(binary) }
            : new { byteLength = binary.Length };
        var model = new
        {
            asset = new { version = "2.0", generator = "Gof2Workshop synthetic test" },
            buffers = new[] { buffer },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = 36 },
                new { buffer = 0, byteOffset = 36, byteLength = 36 },
                new { buffer = 0, byteOffset = 72, byteLength = 24 },
                new { buffer = 0, byteOffset = 96, byteLength = 6 },
            },
            accessors = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3" },
                new { bufferView = 1, componentType = 5126, count = 3, type = "VEC3" },
                new { bufferView = 2, componentType = 5126, count = 3, type = "VEC2" },
                new { bufferView = 3, componentType = 5123, count = 3, type = "SCALAR" },
            },
            meshes = new[]
            {
                new
                {
                    name = "SyntheticTriangle",
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new Dictionary<string, int>
                            {
                                ["POSITION"] = 0,
                                ["NORMAL"] = 1,
                                ["TEXCOORD_0"] = 2,
                            },
                            indices = 3,
                            mode = 4,
                        },
                    },
                },
            },
            nodes = new[] { new { mesh = 0, name = "SyntheticNode" } },
            scenes = new[] { new { nodes = new[] { 0 } } },
            scene = 0,
        };
        return JsonSerializer.SerializeToUtf8Bytes(model);
    }

    private static byte[] CreateGlb(byte[] json, byte[] binary)
    {
        int paddedJson = (json.Length + 3) & ~3;
        int paddedBinary = (binary.Length + 3) & ~3;
        byte[] glb = new byte[12 + 8 + paddedJson + 8 + paddedBinary];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), checked((uint)glb.Length));
        int offset = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset), checked((uint)paddedJson));
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset + 4), 0x4E4F534A);
        json.CopyTo(glb, offset + 8);
        Array.Fill(glb, (byte)0x20, offset + 8 + json.Length, paddedJson - json.Length);
        offset += 8 + paddedJson;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset), checked((uint)paddedBinary));
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset + 4), 0x004E4942);
        binary.CopyTo(glb, offset + 8);
        return glb;
    }
}
