using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Testbed;

internal sealed record CorpusKindReport(
    int Discovered,
    int Parsed,
    int DecodedOrSceneConverted,
    int Unsupported,
    int Corrupt,
    IReadOnlyDictionary<string, int> Distribution,
    IReadOnlyDictionary<string, int> FailureGroups);

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
        AemParser aemParser = new();
        AemSceneConverter converter = new();

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
            }
            catch (FormatParseException exception)
            {
                aei.RecordFailure(exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                aei.Corrupt++;
                aei.IncrementFailure(exception.GetType().Name);
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
            }
            catch (FormatParseException exception)
            {
                aem.RecordFailure(exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                aem.Corrupt++;
                aem.IncrementFailure(exception.GetType().Name);
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

        public MutableKindReport(int discovered)
        {
            Discovered = discovered;
        }

        public int Discovered { get; }

        public int Parsed { get; set; }

        public int DecodedOrConverted { get; set; }

        public int Unsupported { get; set; }

        public int Corrupt { get; set; }

        public void IncrementDistribution(string key)
        {
            distribution[key] = distribution.GetValueOrDefault(key) + 1;
        }

        public void IncrementFailure(string key)
        {
            failures[key] = failures.GetValueOrDefault(key) + 1;
        }

        public void RecordFailure(FormatParseException exception)
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
        }

        public CorpusKindReport ToImmutable()
        {
            return new CorpusKindReport(
                Discovered,
                Parsed,
                DecodedOrConverted,
                Unsupported,
                Corrupt,
                new SortedDictionary<string, int>(distribution, StringComparer.Ordinal),
                new SortedDictionary<string, int>(failures, StringComparer.Ordinal));
        }
    }
}
