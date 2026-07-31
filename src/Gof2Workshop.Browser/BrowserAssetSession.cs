using System.Collections.ObjectModel;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Browser;

public enum BrowserAssetKind
{
    Aei,
    Aem,
    Companion,
}

public sealed record BrowserAssetItem(
    string Name,
    BrowserAssetKind Kind,
    byte[] Bytes,
    string Summary,
    AeiFile? Aei,
    RgbaImage? Texture,
    AemFile? Aem,
    SceneDocument? Scene)
{
    public string SizeText => Bytes.Length < 1024 * 1024
        ? $"{Bytes.Length / 1024d:F1} KiB"
        : $"{Bytes.Length / 1048576d:F1} MiB";
}

public sealed class BrowserAssetSession
{
    public const int MaximumFileBytes = 256 * 1024 * 1024;
    public const long MaximumCollectionBytes = 512L * 1024 * 1024;

    private long retainedBytes;

    public ObservableCollection<BrowserAssetItem> Assets { get; } = [];

    public async Task<BrowserAssetItem> AddAsync(
        string name,
        Stream input,
        AssetPlatformProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(input);

        byte[] bytes = await ReadBoundedAsync(input, cancellationToken).ConfigureAwait(false);
        if (retainedBytes + bytes.Length > MaximumCollectionBytes)
        {
            throw new InvalidDataException(
                $"The inspection collection is limited to {MaximumCollectionBytes / 1048576} MiB.");
        }

        BrowserAssetItem item = Parse(name, bytes, profile, cancellationToken);
        retainedBytes += bytes.Length;
        Assets.Add(item);
        return item;
    }

    public void Clear()
    {
        Assets.Clear();
        retainedBytes = 0;
    }

    public static RgbaImage RenderScene(BrowserAssetItem item, CancellationToken cancellationToken = default)
    {
        return RenderScene(item, texture: null, cancellationToken);
    }

    public static RgbaImage RenderScene(
        BrowserAssetItem item,
        RgbaImage? texture,
        CancellationToken cancellationToken = default)
    {
        if (item.Scene is null)
        {
            throw new InvalidOperationException("The selected asset does not contain a scene.");
        }

        IReadOnlyDictionary<int, RgbaImage>? textures = texture is null
            ? null
            : item.Scene.Primitives
                .Select((_, index) => index)
                .ToDictionary(index => index, _ => texture);
        return new ScenePreviewRenderer().Render(
            item.Scene,
            new ScenePreviewOptions(
                Width: 960,
                Height: 640,
                Wireframe: true,
                ShowNormals: false,
                ShowPivots: true,
                ShowBoundingSpheres: true,
                Textures: textures),
            cancellationToken).Image;
    }

    public BrowserAssetItem? ResolveTexture(BrowserAssetItem model)
    {
        if (model.Kind != BrowserAssetKind.Aem)
        {
            return null;
        }

        string stem = NormalizeStem(Path.GetFileNameWithoutExtension(model.Name));
        return Assets
            .Where(asset => asset.Kind == BrowserAssetKind.Aei && asset.Texture is not null)
            .Select(asset => new
            {
                Asset = asset,
                Stem = NormalizeStem(Path.GetFileNameWithoutExtension(asset.Name)),
            })
            .OrderByDescending(candidate => string.Equals(candidate.Stem, stem, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate =>
                candidate.Stem.Contains(stem, StringComparison.OrdinalIgnoreCase) ||
                stem.Contains(candidate.Stem, StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault(candidate =>
                string.Equals(NormalizeStem(Path.GetFileNameWithoutExtension(candidate.Name)), stem, StringComparison.OrdinalIgnoreCase) ||
                NormalizeStem(Path.GetFileNameWithoutExtension(candidate.Name)).Contains(stem, StringComparison.OrdinalIgnoreCase) ||
                stem.Contains(NormalizeStem(Path.GetFileNameWithoutExtension(candidate.Name)), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeStem(string value)
    {
        string result = value.ToLowerInvariant();
        foreach (string suffix in new[] { "_diffuse", "-diffuse", "_lod_0", "_lod_1", "_lod_2" })
        {
            result = result.Replace(suffix, string.Empty, StringComparison.Ordinal);
        }

        return result;
    }

    private static BrowserAssetItem Parse(
        string name,
        byte[] bytes,
        AssetPlatformProfile profile,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(name);
        using MemoryStream stream = new(bytes, writable: false);
        if (extension.Equals(".aei", StringComparison.OrdinalIgnoreCase))
        {
            AeiFile parsed = new AeiParser().Parse(
                stream,
                name,
                new AeiParserOptions(profile),
                cancellationToken);
            AeiTextureDecoder decoder = new();
            RgbaImage? texture = decoder.CanDecode(parsed.Format.Format)
                ? decoder.DecodeAtlas(parsed, cancellationToken)
                : null;
            string summary = texture is null
                ? $"AEI {parsed.Width} x {parsed.Height} - {parsed.Format.DisplayName} (recognized, decoder unavailable)"
                : $"AEI {parsed.Width} x {parsed.Height} - {parsed.Format.DisplayName}; {parsed.Regions.Count} region(s)";
            return new BrowserAssetItem(
                name,
                BrowserAssetKind.Aei,
                bytes,
                summary,
                parsed,
                texture,
                null,
                null);
        }

        if (extension.Equals(".aem", StringComparison.OrdinalIgnoreCase))
        {
            AemFile parsed = new AemParser().Parse(
                stream,
                name,
                new AemParserOptions(profile),
                cancellationToken);
            SceneDocument scene = new AemSceneConverter().Convert(parsed);
            long triangles = scene.Primitives.Sum(primitive => primitive.Indices.Length / 3L);
            return new BrowserAssetItem(
                name,
                BrowserAssetKind.Aem,
                bytes,
                $"AEM v{(int)parsed.Version}; {scene.Primitives.Count} submesh(es); {triangles:N0} triangles",
                null,
                null,
                parsed,
                scene);
        }

        return new BrowserAssetItem(
            name,
            BrowserAssetKind.Companion,
            bytes,
            $"Companion file ({extension.TrimStart('.').ToUpperInvariant()})",
            null,
            null,
            null,
            null);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        if (input.CanSeek && input.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"Browser files are limited to {MaximumFileBytes / 1048576} MiB each.");
        }

        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumFileBytes)
            {
                throw new InvalidDataException(
                    $"Browser files are limited to {MaximumFileBytes / 1048576} MiB each.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
