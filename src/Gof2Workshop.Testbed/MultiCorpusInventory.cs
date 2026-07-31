using System.Security.Cryptography;
using System.Text;
using Gof2Workshop.Core;

namespace Gof2Workshop.Testbed;

internal sealed record CorpusInventorySummary(
    string Profile,
    bool Present,
    int Files,
    long Bytes,
    IReadOnlyDictionary<string, int> Extensions,
    IReadOnlyDictionary<string, int> AeiIdentifiers,
    IReadOnlyDictionary<string, int> AemSignatures,
    int DatabaseCandidates,
    int MissionCandidates,
    IReadOnlyList<string> AnonymizedMissionCandidates);

internal sealed record CorpusPairComparison(
    string LeftProfile,
    string RightProfile,
    int SharedFileNames,
    int ByteIdenticalFiles,
    int StructurallySimilarAei,
    int StructurallySimilarAem);

internal sealed record MultiCorpusInventoryReport(
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<CorpusInventorySummary> Corpora,
    IReadOnlyList<CorpusPairComparison> Comparisons);

internal sealed class MultiCorpusInventory
{
    public MultiCorpusInventoryReport Scan(
        IReadOnlyList<(AssetPlatformProfile Profile, string Root)> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        List<CorpusSnapshot> snapshots = [];
        foreach ((AssetPlatformProfile profile, string root) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(ScanOne(profile, root, cancellationToken));
        }

        List<CorpusPairComparison> comparisons = [];
        for (int left = 0; left < snapshots.Count; left++)
        {
            for (int right = left + 1; right < snapshots.Count; right++)
            {
                comparisons.Add(Compare(snapshots[left], snapshots[right]));
            }
        }

        return new MultiCorpusInventoryReport(
            DateTimeOffset.UtcNow,
            snapshots.Select(value => value.Summary).ToArray(),
            comparisons);
    }

    private static CorpusSnapshot ScanOne(
        AssetPlatformProfile profile,
        string root,
        CancellationToken cancellationToken)
    {
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            return new CorpusSnapshot(
                new CorpusInventorySummary(
                    profile.Id,
                    false,
                    0,
                    0,
                    new SortedDictionary<string, int>(),
                    new SortedDictionary<string, int>(),
                    new SortedDictionary<string, int>(),
                    0,
                    0,
                    []),
                []);
        }

        Dictionary<string, int> extensions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> aeiIdentifiers = new(StringComparer.Ordinal);
        Dictionary<string, int> aemSignatures = new(StringComparer.Ordinal);
        List<InventoryFingerprint> fingerprints = [];
        List<string> missionCandidates = [];
        int databaseCandidates = 0;
        long bytes = 0;
        foreach (string path in Directory.EnumerateFiles(
            fullRoot,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            bytes = checked(bytes + info.Length);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            Increment(extensions, string.IsNullOrEmpty(extension) ? "<none>" : extension);
            string relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
            byte[] header = ReadHeader(path, 32);
            string structure = extension switch
            {
                ".aei" => DescribeAei(header, info.Length, aeiIdentifiers),
                ".aem" => DescribeAem(header, aemSignatures),
                _ => extension,
            };
            fingerprints.Add(new InventoryFingerprint(
                info.Name.ToLowerInvariant(),
                extension,
                ComputeFileHash(path),
                structure));

            string candidateText = relative.ToLowerInvariant();
            if (IsDatabaseCandidate(extension, candidateText))
            {
                databaseCandidates++;
            }

            if (profile.Details.MissionCandidates.Any(candidateText.Contains))
            {
                byte[] candidateId = SHA256.HashData(Encoding.UTF8.GetBytes(candidateText));
                missionCandidates.Add(Convert.ToHexString(candidateId.AsSpan(0, 8)).ToLowerInvariant());
            }
        }

        return new CorpusSnapshot(
            new CorpusInventorySummary(
                profile.Id,
                true,
                fingerprints.Count,
                bytes,
                new SortedDictionary<string, int>(extensions, StringComparer.OrdinalIgnoreCase),
                new SortedDictionary<string, int>(aeiIdentifiers, StringComparer.Ordinal),
                new SortedDictionary<string, int>(aemSignatures, StringComparer.Ordinal),
                databaseCandidates,
                missionCandidates.Count,
                missionCandidates.Take(32).ToArray()),
            fingerprints);
    }

