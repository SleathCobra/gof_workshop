using System.Text;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;

namespace Gof2Workshop.Testbed;

internal sealed class AssetScanner
{
    public AssetInventory Scan(
        string root,
        AssetPlatformProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(profile);
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Asset directory does not exist: {root}");
        }

        EnumerationOptions enumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        List<AssetInventoryEntry> entries = [];
        foreach (string path in Directory.EnumerateFiles(fullRoot, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path);
            AssetKind? kind = extension.Equals(".aei", StringComparison.OrdinalIgnoreCase)
                ? AssetKind.Aei
                : extension.Equals(".aem", StringComparison.OrdinalIgnoreCase)
                    ? AssetKind.Aem
                    : null;
            if (kind is null)
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(fullRoot, path);
            FileInfo file = new(path);
            try
            {
                (string classification, string? version) = Probe(path, kind.Value);
                entries.Add(new AssetInventoryEntry(
                    relativePath,
                    file.Name,
                    kind.Value,
                    file.Length,
                    classification,
                    version,
                    null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entries.Add(new AssetInventoryEntry(
                    relativePath,
                    file.Name,
                    kind.Value,
                    file.Exists ? file.Length : 0,
                    "Unreadable",
                    null,
                    exception.Message));
            }
        }

        Dictionary<string, int> duplicates = entries
            .GroupBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

        return new AssetInventory(
            profile.Id,
            DateTimeOffset.UtcNow,
            entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            duplicates);
    }

    private static (string Classification, string? Version) Probe(string path, AssetKind kind)
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
        return kind == AssetKind.Aei
            ? ProbeAei(header[..read])
            : ProbeAem(header[..read]);
    }

    private static (string Classification, string? Version) ProbeAei(ReadOnlySpan<byte> header)
    {
        if (header.Length < 9)
        {
            return ("Truncated AEI header", null);
        }

        if (!header[..8].SequenceEqual("AEimage\0"u8))
        {
            return ("Unknown AEI signature", null);
        }

        AeiFormatDescriptor format = AeiFormatDescriptor.Identify(header[8]);
        return (format.DisplayName, $"0x{format.RawId:X2}");
    }

    private static (string Classification, string? Version) ProbeAem(ReadOnlySpan<byte> header)
    {
        int terminator = header.IndexOf((byte)0);
        if (terminator < 0 || terminator + 1 >= header.Length)
        {
            return ("Truncated or unknown AEM header", null);
        }

        string signature = Encoding.ASCII.GetString(header[..terminator]);
        string? version = signature switch
        {
            "AEMesh" => "1",
            "V2AEMesh" => "2",
            "V3AEMesh" => "3",
            "V4AEMesh" => "4",
            "V5AEMesh" => "5",
            _ => null,
        };
        byte flags = header[terminator + 1];
        return ($"{signature} flags=0x{flags:X2}", version);
    }
}
