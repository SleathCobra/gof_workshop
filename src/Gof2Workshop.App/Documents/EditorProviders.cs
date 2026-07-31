using System.Diagnostics;
using System.Security.Cryptography;
using Gof2Workshop.App.Rendering;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.GameData;
using Gof2Workshop.Scene;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public sealed class LanguageEditorProvider : IDocumentEditorProvider
{
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;

    public LanguageEditorProvider(
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems)
    {
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
    }

    public string Name => "Language Table Editor";

    public int Priority => 100;

    public bool CanOpen(IndexedAsset asset) =>
        asset.Kind == Core.AssetKind.Language && asset.Support == AssetSupport.Supported;

    public async Task<IDocument> OpenAsync(EditorOpenContext context)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        LanguageTable table = await Task.Run(
            () => new LanguageTableParser().Parse(context.Asset.FullPath),
            context.CancellationToken);
        output.Write(
            OutputLevel.Information,
            "Open",
            $"{context.Asset.FileName}: {table.Entries.Count:N0} language entries parsed in " +
            $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        return new LanguageDocumentViewModel(
            context.Asset,
            table,
            context.Workspace,
            dialogs,
            output,
            problems);
    }
}

public sealed class AeiEditorProvider : IDocumentEditorProvider
{
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;
    private readonly IWorkspaceService workspaceService;

    public AeiEditorProvider(
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems,
        IWorkspaceService workspaceService)
    {
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
        this.workspaceService = workspaceService;
    }

    public string Name => "AEI Texture Editor";

    public int Priority => 100;

    public bool CanOpen(IndexedAsset asset) =>
        asset.Kind == Core.AssetKind.Aei &&
        asset.Support is AssetSupport.Supported or AssetSupport.RecognizedUnsupported;

    public async Task<IDocument> OpenAsync(EditorOpenContext context)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        output.Write(OutputLevel.Information, "Open", $"Parsing {context.Asset.FileName}…");
        AeiFile file = await Task.Run(
            () => new AeiParser().Parse(
                context.Asset.FullPath,
                new AeiParserOptions(Core.ProfileCatalog.Resolve(context.Workspace.ProfileId)),
                context.CancellationToken),
            context.CancellationToken);
        AeiTextureDecoder decoder = new();
        Core.RgbaImage? image = decoder.CanDecode(file.Format.Format)
            ? await Task.Run(
                () => decoder.DecodeAtlas(file, context.CancellationToken),
                context.CancellationToken)
            : null;
        problems.AddRange(file.Diagnostics.Select(
            diagnostic => ProblemEntry.FromDiagnostic(
                context.Asset.FileName,
                context.Asset.FullPath,
                file.Format.DisplayName,
                diagnostic)));
        output.Write(
            image is null ? OutputLevel.Warning : OutputLevel.Information,
            "Open",
            image is null
                ? $"{context.Asset.FileName} metadata parsed in {stopwatch.Elapsed.TotalMilliseconds:N0} ms; " +
                  $"{file.Format.DisplayName} has no decoder."
                : $"{context.Asset.FileName} parsed and decoded in {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        return new AeiDocumentViewModel(
            context.Asset,
            file,
            image,
            context.Workspace,
            dialogs,
            output,
            problems,
            workspaceService,
            await HashFileAsync(context.Asset.FullPath, context.CancellationToken));
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(input, cancellationToken));
    }
}

public sealed class AemEditorProvider : IDocumentEditorProvider
{
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;
    private readonly IAssetRelationshipService relationships;
    private readonly IWorkspaceService workspaceService;

    public AemEditorProvider(
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems,
        IAssetRelationshipService relationships,
        IWorkspaceService workspaceService)
    {
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
        this.relationships = relationships;
        this.workspaceService = workspaceService;
    }

    public string Name => "AEM Model Editor";

    public int Priority => 100;

    public bool CanOpen(IndexedAsset asset) =>
        asset.Kind == Core.AssetKind.Aem && asset.Support == AssetSupport.Supported;

