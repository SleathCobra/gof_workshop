using Gof2Workshop.Binary;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.IntegrationTests;

[TestClass]
public sealed class LocalCorpusTests
{
    [TestMethod]
    [TestCategory("LocalCorpus")]
    public async Task WorkbenchIndexScansLocalCorpusWithoutFullDecodes()
    {
        string dataRoot = GetDataRootOrSkip();

        AssetIndexResult result = await new AssetIndexService().ScanAsync(
            dataRoot,
            AssetOwnership.Game,
            Core.ProfileCatalog.Pc1X);

        Assert.AreEqual(2_016, result.Assets.Count);
        Assert.AreEqual(1_228, result.Assets.Count(asset => asset.Kind == Core.AssetKind.Aei));
        Assert.AreEqual(752, result.Assets.Count(asset => asset.Kind == Core.AssetKind.Aem));
        Assert.AreEqual(11, result.Assets.Count(asset => asset.Kind == Core.AssetKind.Language));
        Assert.AreEqual(25, result.Assets.Count(asset => asset.Kind == Core.AssetKind.GameData));
        Assert.AreEqual(
            0,
            result.Assets.Count(asset => asset.Support == AssetSupport.RecognizedUnsupported));
        Assert.AreEqual(
            result.Assets.Count,
            result.Assets.Count(asset => asset.Support == AssetSupport.Supported));
    }

    [TestMethod]
    [TestCategory("LocalCorpus")]
    public void SmokeParsesAndViewsLocalAssetsWhenPresent()
    {
        string dataRoot = GetDataRootOrSkip();
        string[] aeiPaths = Directory.EnumerateFiles(dataRoot, "*.aei", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        string[] aemPaths = Directory.EnumerateFiles(dataRoot, "*.aem", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        Assert.IsNotEmpty(aeiPaths);
        Assert.IsNotEmpty(aemPaths);
        AeiParser aeiParser = new();
        AeiTextureDecoder decoder = new();
        int decoded = 0;
        foreach (string path in aeiPaths)
        {
            AeiFile file = aeiParser.Parse(path);
            if (decoder.CanDecode(file.Format.Format))
            {
                _ = decoder.DecodeAtlas(file).GetPixel(0, 0);
                decoded++;
            }
        }

        AemParser aemParser = new();
        AemSceneConverter converter = new();
        int parsedAem = 0;
        foreach (string path in aemPaths)
        {
            try
            {
                SceneDocument scene = converter.Convert(aemParser.Parse(path));
                Assert.IsNotEmpty(scene.Primitives);
                parsedAem++;
            }
            catch (FormatParseException exception) when (exception.FailureKind == FormatFailureKind.Unsupported)
            {
                // Recognized older variants are intentionally skipped by this v4/v5 smoke test.
            }
        }

        Assert.IsGreaterThan(0, decoded);
        Assert.IsGreaterThan(0, parsedAem);
    }

    [TestMethod]
    [TestCategory("LocalCorpus")]
    public void RepresentativeLocalModelRendersWithoutWritingDataFolder()
    {
        string dataRoot = GetDataRootOrSkip();
        AemParser parser = new();
        string? supportedPath = Directory.EnumerateFiles(dataRoot, "*.aem", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
            {
                using FileStream stream = File.OpenRead(path);
                Span<byte> signature = stackalloc byte[9];
                return stream.Read(signature) == signature.Length
                    && (signature.SequenceEqual("V4AEMesh\0"u8)
                        || signature.SequenceEqual("V5AEMesh\0"u8));
            });
        Assert.IsNotNull(supportedPath);

        SceneDocument scene = new AemSceneConverter().Convert(parser.Parse(supportedPath));
        ScenePreviewResult preview = new ScenePreviewRenderer().Render(
            scene,
            new ScenePreviewOptions(256, 256, MaximumTriangles: 25_000));

        Assert.IsGreaterThan(0, preview.SourceTriangleCount);
        Assert.IsGreaterThan(0, preview.RenderedTriangleCount);
        Assert.AreEqual(256, preview.Image.Width);
    }

    [TestMethod]
    [TestCategory("LocalCorpus")]
    public void AemStructuralWriterRoundTripsLocalCorpusInMemory()
    {
        string dataRoot = GetDataRootOrSkip();
        string[] paths = Directory.EnumerateFiles(dataRoot, "*.aem", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.IsNotEmpty(paths);
        AemParser parser = new();
        AemWriter writer = new();

        foreach (string path in paths)
        {
            byte[] original = File.ReadAllBytes(path);
            using MemoryStream input = new(original, writable: false);
            AemFile file = parser.Parse(input, path);
            using MemoryStream output = new(original.Length);

            writer.Write(file, output);

            CollectionAssert.AreEqual(
                original,
                output.ToArray(),
                $"Structural writer changed the immutable representation of " +
                $"{Path.GetFileName(path)}.");
        }
    }

    private static string GetDataRootOrSkip()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalaxyOnFire2Workshop.sln")))
        {
            directory = directory.Parent;
        }

        string? dataRoot = directory is null ? null : Path.Combine(directory.FullName, "data");
        if (dataRoot is null || !Directory.Exists(dataRoot))
        {
            Assert.Inconclusive(
                "Local proprietary corpus is absent. Place it under ignored /data to run LocalCorpus tests.");
            return string.Empty;
        }

        return dataRoot;
    }
}