    private static CorpusPairComparison Compare(CorpusSnapshot left, CorpusSnapshot right)
    {
        HashSet<string> rightNames = right.Files.Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> rightHashes = right.Files.Select(file => file.Hash).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, int> rightStructures = right.Files
            .GroupBy(file => $"{file.Extension}:{file.Structure}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        int aeiSimilar = CountStructuralMatches(left.Files, rightStructures, ".aei");
        int aemSimilar = CountStructuralMatches(left.Files, rightStructures, ".aem");
        return new CorpusPairComparison(
            left.Summary.Profile,
            right.Summary.Profile,
            left.Files.Count(file => rightNames.Contains(file.Name)),
            left.Files.Count(file => rightHashes.Contains(file.Hash)),
            aeiSimilar,
            aemSimilar);
    }

    private static int CountStructuralMatches(
        IReadOnlyList<InventoryFingerprint> left,
        Dictionary<string, int> rightStructures,
        string extension)
    {
        Dictionary<string, int> remaining = new(rightStructures, StringComparer.Ordinal);
        int count = 0;
        foreach (InventoryFingerprint file in left.Where(file => file.Extension == extension))
        {
            string key = $"{file.Extension}:{file.Structure}";
            if (remaining.GetValueOrDefault(key) > 0)
            {
                remaining[key]--;
                count++;
            }
        }

        return count;
    }

    private static string DescribeAei(
        ReadOnlySpan<byte> header,
        long length,
        Dictionary<string, int> distribution)
    {
        if (header.Length < 15 || !header[..8].SequenceEqual("AEimage\0"u8))
        {
            Increment(distribution, "unknown-signature");
            return $"unknown:{length}";
        }

        byte id = header[8];
        int width = header[9] | (header[10] << 8);
        int height = header[11] | (header[12] << 8);
        string key = $"0x{id:X2}";
        Increment(distribution, key);
        return $"{key}:{width}x{height}:{length}";
    }

    private static string DescribeAem(
        ReadOnlySpan<byte> header,
        Dictionary<string, int> distribution)
    {
        int terminator = header.IndexOf((byte)0);
        if (terminator < 0 || terminator + 1 >= header.Length)
        {
            Increment(distribution, "unknown-signature");
            return "unknown";
        }

        string signature = Encoding.ASCII.GetString(header[..terminator]);
        string key = $"{signature}:0x{header[terminator + 1]:X2}";
        Increment(distribution, key);
        return key;
    }

    private static bool IsDatabaseCandidate(string extension, string relative)
    {
        return extension is ".json" or ".xml" or ".db" or ".sqlite" or ".txt" or ".csv"
            || relative.Contains("database", StringComparison.Ordinal)
            || relative.Contains("items", StringComparison.Ordinal)
            || relative.Contains("ships", StringComparison.Ordinal)
            || relative.Contains("systems", StringComparison.Ordinal)
            || relative.Contains("stations", StringComparison.Ordinal);
    }

    private static byte[] ReadHeader(string path, int maximum)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] result = new byte[Math.Min(maximum, checked((int)Math.Min(stream.Length, maximum)))];
        stream.ReadExactly(result);
        return result;
    }

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Increment(Dictionary<string, int> values, string key)
    {
        values[key] = values.GetValueOrDefault(key) + 1;
    }

    private sealed record InventoryFingerprint(
        string Name,
        string Extension,
        string Hash,
        string Structure);

    private sealed record CorpusSnapshot(
        CorpusInventorySummary Summary,
        IReadOnlyList<InventoryFingerprint> Files);
}
