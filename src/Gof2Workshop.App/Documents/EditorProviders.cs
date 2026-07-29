using System.Diagnostics;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public sealed class AeiEditorProvider : IDocumentEditorProvider
{
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;

    public AeiEditorProvider(
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems)
    {
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
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
            problems);
    }
}

public sealed class AemEditorProvider : IDocumentEditorProvider
{
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;

    public AemEditorProvider(
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems)
    {
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
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
            problems);
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