    public async Task<IDocument> OpenAsync(EditorOpenContext context)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        output.Write(OutputLevel.Information, "Open", $"Parsing {context.Asset.FileName}…");
        AemFile file = await Task.Run(
            () => new AemParser().Parse(
                context.Asset.FullPath,
                new AemParserOptions(Core.ProfileCatalog.Resolve(context.Workspace.ProfileId)),
                context.CancellationToken),
            context.CancellationToken);
        SceneDocument scene = await Task.Run(
            () => new AemSceneConverter().Convert(file),
            context.CancellationToken);
        AemMaterialAssignment[] materials = await ResolveMaterialsAsync(
            context,
            scene,
            context.CancellationToken);
        ScenePreviewResult preview = await Task.Run(
            () => new ScenePreviewRenderer().Render(
                scene,
                new ScenePreviewOptions(
                    Width: 1000,
                    Height: 700,
                    ShowNormals: false,
                    Camera: new SceneCamera(Perspective: true)),
                context.CancellationToken),
            context.CancellationToken);
        problems.AddRange(file.Diagnostics
            .Concat(scene.Diagnostics)
            .Select(
                diagnostic => ProblemEntry.FromDiagnostic(
                    context.Asset.FileName,
                    context.Asset.FullPath,
                    $"AEM v{(int)file.Version}",
                    diagnostic)));
        output.Write(
            OutputLevel.Information,
            "Open",
            $"{context.Asset.FileName} parsed and rendered in {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        return new AemDocumentViewModel(
            context.Asset,
            file,
            scene,
            preview,
            context.Workspace,
            dialogs,
            output,
            problems,
            relationships,
            workspaceService,
            materials);
    }

    private async Task<AemMaterialAssignment[]> ResolveMaterialsAsync(
        EditorOpenContext context,
        SceneDocument scene,
        CancellationToken cancellationToken)
    {
        Dictionary<string, SceneTextureBinding?> decoded =
            new(StringComparer.OrdinalIgnoreCase);
        List<AemMaterialAssignment> assignments = new(scene.Primitives.Count);
        for (int primitiveIndex = 0;
             primitiveIndex < scene.Primitives.Count;
             primitiveIndex++)
        {
            AssetRelationshipResolution resolution = relationships.ResolveMaterial(
                context.Workspace,
                context.Asset,
                primitiveIndex);
            SceneTextureBinding? binding = null;
            if (resolution.SelectedAsset is IndexedAsset texture)
            {
                if (!decoded.TryGetValue(texture.FullPath, out binding))
                {
                    try
                    {
                        binding = await DecodeTextureAsync(
                            texture,
                            context.Workspace,
                            cancellationToken);
                    }
                    catch (Exception exception) when (
                        exception is IOException or InvalidDataException or
                            Gof2Workshop.Binary.FormatParseException)
                    {
                        problems.Add(new ProblemEntry(
                            ProblemSeverity.Warning,
                            context.Asset.FileName,
                            context.Asset.FullPath,
                            $"AEM material {primitiveIndex}",
                            $"Resolved texture {texture.FileName} could not be decoded: {exception.Message}",
                            null,
                            "material texture",
                            "Choose another AEI assignment or inspect the texture document."));
                    }

                    decoded.Add(texture.FullPath, binding);
                }
            }

            assignments.Add(new AemMaterialAssignment(
                primitiveIndex,
                scene.Primitives[primitiveIndex].Name,
                resolution,
                binding));
        }

        int resolved = assignments.Count(value => value.Binding is not null);
        output.Write(
            resolved > 0 ? OutputLevel.Information : OutputLevel.Warning,
            "Materials",
            $"{context.Asset.FileName}: {resolved}/{assignments.Count} primitive materials " +
            "resolved to decodable AEI textures.");
        return assignments.ToArray();
    }

    internal static async Task<SceneTextureBinding> DecodeTextureAsync(
        IndexedAsset texture,
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken)
    {
        AeiFile file = await Task.Run(
            () => new AeiParser().Parse(
                texture.FullPath,
                new AeiParserOptions(Core.ProfileCatalog.Resolve(workspace.ProfileId)),
                cancellationToken),
            cancellationToken);
        AeiTextureDecoder decoder = new();
        if (!decoder.CanDecode(file.Format.Format))
        {
            throw new NotSupportedException(
                $"{file.Format.DisplayName} is recognized but has no pixel decoder.");
        }

        AeiSurface[] surfaces = file.Surfaces
            .Where(surface => surface.ArrayElement == 0 && surface.Face == 0)
            .OrderBy(surface => surface.MipLevel)
            .ToArray();
        List<Core.RgbaImage> mips = [];
        if (surfaces.Length == 0)
        {
            mips.Add(decoder.DecodeAtlas(file, cancellationToken));
        }
        else
        {
            foreach (AeiSurface surface in surfaces)
            {
                mips.Add(decoder.DecodeSurface(
                    file,
                    surface.ArrayElement,
                    surface.Face,
                    surface.MipLevel,
                    cancellationToken));
            }
        }

        FileInfo info = new(texture.FullPath);
        string identity = $"{Path.GetFullPath(texture.FullPath)}|" +
            $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|0|0";
        string cacheKey = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        bool hasAlpha = mips[0].ReadOnlyPixelBytes
            .ToArray()
            .Where((_, index) => (index & 3) == 3)
            .Any(value => value != byte.MaxValue);
        return new SceneTextureBinding(
            cacheKey,
            texture.FileName,
            texture.FullPath,
            mips,
            FlipVertically: false,
            hasAlpha);
    }
}

public sealed class UnsupportedEditorProvider : IDocumentEditorProvider
{
    public string Name => "Unsupported Asset Information";

    public int Priority => 10;

    public bool CanOpen(IndexedAsset asset) => true;

    public Task<IDocument> OpenAsync(EditorOpenContext context)
    {
        return Task.FromResult<IDocument>(new UnsupportedDocumentViewModel(context.Asset));
    }
}
