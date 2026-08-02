using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.GameData;
using Gof2Workshop.Import;
using Gof2Workshop.Scene;
using Gof2Workshop.Workbench;

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
            "compare-corpora" => CompareCorpora(args, logger, cancellationToken),
            "bin-matrix" => BinMatrix(args, logger, cancellationToken),
            "dependency-report" => DependencyReport(args, logger, cancellationToken),
            "model-import" => ImportModel(args, logger, cancellationToken),
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
        string? texturePath = args.GetOption("texture");
        GltfTextureAssignment[] assignments = [];
        if (!string.IsNullOrWhiteSpace(texturePath))
        {
            AeiFile textureFile = new AeiParser().Parse(
                texturePath,
                new AeiParserOptions(ResolveProfile(args)),
                cancellationToken);
            RgbaImage texture = new AeiTextureDecoder().DecodeAtlas(textureFile, cancellationToken);
            string cacheKey = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(textureFile.Payload));
            assignments = Enumerable.Range(0, scene.Primitives.Count)
                .Select(index => new GltfTextureAssignment(
                    index,
                    cacheKey,
                    Path.GetFileNameWithoutExtension(texturePath),
                    texture,
                    HasAlpha(texture)))
                .ToArray();
        }

        if (format is "gltf" or "both")
        {
            GltfExportResult result = assignments.Length == 0
                ? new GltfExporter().Export(scene, output, cancellationToken: cancellationToken)
                : new GltfExporter().ExportWithMaterials(
                    scene,
                    output,
                    baseName: null,
                    assignments,
                    cancellationToken);
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
            args.HasFlag("roundtrip"),
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

    private static bool HasAlpha(RgbaImage image)
    {
        ReadOnlySpan<byte> pixels = image.ReadOnlyPixelBytes;
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] < byte.MaxValue)
            {
                return true;
            }
        }

        return false;
    }

    private static int CompareCorpora(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        if (args.Positionals.Count != 5)
        {
            throw new ArgumentException(
                "compare-corpora requires roots in this order: PC Android iOS macOS GOF3D-iOS.");
        }

        AssetPlatformProfile[] profiles =
        [
            ProfileCatalog.Pc1X,
            ProfileCatalog.Android,
            ProfileCatalog.IOS,
            ProfileCatalog.MacOS,
            ProfileCatalog.Gof3DIosResearch,
        ];
        MultiCorpusInventoryReport report = new MultiCorpusInventory().Scan(
            profiles.Zip(args.Positionals, (profile, root) => (profile, root)).ToArray(),
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        WriteJsonOption(args, "json", report, logger);
        return report.Corpora.All(corpus => corpus.Present) ? 0 : 3;
    }

    private static int BinMatrix(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        if (args.Positionals.Count != 4)
        {
            throw new ArgumentException("bin-matrix requires roots in this order: PC Android iOS macOS.");
        }

        string[] profiles =
        [
            ProfileCatalog.Pc1X.Id,
            ProfileCatalog.Android.Id,
            ProfileCatalog.IOS.Id,
            ProfileCatalog.MacOS.Id,
        ];
        GameDataSupportMatrixReport report = new GameDataSupportMatrixBuilder().Build(
            profiles.Zip(args.Positionals, (profile, root) => new GameDataCorpusSource(profile, root)),
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        WriteJsonOption(args, "json", report, logger);
        string? markdown = args.GetOption("markdown");
        if (markdown is not null)
        {
            string? directory = Path.GetDirectoryName(markdown);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(markdown, report.ToMarkdown());
            logger.Info("markdown.written", "Generated BIN support matrix written.", ("output", DisplayPath(markdown)));
        }

        return report.ParsedFiles == report.TotalFiles && report.ByteIdenticalRoundTrips == report.TotalFiles
            ? 0
            : 3;
    }

    private static int DependencyReport(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string root = args.RequirePositional(0, "asset folder");
        AssetPlatformProfile profile = ResolveProfile(args);
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        AssetIndexResult index = new AssetIndexService().ScanAsync(
            root,
            AssetOwnership.Game,
            profile,
            cancellationToken: cancellationToken).GetAwaiter().GetResult();
        TimeSpan indexTime = clock.Elapsed;
        DependencyGraph graph = new();
        DependencyGraphSnapshot snapshot = new DependencyGraphBuilder(graph).BuildAsync(
            profile.Id,
            index.Assets,
            cancellationToken).GetAwaiter().GetResult();
        clock.Stop();
        IReadOnlyList<DependencyGraphIssue> issues = new DependencyReferenceValidator(graph).Validate();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: false);
        var report = new
        {
            Profile = profile.Id,
            Assets = index.Assets.Count,
            Nodes = snapshot.Nodes.Count,
            Edges = snapshot.Edges.Count,
            BrokenOrUnresolved = issues.Count,
            IndexMilliseconds = Math.Round(indexTime.TotalMilliseconds, 2),
            GraphMilliseconds = Math.Round((clock.Elapsed - indexTime).TotalMilliseconds, 2),
            TotalMilliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 2),
            ManagedMemoryDeltaBytes = memoryAfter - memoryBefore,
            NodeKinds = snapshot.Nodes.GroupBy(node => node.Kind.ToString()).ToDictionary(group => group.Key, group => group.Count()),
            EdgeKinds = snapshot.Edges.GroupBy(edge => edge.Kind.ToString()).ToDictionary(group => group.Key, group => group.Count()),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        WriteJsonOption(args, "json", report, logger);
        return 0;
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
              scan <folder> [--profile gof2-pc-1x|gof2-android|gof2-ios|gof2-macos|gof3d-ios-research] [--json path]
              aei-info <file> [--profile profile-id] [--research]
              aei-export <file> [--output work/name-aei] [--profile profile-id]
              aem-info <file> [--profile profile-id] [--research]
              aem-export <file> [--format gltf|obj|both] [--texture companion.aei] [--output work/name-aem]
              aem-preview <file> [--output work/name-preview.png] [--size 1024] [--time seconds]
              view <file> [--output path]
              validate-corpus <folder> [--decode] [--roundtrip] [--limit N] [--profile profile-id] [--json path]
              compare-corpora <pc> <android> <ios> <macos> <gof3d-ios> [--json path]
              bin-matrix <pc> <android> <ios> <macos> [--json path] [--markdown path]
              dependency-report <folder> [--profile profile-id] [--json path]
              model-import <gltf|glb|obj> [--version 4|5] [--output work/custom.aem] [--preview path]
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

    private static int ImportModel(
        CliArguments args,
        CliLogger logger,
        CancellationToken cancellationToken)
    {
        string source = args.RequirePositional(0, "glTF, GLB, or OBJ model");
        string extension = Path.GetExtension(source);
        ImportedScene imported = extension.ToLowerInvariant() switch
        {
            ".gltf" or ".glb" => new GltfModelImporter().Import(source, cancellationToken),
            ".obj" => new ObjModelImporter().Import(source, cancellationToken),
            _ => throw new NotSupportedException(
                $"Model extension '{extension}' is unsupported. Choose glTF, GLB, or OBJ."),
        };
        int versionNumber = int.Parse(
            args.GetOption("version", "4"),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        AemVersion version = versionNumber switch
        {
            4 => AemVersion.V4,
            5 => AemVersion.V5,
            _ => throw new ArgumentException("Custom model target version must be 4 or 5."),
        };
        AemAuthoringResult authored = new AemAuthoringService().Author(
            imported,
            new AemAuthoringOptions(version),
            cancellationToken);
        string output = args.GetOption(
            "output",
            Path.Combine("work", ObjExporter.SanitizeFileName(imported.Name) + $"-v{versionNumber}.aem"));
        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.WriteAllBytes(output, authored.Bytes);
        string preview = args.GetOption(
            "preview",
            Path.ChangeExtension(output, ".preview.png"));
        new ScenePreviewRenderer().RenderToPng(
            authored.Scene,
            preview,
            new ScenePreviewOptions(Width: 960, Height: 640),
            cancellationToken);
        logger.Info(
            "model.imported",
            "Imported model authored, reparsed, and rendered successfully.",
            ("source", Path.GetFileName(source)),
            ("target", $"AEM v{versionNumber}"),
            ("submeshes", authored.Reparsed.Submeshes.Count),
            ("vertices", authored.Reparsed.Submeshes.Sum(value => value.Positions.Length)),
            ("triangles", authored.Reparsed.Submeshes.Sum(value => value.Indices.Length / 3)),
            ("output", DisplayPath(output)),
            ("preview", DisplayPath(preview)));
        foreach (ModelImportDiagnostic diagnostic in authored.Diagnostics)
        {
            logger.Info("model.diagnostic", diagnostic.Message, ("code", diagnostic.Code));
        }

        return 0;
    }
}
