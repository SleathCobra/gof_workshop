using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;
using System.Security.Cryptography;
using System.Text;

namespace Gof2Workshop.Testbed;

internal sealed record CorpusKindReport(
    int Discovered,
    int Parsed,
    int DecodedOrSceneConverted,
    int Unsupported,
    int Corrupt,
    IReadOnlyDictionary<string, int> Distribution,
    IReadOnlyDictionary<string, int> FailureGroups,
    IReadOnlyDictionary<string, int> Classifications,
    CorpusWriteReport WriteValidation,
    IReadOnlyList<CorpusFailureSample> FailureSamples);

internal sealed record CorpusWriteReport(
    int Attempted,
    int ByteIdentical,
    int Different,
    int Failed);

internal sealed record CorpusFailureSample(
    string AssetId,
    string Signature,
    string Classification,
    long? Offset,
    string? Field,
    string Reason);

internal sealed record CorpusValidationReport(
    string Profile,
    DateTimeOffset ValidatedAtUtc,
    CorpusKindReport Aei,
    CorpusKindReport Aem);

internal sealed class CorpusValidator
{
    private readonly CliLogger logger;

    public CorpusValidator(CliLogger logger)
    {
        this.logger = logger;
    }

    public CorpusValidationReport Validate(
        string root,
        AssetPlatformProfile profile,
        bool decodeTextures,
        bool validateWriters,
        int? limitPerKind,
        CancellationToken cancellationToken = default)
    {
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Asset directory does not exist: {root}");
        }

        string[] aeiFiles = Enumerate(fullRoot, ".aei", limitPerKind);
        string[] aemFiles = Enumerate(fullRoot, ".aem", limitPerKind);
        AeiParser aeiParser = new();
        AeiTextureDecoder decoder = new();
        AeiWriter aeiWriter = new();
        AemParser aemParser = new();
        AemSceneConverter converter = new();
        AemWriter aemWriter = new();

        MutableKindReport aei = new(aeiFiles.Length);
        foreach (string path in aeiFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AeiFile file = aeiParser.Parse(
                    path,
                    new AeiParserOptions(profile),
                    cancellationToken);
                aei.Parsed++;
                aei.IncrementDistribution(file.Format.DisplayName);
                if (decodeTextures && decoder.CanDecode(file.Format.Format))
                {
                    RgbaImage image = decoder.DecodeAtlas(file, cancellationToken);
                    _ = image.GetPixel(0, 0);
                    aei.DecodedOrConverted++;
                }
                else if (decodeTextures)
                {
                    aei.Unsupported++;
                }

                if (validateWriters)
                {
                    ValidateAeiWriter(path, file, aeiWriter, aei);
                }
            }
            catch (FormatParseException exception)
            {
                aei.RecordFailure(fullRoot, path, exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                aei.Corrupt++;
                aei.IncrementFailure(exception.GetType().Name);
                aei.RecordFailure(fullRoot, path, "Unexpected parser failure", null, null, exception.Message);
            }
        }

        MutableKindReport aem = new(aemFiles.Length);
        foreach (string path in aemFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AemFile file = aemParser.Parse(
                    path,
                    new AemParserOptions(profile),
                    cancellationToken);
                aem.Parsed++;
                aem.IncrementDistribution($"v{(int)file.Version} flags=0x{(byte)file.Flags:X2}");
                SceneDocument scene = converter.Convert(file);
                _ = scene.Bounds;
                aem.DecodedOrConverted++;
                if (validateWriters)
                {
                    ValidateAemWriter(path, file, aemWriter, aem, cancellationToken);
                }
            }
            catch (FormatParseException exception)
            {
                aem.RecordFailure(fullRoot, path, exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                aem.Corrupt++;
                aem.IncrementFailure(exception.GetType().Name);
                aem.RecordFailure(fullRoot, path, "Unexpected parser failure", null, null, exception.Message);
            }
        }

