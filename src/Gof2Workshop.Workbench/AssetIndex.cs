using System.Collections.ObjectModel;
using System.Text;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;

namespace Gof2Workshop.Workbench;

public enum AssetOwnership
{
    Game,
    Mod,
}

public enum AssetSupport
{
    Supported,
    RecognizedUnsupported,
    Unknown,
    Unreadable,
}

public sealed record IndexedAsset(
    string FullPath,
    string RelativePath,
    string FileName,
    AssetKind Kind,
    AssetOwnership Ownership,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string Classification,
    string? Version,
    AssetSupport Support,
    bool PreviewSupported,
    string? Warning)
{
    public string StableKey => Path.GetFullPath(FullPath);
}

public sealed record AssetIndexProgress(
    int FilesVisited,
    int AssetsFound,
    string? CurrentRelativePath);

public sealed record AssetIndexDelta(
    int Added,
    int Removed,
    int Changed);

public sealed record AssetIndexResult(
    string Root,
    DateTimeOffset ScannedAtUtc,
    TimeSpan Duration,
    IReadOnlyList<IndexedAsset> Assets,
    AssetIndexDelta Delta,
    IReadOnlyList<ProblemEntry> Problems);

public interface IAssetIndex
{
    public Task<AssetIndexResult> ScanAsync(
        string root,
        AssetOwnership ownership,
        AssetPlatformProfile profile,
        IProgress<AssetIndexProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class AssetIndexService : IAssetIndex
{
    private readonly object sync = new();
    private readonly Dictionary<AssetOwnership, Dictionary<string, IndexedAsset>> previous = [];

    public Task<AssetIndexResult> ScanAsync(
        string root,
        AssetOwnership ownership,
        AssetPlatformProfile profile,
        IProgress<AssetIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(profile);
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Asset directory does not exist: {root}");
        }

        return Task.Run(
            () => ScanCore(fullRoot, ownership, profile, progress, cancellationToken),
            cancellationToken);
    }

    public IndexedAsset ProbeFile(
        string path,
        AssetOwnership ownership,
        AssetPlatformProfile profile,
        string? relativePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        string fullPath = Path.GetFullPath(path);
        FileInfo info = new(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Inspection source file does not exist.", fullPath);
        }

        AssetKind kind = ParseKind(info.Extension)
            ?? throw new NotSupportedException(
                $"Quick Inspect cannot open '{info.Extension}' as an AEI/AEM document yet.");
        return Probe(
            fullPath,
            relativePath ?? info.Name,
            info,
            kind,
            ownership,
            profile);
    }

    private AssetIndexResult ScanCore(
        string fullRoot,
        AssetOwnership ownership,
        AssetPlatformProfile profile,
        IProgress<AssetIndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        List<IndexedAsset> assets = [];
        List<ProblemEntry> problems = [];
        int visited = 0;

        foreach (string path in Directory.EnumerateFiles(fullRoot, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited++;
            AssetKind? kind = ParseKind(Path.GetExtension(path));
            if (kind is null)
            {
                if ((visited & 127) == 0)
                {
                    progress?.Report(new AssetIndexProgress(visited, assets.Count, null));
                }

                continue;
            }

            string relativePath = Path.GetRelativePath(fullRoot, path);
            try
            {
                FileInfo info = new(path);
                IndexedAsset asset = Probe(path, relativePath, info, kind.Value, ownership, profile);
                assets.Add(asset);
                if (asset.Warning is not null)
                {
                    problems.Add(ProblemEntry.Warning(
                        asset,
                        asset.Warning,
                        asset.Support == AssetSupport.RecognizedUnsupported
                            ? "Metadata remains available; choose a supported decoder or profile."
                            : "Inspect the asset details."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                IndexedAsset asset = new(
                    path,
                    relativePath,
                    Path.GetFileName(path),
                    kind.Value,
                    ownership,
                    0,
                    DateTimeOffset.MinValue,
                    "Unreadable",
                    null,
                    AssetSupport.Unreadable,
                    false,
                    exception.Message);
                assets.Add(asset);
                problems.Add(ProblemEntry.Error(asset, exception.Message, "Check file permissions."));
            }

            if ((assets.Count & 31) == 0)
            {
                progress?.Report(new AssetIndexProgress(visited, assets.Count, relativePath));
            }
        }

        assets.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        Dictionary<string, IndexedAsset> current = assets.ToDictionary(
            asset => asset.StableKey,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IndexedAsset> before;
        lock (sync)
        {
            before = previous.TryGetValue(ownership, out Dictionary<string, IndexedAsset>? value)
                ? value
                : new Dictionary<string, IndexedAsset>(StringComparer.OrdinalIgnoreCase);
            previous[ownership] = current;
        }

        int added = current.Keys.Count(key => !before.ContainsKey(key));
        int removed = before.Keys.Count(key => !current.ContainsKey(key));
        int changed = current.Count(pair =>
            before.TryGetValue(pair.Key, out IndexedAsset? old) &&
            (old.Size != pair.Value.Size || old.LastWriteTimeUtc != pair.Value.LastWriteTimeUtc));
        progress?.Report(new AssetIndexProgress(visited, assets.Count, null));
        return new AssetIndexResult(
            fullRoot,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow - started,
            new ReadOnlyCollection<IndexedAsset>(assets),
            new AssetIndexDelta(added, removed, changed),
            new ReadOnlyCollection<ProblemEntry>(problems));
    }

    private static IndexedAsset Probe(
        string path,
        string relativePath,
        FileInfo info,
        AssetKind kind,
        AssetOwnership ownership,
        AssetPlatformProfile profile)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        ProbeResult probe = kind switch
        {
            AssetKind.Aei => ProbeAei(header[..read], profile),
            AssetKind.Aem => ProbeAem(header[..read], profile),
            AssetKind.Language => ProbeLanguage(path, header[..read]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown asset kind."),
        };
        return new IndexedAsset(
            path,
            relativePath,
            info.Name,
            kind,
            ownership,
            info.Length,
            info.LastWriteTimeUtc,
            probe.Classification,
            probe.Version,
            probe.Support,
            probe.PreviewSupported,
            probe.Warning);
    }

    private static ProbeResult ProbeAei(
        ReadOnlySpan<byte> header,
        AssetPlatformProfile profile)
    {
        if (header.Length < 9)
        {
            return new ProbeResult(
                "Truncated AEI header",
                null,
                AssetSupport.Unknown,
                false,
                "The AEI header is truncated.");
        }

        if (!header[..8].SequenceEqual("AEimage\0"u8))
        {
            return new ProbeResult(
                "Unknown AEI signature",
                null,
                AssetSupport.Unknown,
                false,
                "The file extension is AEI but its signature is unknown.");
        }

        AeiFormatDescriptor descriptor = AeiFormatDescriptor.Identify(header[8]);
        bool decodable = descriptor.Format is
            AeiCompressionFormat.UncompressedUi or
            AeiCompressionFormat.Uncompressed or
            AeiCompressionFormat.UncompressedCubeMapPc or
            AeiCompressionFormat.UncompressedCubeMap or
            AeiCompressionFormat.Dxt1 or
            AeiCompressionFormat.Dxt3 or
            AeiCompressionFormat.Dxt5 or
            AeiCompressionFormat.Pvrtc2Rgba or
            AeiCompressionFormat.Pvrtc4Rgba or
            AeiCompressionFormat.Atc or
            AeiCompressionFormat.Etc1 or
            AeiCompressionFormat.Etc2;
        AssetSupport support = descriptor.IsRecognized
            ? decodable ? AssetSupport.Supported : AssetSupport.RecognizedUnsupported
            : AssetSupport.Unknown;
        string? warning = support switch
        {
            AssetSupport.RecognizedUnsupported =>
                $"{descriptor.DisplayName} is recognized, but no pixel decoder is available.",
            AssetSupport.Unknown => $"AEI format 0x{descriptor.RawId:X2} is unknown.",
            _ when !profile.ExpectedAeiFormats.Contains(descriptor.RawId) =>
                $"{descriptor.DisplayName} is outside the expected {profile.DisplayName} profile.",
            _ => null,
        };
        return new ProbeResult(
            descriptor.DisplayName,
            $"0x{descriptor.RawId:X2}",
            support,
            decodable,
            warning);
    }

    private static ProbeResult ProbeAem(
        ReadOnlySpan<byte> header,
        AssetPlatformProfile profile)
    {
        int terminator = header.IndexOf((byte)0);
        if (terminator < 0 || terminator + 1 >= header.Length)
        {
            return new ProbeResult(
                "Truncated or unknown AEM header",
                null,
                AssetSupport.Unknown,
                false,
                "The AEM signature is missing or truncated.");
        }

        string signature = Encoding.ASCII.GetString(header[..terminator]);
        int? version = signature switch
        {
            "AEMesh" => 1,
            "V2AEMesh" => 2,
            "V3AEMesh" => 3,
            "V4AEMesh" => 4,
            "V5AEMesh" => 5,
            _ => null,
        };
        byte flags = header[terminator + 1];
        AssetSupport support = version is null
            ? AssetSupport.Unknown
            : version is >= 1 and <= 5
                ? AssetSupport.Supported
                : AssetSupport.RecognizedUnsupported;
        string? warning = support switch
        {
            AssetSupport.RecognizedUnsupported =>
                $"AEM v{version} is recognized, but its geometry layout is not supported.",
            AssetSupport.Unknown => $"AEM signature '{signature}' is unknown.",
            _ when version is not null && !profile.SupportedAemVersions.Contains(version.Value) =>
                $"AEM v{version} is outside the expected {profile.DisplayName} profile.",
            _ => null,
        };
        return new ProbeResult(
            $"{signature} flags=0x{flags:X2}",
            version?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            support,
            support == AssetSupport.Supported,
            warning);
    }

    private static AssetKind? ParseKind(string extension)
    {
        return extension.Equals(".aei", StringComparison.OrdinalIgnoreCase)
            ? AssetKind.Aei
            : extension.Equals(".aem", StringComparison.OrdinalIgnoreCase)
                ? AssetKind.Aem
                : extension.Equals(".lang", StringComparison.OrdinalIgnoreCase)
                    ? AssetKind.Language
                    : null;
    }

    private static ProbeResult ProbeLanguage(string path, ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
        {
            return new ProbeResult(
                "Truncated language table",
                "BE-length-UTF8",
                AssetSupport.Unknown,
                false,
                "The first big-endian string-length prefix is truncated.");
        }

        ushort firstLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header);
        if (firstLength > 0 && firstLength + 2 > new FileInfo(path).Length)
        {
            return new ProbeResult(
                "Invalid language table",
                "BE-length-UTF8",
                AssetSupport.Unknown,
                false,
                "The first string exceeds the file bounds.");
        }

        return new ProbeResult(
            "Big-endian UTF-8 language table",
            "BE-length-UTF8",
            AssetSupport.Supported,
            true,
            null);
    }

    private sealed record ProbeResult(
        string Classification,
        string? Version,
        AssetSupport Support,
        bool PreviewSupported,
        string? Warning);
}

public sealed record AssetSearchQuery(
    string Text,
    AssetKind? Kind = null,
    AssetSupport? Support = null,
    string? VersionOrFormat = null,
    int MaximumResults = 5000);

public static class AssetSearchService
{
    public static IReadOnlyList<IndexedAsset> Search(
        IEnumerable<IndexedAsset> assets,
        AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.MaximumResults);
        string text = query.Text?.Trim() ?? string.Empty;
        return assets
            .Where(asset =>
                text.Length == 0 ||
                asset.FileName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                asset.RelativePath.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(asset => query.Kind is null || asset.Kind == query.Kind)
            .Where(asset => query.Support is null || asset.Support == query.Support)
            .Where(asset =>
                string.IsNullOrWhiteSpace(query.VersionOrFormat) ||
                string.Equals(asset.Version, query.VersionOrFormat, StringComparison.OrdinalIgnoreCase) ||
                asset.Classification.Contains(
                    query.VersionOrFormat,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(query.MaximumResults)
            .ToArray();
    }
}
