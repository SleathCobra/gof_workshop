using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Testbed;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };

    public static int Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        CliLogger logger = new();
        try
        {
            CliArguments commandLine = CliArguments.Parse(args);
            return Run(commandLine, logger, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            logger.Warning("operation.cancelled", "Operation cancelled.");
            return 130;
        }
        catch (FormatParseException exception)
        {
            logger.Error(
                "format.failure",
                exception.Reason,
                ("kind", exception.FailureKind),
                ("offset", $"0x{exception.Offset:X}"),
                ("field", exception.Field));
            return exception.FailureKind == FormatFailureKind.Unsupported ? 2 : 3;
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            logger.Error("operation.failed", exception.Message);
            return 1;
        }
    }

    private static int Run(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        return args.Command.ToLowerInvariant() switch
        {
            "help" or "--help" or "-h" => ShowHelp(),
            "scan" => Scan(args, logger, cancellationToken),
            "aei-info" => AeiInfo(args, cancellationToken),
            "aei-export" => AeiExport(args, logger, cancellationToken),
            "aem-info" => AemInfo(args, cancellationToken),
            "aem-export" => AemExport(args, logger, cancellationToken),
            "aem-preview" => AemPreview(args, logger, cancellationToken),
            "view" => View(args, logger, cancellationToken),
            "validate-corpus" => ValidateCorpus(args, logger, cancellationToken),
            "generate-synthetic" => GenerateSynthetic(args, logger),
            _ => throw new ArgumentException($"Unknown command '{args.Command}'. Run 'help' for usage."),
        };
    }

    private static int Scan(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string root = args.RequirePositional(0, "asset folder");
        AssetPlatformProfile profile = ResolveProfile(args);
        AssetInventory inventory = new AssetScanner().Scan(root, profile, cancellationToken);

        foreach (IGrouping<AssetKind, AssetInventoryEntry> kindGroup in inventory.Assets.GroupBy(asset => asset.Kind))
        {
            logger.Info(
                "scan.kind",
                $"{kindGroup.Key} assets discovered.",
                ("count", kindGroup.Count()),
                ("bytes", kindGroup.Sum(asset => asset.Size)));
            foreach (IGrouping<string, AssetInventoryEntry> formatGroup in kindGroup
                .GroupBy(asset => asset.Classification)
                .OrderByDescending(group => group.Count()))
            {
                logger.Info(
                    "scan.classification",
                    formatGroup.Key,
                    ("kind", kindGroup.Key),
                    ("count", formatGroup.Count()));
            }
        }

        logger.Info(
            "scan.duplicates",
            "Duplicate file-name inventory complete.",
            ("duplicateNames", inventory.DuplicateFileNames.Count));
        WriteJsonOption(args, "json", inventory, logger);
        return 0;
    }

    private static int AeiInfo(CliArguments args, CancellationToken cancellationToken)
    {
        string path = args.RequirePositional(0, "AEI file");
        AeiFile file = new AeiParser().Parse(
            path,
            new AeiParserOptions(
                ResolveProfile(args),
                ResearchDiagnostics: args.HasFlag("research")),
            cancellationToken);
        var report = new
        {
            source = Path.GetFileName(path),
            profile = file.ProfileId,
            format = file.Format,
            dimensions = new { file.Width, file.Height },
            surfaceCount = file.Surfaces.Count,
            file.MipLevelCount,
            file.FaceCount,
            file.ArrayElementCount,
            regionCount = file.Regions.Count,
            symbolMapCount = file.SymbolMaps.Count,
            payloadBytes = file.Payload.Length,
            file.CompressionQuality,
            unknownTrailingBytes = file.UnknownTrailingData.Length,
            surfaces = file.Surfaces,
            regions = file.Regions,
            symbolMaps = file.SymbolMaps,
            diagnostics = file.Diagnostics,
            trace = file.Trace?.Entries,
            traceTruncated = file.Trace?.IsTruncated,
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static int AeiExport(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string path = args.RequirePositional(0, "AEI file");
        string output = args.GetOption(
            "output",
            Path.Combine("work", ObjExporter.SanitizeFileName(Path.GetFileNameWithoutExtension(path)) + "-aei"));
        AeiFile file = new AeiParser().Parse(
            path,
            new AeiParserOptions(ResolveProfile(args), ResearchDiagnostics: args.HasFlag("research")),
            cancellationToken);
        AeiExportResult result = new AeiExportService().Export(file, output, cancellationToken);
        logger.Info(
            "aei.exported",
            result.DecodeStatus,
            ("format", file.Format.DisplayName),
            ("regions", result.RegionPaths.Count),
            ("output", DisplayPath(output)));
        return result.Decoded ? 0 : 2;
    }

    private static int AemInfo(CliArguments args, CancellationToken cancellationToken)
    {
        string path = args.RequirePositional(0, "AEM file");
        AemFile file = new AemParser().Parse(
            path,
            new AemParserOptions(
                ResolveProfile(args),
                ResearchDiagnostics: args.HasFlag("research")),
            cancellationToken);
        SceneDocument scene = new AemSceneConverter().Convert(file);
        var report = new
        {
            source = Path.GetFileName(path),
            profile = file.ProfileId,
            file.Signature,
            version = (int)file.Version,
            flags = $"0x{(byte)file.Flags:X2}",
            submeshCount = file.Submeshes.Count,
            vertices = file.Submeshes.Sum(mesh => mesh.Positions.Length),
            indices = file.Submeshes.Sum(mesh => mesh.Indices.Length),
            triangles = file.Submeshes.Sum(mesh => mesh.Indices.Length / 3),
            animationCurves = file.Submeshes.Sum(mesh => mesh.Animation.Curves.Count),
            animationKeys = file.Submeshes.Sum(mesh => mesh.Animation.Curves.Sum(curve => curve.Keys.Count)),
            submeshes = file.Submeshes.Select(mesh => new
            {
                mesh.Index,
                vertexCount = mesh.Positions.Length,
                indexCount = mesh.Indices.Length,
                mesh.Pivot,
                mesh.BoundingSphere,
                hasUvs = mesh.TextureCoordinates is not null,
                hasNormals = mesh.Normals is not null,
                hasAuxiliaryFloat4 = mesh.AuxiliaryFloat4 is not null,
                animationCurveCount = mesh.Animation.Curves.Count,
                animationKeyCount = mesh.Animation.Curves.Sum(curve => curve.Keys.Count),
            }),
            scene.Bounds,
            diagnostics = scene.Diagnostics,
            trace = file.Trace?.Entries,
            traceTruncated = file.Trace?.IsTruncated,
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static int AemExport(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string path = args.RequirePositional(0, "AEM file");
        string format = args.GetOption("format", "gltf").ToLowerInvariant();
        if (format is not ("gltf" or "obj" or "both"))
        {
            throw new ArgumentException("--format must be gltf, obj, or both.");
        }

        string output = args.GetOption(
            "output",
            Path.Combine("work", ObjExporter.SanitizeFileName(Path.GetFileNameWithoutExtension(path)) + "-aem"));
        AemFile file = new AemParser().Parse(
            path,
            new AemParserOptions(ResolveProfile(args)),
            cancellationToken);
        SceneDocument scene = new AemSceneConverter().Convert(file);

        if (format is "gltf" or "both")
        {
            GltfExportResult result = new GltfExporter().Export(scene, output, cancellationToken: cancellationToken);
            logger.Info(
                "aem.gltf_exported",
                result.AnimationStatus,
                ("primitives", result.PrimitiveCount),
                ("output", DisplayPath(result.GltfPath)));
        }

        if (format is "obj" or "both")
        {
            ObjExportResult result = new ObjExporter().Export(scene, output, cancellationToken: cancellationToken);
            logger.Info(
                "aem.obj_exported",
                "OBJ and MTL written.",
                ("output", DisplayPath(result.ObjPath)));
        }

        return 0;
    }

    private static int AemPreview(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string path = args.RequirePositional(0, "AEM file");
        string output = args.GetOption(
            "output",
            Path.Combine("work", ObjExporter.SanitizeFileName(Path.GetFileNameWithoutExtension(path)) + "-preview.png"));
        int size = args.GetIntOption("size") ?? 1024;
        float? animationTime = args.GetFloatOption("time");
        if (animationTime is < 0)
        {
            throw new ArgumentException("--time must be zero or positive.");
        }

        AemFile file = new AemParser().Parse(
            path,
            new AemParserOptions(ResolveProfile(args)),
            cancellationToken);
        SceneDocument scene = new AemSceneConverter().Convert(file);
        ScenePreviewResult result = new ScenePreviewRenderer().RenderToPng(
            scene,
            output,
            new ScenePreviewOptions(
                Width: size,
                Height: size,
                AnimationTimeSeconds: animationTime),
            cancellationToken);
        logger.Info(
            "aem.preview_rendered",
            "Software preview written with solid, wireframe, normals, pivots, and bounds.",
            ("sourceTriangles", result.SourceTriangleCount),
            ("renderedTriangles", result.RenderedTriangleCount),
            ("normalLines", result.NormalLineCount),
            ("output", DisplayPath(output)));
        return 0;
    }

    private static int View(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string path = args.RequirePositional(0, "asset file");
        string extension = Path.GetExtension(path);
        return extension.Equals(".aei", StringComparison.OrdinalIgnoreCase)
            ? AeiExport(args, logger, cancellationToken)
            : extension.Equals(".aem", StringComparison.OrdinalIgnoreCase)
                ? AemPreview(args, logger, cancellationToken)
                : throw new ArgumentException("View supports .aei and .aem files.");
    }

    private static int ValidateCorpus(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string root = args.RequirePositional(0, "asset folder");
        int? limit = args.GetIntOption("limit");
        if (limit <= 0)
        {
            throw new ArgumentException("--limit must be positive.");
        }

        CorpusValidationReport report = new CorpusValidator(logger).Validate(
            root,
            ResolveProfile(args),
            args.HasFlag("decode"),
            limit,
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        WriteJsonOption(args, "json", report, logger);
        return report.Aei.Corrupt == 0 && report.Aem.Corrupt == 0 ? 0 : 3;
    }

    private static AssetPlatformProfile ResolveProfile(CliArguments args)
    {
        return ProfileCatalog.Resolve(args.GetOption("profile", ProfileCatalog.Pc1X.Id));
    }

    private static void WriteJsonOption(
        CliArguments args,
        string option,
        object value,
        CliLogger logger)
    {
        string? path = args.GetOption(option);
        if (path is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
        logger.Info("json.written", "Machine-readable report written.", ("output", DisplayPath(path)));
    }

    private static string DisplayPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? Path.GetFileName(fullPath)
            : relative;
    }

    private static int ShowHelp()
    {
        Console.WriteLine(
            """
            Galaxy on Fire 2 Workshop technical testbed

            Commands:
              scan <folder> [--profile pc-1x|android] [--json work/inventory.json]
              aei-info <file> [--profile pc-1x] [--research]
              aei-export <file> [--output work/name-aei] [--profile pc-1x]
              aem-info <file> [--profile pc-1x] [--research]
              aem-export <file> [--format gltf|obj|both] [--output work/name-aem]
              aem-preview <file> [--output work/name-preview.png] [--size 1024] [--time seconds]
              view <file> [--output path]
              validate-corpus <folder> [--decode] [--limit N] [--json work/validation.json]
              generate-synthetic [--output samples/SyntheticDemo]

            Generated outputs should remain under the ignored work/ directory.
            """);
        return 0;
    }

    private static int GenerateSynthetic(CliArguments args, CliLogger logger)
    {
        string output = args.GetOption("output", Path.Combine("samples", "SyntheticDemo"));
        SyntheticDemoGenerator.Generate(output);
        logger.Info(
            "synthetic.generated",
            "Original CC0/MIT demonstration assets generated.",
            ("output", DisplayPath(output)));
        return 0;
    }
}