        logger.Info(
            "corpus.validated",
            "Local corpus validation completed.",
            ("aeiParsed", aei.Parsed),
            ("aeiDecoded", aei.DecodedOrConverted),
            ("aemParsed", aem.Parsed),
            ("aemScenes", aem.DecodedOrConverted));
        return new CorpusValidationReport(
            profile.Id,
            DateTimeOffset.UtcNow,
            aei.ToImmutable(),
            aem.ToImmutable());
    }

    private static void ValidateAeiWriter(
        string path,
        AeiFile file,
        AeiWriter writer,
        MutableKindReport report)
    {
        report.WriteAttempted++;
        try
        {
            using MemoryStream output = new();
            writer.Write(file, output);
            bool identical = File.ReadAllBytes(path).AsSpan().SequenceEqual(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            if (identical)
            {
                report.WriteByteIdentical++;
            }
            else
            {
                report.WriteDifferent++;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException)
        {
            report.WriteFailed++;
            report.IncrementFailure($"Writer: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ValidateAemWriter(
        string path,
        AemFile file,
        AemWriter writer,
        MutableKindReport report,
        CancellationToken cancellationToken)
    {
        report.WriteAttempted++;
        try
        {
            using MemoryStream output = new();
            writer.Write(file, output, cancellationToken);
            bool identical = File.ReadAllBytes(path).AsSpan().SequenceEqual(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            if (identical)
            {
                report.WriteByteIdentical++;
            }
            else
            {
                report.WriteDifferent++;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException)
        {
            report.WriteFailed++;
            report.IncrementFailure($"Writer: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string[] Enumerate(string root, string extension, int? limit)
    {
        IEnumerable<string> files = Directory
            .EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Where(path => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        if (limit is not null)
        {
            files = files.Take(limit.Value);
        }

        return files.ToArray();
    }

    private sealed class MutableKindReport
    {
        private readonly Dictionary<string, int> distribution = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);
        private readonly List<CorpusFailureSample> failureSamples = [];

        public MutableKindReport(int discovered)
        {
            Discovered = discovered;
        }

        public int Discovered { get; }

        public int Parsed { get; set; }

        public int DecodedOrConverted { get; set; }

        public int Unsupported { get; set; }

        public int Corrupt { get; set; }

        public int WriteAttempted { get; set; }

        public int WriteByteIdentical { get; set; }

        public int WriteDifferent { get; set; }

        public int WriteFailed { get; set; }

        public void IncrementDistribution(string key)
        {
            distribution[key] = distribution.GetValueOrDefault(key) + 1;
        }

        public void IncrementFailure(string key)
        {
            failures[key] = failures.GetValueOrDefault(key) + 1;
        }

        public void RecordFailure(string root, string path, FormatParseException exception)
        {
            if (exception.FailureKind == FormatFailureKind.Unsupported)
            {
                Unsupported++;
            }
            else
            {
                Corrupt++;
            }

            IncrementFailure($"{exception.FailureKind}: {exception.Field}: {exception.Reason}");
            RecordFailure(
                root,
                path,
                exception.FailureKind == FormatFailureKind.Unsupported
                    ? "Recognized but unsupported variant"
                    : "Corrupt, truncated, or structurally ambiguous",
                exception.Offset,
                exception.Field,
                exception.Reason);
        }

        public void RecordFailure(
            string root,
            string path,
            string classification,
            long? offset,
            string? field,
            string reason)
        {
            const int maximumSamples = 32;
            if (failureSamples.Count >= maximumSamples)
            {
                return;
            }

            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            byte[] idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant()));
            failureSamples.Add(new CorpusFailureSample(
                Convert.ToHexString(idBytes.AsSpan(0, 8)).ToLowerInvariant(),
                ReadSignature(path),
                classification,
                offset,
                field,
                reason));
        }

        public CorpusKindReport ToImmutable()
        {
            Dictionary<string, int> classifications = new(StringComparer.Ordinal)
            {
                ["Fully parsed and decoded/converted"] = DecodedOrConverted,
                ["Fully parsed but decoder unavailable"] = Math.Max(0, Parsed - DecodedOrConverted),
                ["Recognized but unsupported variant"] = Unsupported,
                ["Corrupt, truncated, or structurally ambiguous"] = Corrupt,
            };
            return new CorpusKindReport(
                Discovered,
                Parsed,
                DecodedOrConverted,
                Unsupported,
                Corrupt,
                new SortedDictionary<string, int>(distribution, StringComparer.Ordinal),
                new SortedDictionary<string, int>(failures, StringComparer.Ordinal),
                classifications,
                new CorpusWriteReport(WriteAttempted, WriteByteIdentical, WriteDifferent, WriteFailed),
                failureSamples.ToArray());
        }

        private static string ReadSignature(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[16];
                int count = stream.Read(header);
                return Convert.ToHexString(header[..Math.Min(count, 12)]);
            }
            catch (IOException)
            {
                return "unreadable";
            }
        }
    }
}
