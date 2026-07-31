using System.Numerics;
using System.Text.Json;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Formats.Aem.Tests;

[TestClass]
public sealed class AemParserTests
{
    [TestMethod]
    public void V4FixtureParsesGeometryBoundsAndStaticAnimation()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: false));

        AemFile file = new AemParser().Parse(stream, "triangle.aem");

        Assert.AreEqual(AemVersion.V4, file.Version);
        Assert.HasCount(1, file.Submeshes);
        AemSubmesh mesh = file.Submeshes[0];
        Assert.HasCount(3, mesh.Positions);
        Assert.HasCount(3, mesh.Indices);
        Assert.IsNotNull(mesh.TextureCoordinates);
        Assert.IsNotNull(mesh.Normals);
        Assert.AreEqual(2.0f, mesh.BoundingSphere.Radius);
        Assert.AreEqual(-1, mesh.Animation.SpecialV4Type);
        Assert.IsTrue(mesh.Animation.Curves.All(curve => curve.Keys.Count == 0));
    }

    [TestMethod]
    public void V4AuxiliaryFloat4IsPreserved()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: true));

        AemFile file = new AemParser().Parse(stream, "colors.aem");

        Assert.IsNotNull(file.Submeshes[0].AuxiliaryFloat4);
        Assert.AreEqual(Vector4.One, file.Submeshes[0].AuxiliaryFloat4![0]);
    }

    [TestMethod]
    public void V5FixtureParsesSpecialAndUvAnimationCurves()
    {
        using MemoryStream stream = new(CreateV5AnimatedFixture());

        AemFile file = new AemParser().Parse(stream, "animated.aem");
        AemAnimation animation = file.Submeshes[0].Animation;

        Assert.AreEqual(AemVersion.V5, file.Version);
        Assert.AreEqual(2, animation.SpecialV4Type);
        Assert.AreEqual<short?>(1, animation.V5UvMarker);
        AemAnimationCurve special = animation.Curves.Single(
            curve => curve.Channel == AemAnimationChannel.SpecialV4);
        Assert.AreEqual(50.0f, special.Keys[0].Value.X);
        AemAnimationCurve uvX = animation.Curves.Single(
            curve => curve.Channel == AemAnimationChannel.UvOffsetX);
        Assert.AreEqual(25.0f, uvX.Keys[0].Value.X);
    }

    [TestMethod]
    public void InvalidVertexIndexFailsSafely()
    {
        byte[] fixture = CreateV4Fixture(includeAuxiliary: false);
        fixture[28] = 9;
        fixture[29] = 0;
        using MemoryStream stream = new(fixture);

        FormatParseException exception = Assert.Throws<FormatParseException>(
            () => new AemParser().Parse(stream, "bad-index.aem"));

        Assert.AreEqual(FormatFailureKind.Corrupt, exception.FailureKind);
        StringAssert.Contains(exception.Field, "indices");
    }

    [TestMethod]
    public void TruncatedMeshFailsSafely()
    {
        byte[] fixture = CreateV4Fixture(includeAuxiliary: false);
        Array.Resize(ref fixture, 24);
        using MemoryStream stream = new(fixture);

        FormatParseException exception = Assert.Throws<FormatParseException>(
            () => new AemParser().Parse(stream, "truncated.aem"));

        Assert.AreEqual(FormatFailureKind.Corrupt, exception.FailureKind);
    }

    [TestMethod]
    public void V2FixedPointFixtureParsesGeometry()
    {
        using MemoryStream stream = new(CreateV2Fixture());

        AemFile file = new AemParser().Parse(stream, "v2.aem");

        Assert.AreEqual(AemVersion.V2, file.Version);
        Assert.HasCount(3, file.Submeshes[0].Positions);
        Assert.AreEqual(1.0f, file.Submeshes[0].Positions[1].X);
        Assert.AreEqual(1.0f, file.Submeshes[0].TextureCoordinates![1].X);
        Assert.AreEqual(1.0f, file.Submeshes[0].Normals![0].Z, 0.0001f);
    }

    [TestMethod]
    public void V1TriangleStripFixtureExpandsWithAlternatingWinding()
    {
        using MemoryStream stream = new(CreateV1Fixture());

        AemSubmesh mesh = new AemParser().Parse(stream, "v1.aem").Submeshes[0];

        Assert.AreEqual(AemPrimitiveTopology.TriangleStrips, mesh.SourceTopology);
        CollectionAssert.AreEqual(
            new ushort[] { 0, 1, 2, 1, 3, 2 },
            mesh.Indices);
        Assert.IsTrue(mesh.IsTransparent);
    }

    [TestMethod]
    public void V3FixedPointFixtureParsesBoundsAndAnimationHeader()
    {
        using MemoryStream stream = new(CreateV3Fixture());

        AemFile file = new AemParser().Parse(stream, "v3.aem");

        Assert.AreEqual(AemVersion.V3, file.Version);
        Assert.AreEqual(new Vector3(2, 3, 4), file.Submeshes[0].Pivot);
        Assert.AreEqual(2f, file.Submeshes[0].BoundingSphere.Radius);
        Assert.AreEqual(0, file.UnknownTrailingData.Length);
    }

    [TestMethod]
    public void SceneConversionReportsAlignedWindingAndFlipsUvV()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: false));
        AemFile file = new AemParser().Parse(stream, "triangle.aem");

        SceneDocument scene = new AemSceneConverter().Convert(file);

        Assert.AreEqual(1.0f, scene.Primitives[0].TextureCoordinates![0].Y);
        Assert.IsTrue(scene.Diagnostics.Any(
            diagnostic => diagnostic.Code == "AEM_WINDING"
                && diagnostic.Message.Contains("aligned=1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ExportersProduceStructurallyConsistentFilesAndPreview()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: true));
        SceneDocument scene = new AemSceneConverter().Convert(
            new AemParser().Parse(stream, "triangle.aem"));
        string directory = Path.Combine(Path.GetTempPath(), "gof2-workshop-tests", Guid.NewGuid().ToString("N"));

        try
        {
            ObjExportResult obj = new ObjExporter().Export(scene, directory);
            GltfExportResult gltf = new GltfExporter().Export(scene, directory);
            ScenePreviewResult preview = new ScenePreviewRenderer().Render(scene, new ScenePreviewOptions(128, 128));

            StringAssert.Contains(File.ReadAllText(obj.ObjPath), "f 1/1/1 2/2/2 3/3/3");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(gltf.GltfPath));
            int declaredLength = document.RootElement
                .GetProperty("buffers")[0]
                .GetProperty("byteLength")
                .GetInt32();
            Assert.AreEqual(new FileInfo(gltf.BinaryPath).Length, declaredLength);
            Assert.AreEqual(128, preview.Image.Width);
            Assert.IsGreaterThan(0, preview.RenderedTriangleCount);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void GltfExporterWritesDeduplicatedTextureAndMaterialBinding()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: false));
        SceneDocument scene = new AemSceneConverter().Convert(
            new AemParser().Parse(stream, "textured-triangle.aem"));
        RgbaImage texture = new(2, 2);
        texture.PixelBytes.Fill(255);
        string directory = Path.Combine(
            Path.GetTempPath(),
            "gof2-workshop-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            GltfExportResult result = new GltfExporter().ExportWithMaterials(
                scene,
                directory,
                "textured",
                [new GltfTextureAssignment(0, "synthetic-cache-key", "Synthetic", texture, true)]);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.GltfPath));

            Assert.HasCount(1, result.TexturePaths!);
            Assert.HasCount(0, result.UnresolvedMaterialPrimitives!);
            Assert.AreEqual(1, document.RootElement.GetProperty("images").GetArrayLength());
            Assert.AreEqual(1, document.RootElement.GetProperty("textures").GetArrayLength());
            Assert.AreEqual(
                "BLEND",
                document.RootElement.GetProperty("materials")[0].GetProperty("alphaMode").GetString());
            Assert.IsTrue(File.Exists(result.TexturePaths![0]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void InteractiveCameraAndWindingDiagnosticRenderSafely()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: false));
        SceneDocument scene = new AemSceneConverter().Convert(
            new AemParser().Parse(stream, "triangle.aem"));
        ScenePreviewRenderer renderer = new();

        ScenePreviewResult result = renderer.Render(
            scene,
            new ScenePreviewOptions(
                Width: 160,
                Height: 120,
                ShowNormals: true,
                Camera: new SceneCamera(
                    Yaw: 0.8f,
                    Pitch: -0.25f,
                    PanX: 0.1f,
                    PanY: -0.1f,
                    Zoom: 1.5f,
                    Perspective: true),
                IsolatedPrimitiveIndex: 0,
                ShowFaceWinding: true));

        Assert.AreEqual(160, result.Image.Width);
        Assert.AreEqual(120, result.Image.Height);
        Assert.AreEqual(1, result.RenderedTriangleCount);
        Assert.AreEqual(3, result.NormalLineCount);
    }

    [TestMethod]
    public void SoftwarePreviewSamplesAssignedRgbaTexture()
    {
        using MemoryStream stream = new(CreateV4Fixture(includeAuxiliary: false));
        SceneDocument scene = new AemSceneConverter().Convert(
            new AemParser().Parse(stream, "textured.aem"));
        RgbaImage texture = new(2, 2);
        texture.SetPixel(0, 0, new Rgba32(255, 0, 0, 255));
        texture.SetPixel(1, 0, new Rgba32(0, 255, 0, 255));
        texture.SetPixel(0, 1, new Rgba32(0, 0, 255, 255));
        texture.SetPixel(1, 1, new Rgba32(255, 255, 0, 255));
        ScenePreviewRenderer renderer = new();
        ScenePreviewOptions common = new(
            128,
            128,
            Wireframe: false,
            ShowNormals: false,
            ShowPivots: false,
            ShowBoundingSpheres: false);
        RgbaImage flat = renderer.Render(scene, common).Image;
        RgbaImage textured = renderer.Render(
            scene,
            common with { Textures = new Dictionary<int, RgbaImage> { [0] = texture } }).Image;

        CollectionAssert.AreNotEqual(
            flat.ReadOnlyPixelBytes.ToArray(),
            textured.ReadOnlyPixelBytes.ToArray());
    }

    [TestMethod]
    public void SnapshotWriterRoundTripsEverySupportedLayout()
    {
        foreach (byte[] fixture in new[]
        {
            CreateV1Fixture(),
            CreateV2Fixture(),
            CreateV3Fixture(),
            CreateV4Fixture(includeAuxiliary: false),
            CreateV5AnimatedFixture(),
        })
        {
            using MemoryStream input = new(fixture);
            AemFile file = new AemParser().Parse(input, "snapshot.aem");
            using MemoryStream output = new();

            new AemWriter().WriteSnapshot(file, output);

            CollectionAssert.AreEqual(fixture, output.ToArray());
        }
    }

    [TestMethod]
    public void StructuralWriterRoundTripsEverySupportedLayout()
    {
        foreach (byte[] fixture in new[]
        {
            CreateV1Fixture(),
            CreateV2Fixture(),
            CreateV3Fixture(),
            CreateV4Fixture(includeAuxiliary: false),
            CreateV5AnimatedFixture(),
        })
        {
            using MemoryStream input = new(fixture);
            AemFile file = new AemParser().Parse(input, "structural.aem");
            using MemoryStream output = new();

            new AemWriter().Write(file, output);

            CollectionAssert.AreEqual(fixture, output.ToArray());
        }
    }

    [TestMethod]
    public void StructuralWriterPersistsEditedModernGeometry()
    {
        using MemoryStream input = new(CreateV4Fixture(includeAuxiliary: false));
        AemFile file = new AemParser().Parse(input, "edited.aem");
        Vector3[] positions = file.Submeshes[0].Positions.ToArray();
        positions[1] = new Vector3(3, 4, 5);
        AemSubmesh editedMesh = file.Submeshes[0] with { Positions = positions };
        AemFile editedFile = file with { Submeshes = [editedMesh] };
        using MemoryStream output = new();

        new AemWriter().Write(editedFile, output);
        output.Position = 0;
        AemFile reparsed = new AemParser().Parse(output, "edited-copy.aem");

        Assert.AreEqual(new Vector3(3, 4, 5), reparsed.Submeshes[0].Positions[1]);
    }

    [TestMethod]
    public void StructuralWriterDoesNotReplaceDestinationWhenValidationFails()
    {
        using MemoryStream input = new(CreateV4Fixture(includeAuxiliary: false));
        AemFile file = new AemParser().Parse(input, "invalid-edit.aem");
        Vector3[] positions = file.Submeshes[0].Positions.ToArray();
        positions[0] = new Vector3(float.NaN, 0, 0);
        AemFile invalid = file with
        {
            Submeshes = [file.Submeshes[0] with { Positions = positions }],
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "gof2-workshop-tests",
            Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "model.aem");
        byte[] sentinel = [1, 2, 3, 4];

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(destination, sentinel);

            Assert.Throws<InvalidDataException>(
                () => new AemWriter().Write(invalid, destination));

            CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(destination));
            Assert.IsEmpty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TransformAnimationEvaluatesAndExportsToGltf()
    {
        using MemoryStream input = new(CreateV4TransformAnimatedFixture());
        SceneDocument scene = new AemSceneConverter().Convert(
            new AemParser().Parse(input, "animated-transform.aem"));

        Assert.HasCount(1, scene.Animations);
        Assert.AreEqual(1f, scene.Animations[0].DurationSeconds);
        SceneTransform halfway = SceneAnimationEvaluator.Evaluate(
            scene.Animations[0],
            primitiveIndex: 0,
            timeSeconds: 0.5f,
            loop: false);
        Assert.AreEqual(5f, halfway.Translation.X, 0.0001f);

        string directory = Path.Combine(
            Path.GetTempPath(),
            "gof2-workshop-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            GltfExportResult result = new GltfExporter().Export(scene, directory);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.GltfPath));
            JsonElement animations = document.RootElement.GetProperty("animations");
            Assert.AreEqual(1, animations.GetArrayLength());
            Assert.AreEqual(
                "translation",
                animations[0].GetProperty("channels")[0]
                    .GetProperty("target")
                    .GetProperty("path")
                    .GetString());
            StringAssert.Contains(result.AnimationStatus, "exported");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ScalarAnimationUsesEngineAxisRotationAndInterpolationSemantics()
    {
        using MemoryStream input = new(CreateV4ScalarTransformFixture());
        SceneDocument scene = new AemSceneConverter().Convert(
            new AemParser().Parse(input, "scalar-transform.aem"));

        SceneTransform first = SceneAnimationEvaluator.Evaluate(
            scene.Animations[0],
            primitiveIndex: 0,
            timeSeconds: 0,
            loop: false);
        Assert.AreEqual(new Vector3(1, 3, -2), first.Translation);
        Assert.AreEqual(0f, first.Rotation.X, 0.0001f);
        Assert.AreEqual(-MathF.Sqrt(0.5f), first.Rotation.Y, 0.0001f);
        Assert.AreEqual(0f, first.Rotation.Z, 0.0001f);
        Assert.AreEqual(MathF.Sqrt(0.5f), first.Rotation.W, 0.0001f);

        Quaternion longArc = AemTransformSemantics.InterpolateEngineRotation(
            Quaternion.Identity,
            AemTransformSemantics.CreateEngineRotation(
                new Vector3(0, 0, MathF.PI * 1.5f)),
            0.5f);
        Assert.AreEqual(0.9238795f, longArc.Z, 0.0001f);
        Assert.AreEqual(0.3826834f, longArc.W, 0.0001f);
    }

    private static byte[] CreateV4Fixture(bool includeAuxiliary)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("V4AEMesh\0"u8);
        writer.Write((byte)(includeAuxiliary ? 0x1F : 0x17));
        writer.Write((ushort)1);
        WriteVector3(writer, Vector3.Zero);
        WriteGeometry(writer, includeAuxiliary);
        WriteStaticTransformGroups(writer);
        writer.Write((short)-1);
        writer.Write((short)0);
        return stream.ToArray();
    }

    private static byte[] CreateV1Fixture()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)4);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((ushort)3);
        writer.Write((ushort)1);
        writer.Write((ushort)4);
        writer.Write((ushort)4);
        foreach (short value in new short[]
        {
            0, 0, 0,
            1, 0, 0,
            0, 1, 0,
            1, 1, 0,
        })
        {
            writer.Write(value);
        }

        foreach (short value in new short[] { 0, 0, 256, 0, 0, 256, 256, 256 })
        {
            writer.Write(value);
        }

        for (int index = 0; index < 4; index++)
        {
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)256);
        }

        writer.Write((byte)1);
        return stream.ToArray();
    }

    private static byte[] CreateV2Fixture()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("V2AEMesh\0"u8);
        writer.Write((byte)0x17);
        WriteLegacyTriangle(writer);
        writer.Write((byte)0);
        return stream.ToArray();
    }

    private static byte[] CreateV3Fixture()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("V3AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)1);
        WriteVector3(writer, new Vector3(2, 3, 4));
        WriteLegacyTriangle(writer);
        WriteVector4(writer, new Vector4(0.5f, 0.5f, 0, 2));
        WriteStaticTransformGroups(writer);
        writer.Write((short)0);
        return stream.ToArray();
    }

    private static void WriteLegacyTriangle(BinaryWriter writer)
    {
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((ushort)3);
        WriteFixed16_16(writer, 0);
        WriteFixed16_16(writer, 0);
        WriteFixed16_16(writer, 0);
        WriteFixed16_16(writer, 1);
        WriteFixed16_16(writer, 0);
        WriteFixed16_16(writer, 0);
        WriteFixed16_16(writer, 0);
        WriteFixed16_16(writer, 1);
        WriteFixed16_16(writer, 0);
        foreach (short value in new short[] { 0, 0, 4096, 0, 0, 4096 })
        {
            writer.Write(value);
        }

        for (int index = 0; index < 3; index++)
        {
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write(short.MaxValue);
        }
    }

    private static void WriteFixed16_16(BinaryWriter writer, float value)
    {
        writer.Write(checked((int)(value * 65536)));
    }

    private static byte[] CreateV5AnimatedFixture()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("V5AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)1);
        WriteVector3(writer, Vector3.Zero);
        WriteGeometry(writer, includeAuxiliary: false);
        WriteStaticTransformGroups(writer);
        writer.Write((short)2);
        WriteScalarCurve(writer, 100, 50);
        writer.Write((short)1);
        WriteScalarCurve(writer, 200, 25);
        for (int index = 1; index < 7; index++)
        {
            writer.Write((ushort)0);
        }

        writer.Write((short)0);
        return stream.ToArray();
    }

    private static byte[] CreateV4TransformAnimatedFixture()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("V4AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)1);
        WriteVector3(writer, Vector3.Zero);
        WriteGeometry(writer, includeAuxiliary: false);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write(0f);
        WriteVector3(writer, Vector3.Zero);
        writer.Write(1000f);
        WriteVector3(writer, new Vector3(10, 0, 0));
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((short)-1);
        writer.Write((short)0);
        return stream.ToArray();
    }

    private static byte[] CreateV4ScalarTransformFixture()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("V4AEMesh\0"u8);
        writer.Write((byte)0x17);
        writer.Write((ushort)1);
        WriteVector3(writer, new Vector3(10, 20, 30));
        WriteGeometry(writer, includeAuxiliary: false);

        writer.Write((ushort)0);
        WriteScalarCurve(writer, 0, 1);
        WriteScalarCurve(writer, 0, 2);
        WriteScalarCurve(writer, 0, 3);

        writer.Write((ushort)0);
        WriteScalarCurve(writer, 0, 0);
        WriteScalarCurve(writer, 0, MathF.PI / 2);
        WriteScalarCurve(writer, 0, 0);

        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(0f);
        WriteVector3(writer, Vector3.One);
        writer.Write((short)-1);
        writer.Write((short)0);
        return stream.ToArray();
    }

    private static void WriteGeometry(BinaryWriter writer, bool includeAuxiliary)
    {
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((ushort)3);
        WriteVector3(writer, Vector3.Zero);
        WriteVector3(writer, Vector3.UnitX);
        WriteVector3(writer, Vector3.UnitY);
        WriteVector2(writer, Vector2.Zero);
        WriteVector2(writer, Vector2.UnitX);
        WriteVector2(writer, Vector2.UnitY);
        WriteVector3(writer, Vector3.UnitZ);
        WriteVector3(writer, Vector3.UnitZ);
        WriteVector3(writer, Vector3.UnitZ);
        if (includeAuxiliary)
        {
            WriteVector4(writer, Vector4.One);
            WriteVector4(writer, Vector4.One);
            WriteVector4(writer, Vector4.One);
        }

        WriteVector4(writer, new Vector4(0.5f, 0.5f, 0, 2));
    }

    private static void WriteStaticTransformGroups(BinaryWriter writer)
    {
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
    }

    private static void WriteScalarCurve(BinaryWriter writer, float time, float value)
    {
        writer.Write((ushort)1);
        writer.Write(time);
        writer.Write(value);
    }

    private static void WriteVector2(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
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
