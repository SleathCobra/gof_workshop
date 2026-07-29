using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.App.Rendering;
using Gof2Workshop.App.Views;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public interface IExportableDocument
{
    public Task ExportDefaultAsync();
}

public interface IUndoableDocument
{
    public System.Windows.Input.ICommand UndoCommand { get; }

    public System.Windows.Input.ICommand RedoCommand { get; }
}

public abstract class DocumentViewModelBase : ObservableObject, IDocument, IInspectorSource
{
    protected static readonly JsonSerializerOptions DetailsJsonOptions = new()
    {
        WriteIndented = true,
    };

    private bool disposed;

    protected DocumentViewModelBase(
        string id,
        string title,
        string kind,
        string? sourcePath,
        bool isReadOnly)
    {
        Id = id;
        Title = title;
        Kind = kind;
        SourcePath = sourcePath;
        IsReadOnly = isReadOnly;
    }

    public event EventHandler? InspectorChanged;

    public string Id { get; }

    public string Title { get; }

    public string Kind { get; }

    public string? SourcePath { get; }

    public bool IsReadOnly { get; }

    public string OwnershipLabel => IsReadOnly ? "ORIGINAL · READ ONLY" : "MOD WORKSPACE";

    public abstract IReadOnlyList<InspectorGroup> InspectorGroups { get; }

    public abstract string AssetDetails { get; }

    protected bool IsDisposed => disposed;

    protected void RaiseInspectorChanged() => InspectorChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DisposeCore();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore()
    {
    }
}

public sealed class WelcomeDocumentViewModel : DocumentViewModelBase
{
    public WelcomeDocumentViewModel(
        System.Windows.Input.ICommand newWorkspaceCommand,
        System.Windows.Input.ICommand openWorkspaceCommand,
        System.Windows.Input.ICommand selectGameFolderCommand)
        : base("welcome", "Welcome", "Welcome", null, isReadOnly: true)
    {
        NewWorkspaceCommand = newWorkspaceCommand;
        OpenWorkspaceCommand = openWorkspaceCommand;
        SelectGameFolderCommand = selectGameFolderCommand;
    }

    public System.Windows.Input.ICommand NewWorkspaceCommand { get; }

    public System.Windows.Input.ICommand OpenWorkspaceCommand { get; }

    public System.Windows.Input.ICommand SelectGameFolderCommand { get; }

    public override IReadOnlyList<InspectorGroup> InspectorGroups =>
    [
        new(
            "Getting Started",
            [
                new InspectorProperty("Workspace", "Create or open a mod workspace"),
                new InspectorProperty("Game assets", "Select a read-only extracted asset folder"),
                new InspectorProperty("Profile", "Choose PC 1.x or Android manually"),
            ]),
    ];

    public override string AssetDetails =>
        "Galaxy on Fire 2 Workshop\n\nOpen a workspace, choose a profile, and scan an extracted game asset folder.";
}

public sealed class UnsupportedDocumentViewModel : DocumentViewModelBase
{
    private readonly IndexedAsset asset;

    public UnsupportedDocumentViewModel(IndexedAsset asset)
        : base(
            DocumentManager.NormalizeDocumentId(asset.FullPath),
            asset.FileName,
            "Unsupported",
            asset.FullPath,
            asset.Ownership == AssetOwnership.Game)
    {
        this.asset = asset;
    }

    public string FriendlyMessage => asset.Kind == AssetKind.Aei &&
        asset.Support == AssetSupport.RecognizedUnsupported
        ? $"This texture uses {asset.Classification}, which the Workshop can identify but cannot decode yet."
        : $"{asset.Classification} is recognized, but this Workshop milestone cannot open its contents.";

    public string TechnicalDetails =>
        $"Path: {asset.RelativePath}\n" +
        $"Kind: {asset.Kind}\n" +
        $"Format/version: {asset.Version ?? "unknown"}\n" +
        $"Classification: {asset.Classification}\n" +
        $"Size: {asset.Size:N0} bytes\n" +
        $"Status: {asset.Support}\n" +
        $"Profile note: {asset.Warning ?? "None"}";

    public override IReadOnlyList<InspectorGroup> InspectorGroups =>
    [
        new(
            "Asset",
            [
                new InspectorProperty("Name", asset.FileName),
                new InspectorProperty("Type", asset.Kind.ToString()),
                new InspectorProperty("Status", asset.Support.ToString()),
                new InspectorProperty("Size", $"{asset.Size:N0} bytes"),
            ]),
        new(
            "Advanced",
            [
                new InspectorProperty("Format", asset.Classification),
                new InspectorProperty("Version / ID", asset.Version ?? "Unknown"),
                new InspectorProperty("Source", asset.Ownership.ToString()),
            ],
            IsAdvanced: true),
    ];

    public override string AssetDetails => TechnicalDetails;
}

public sealed record AeiSurfaceOption(
    int ArrayElement,
    int Face,
    int MipLevel,
    int Width,
    int Height,
    string Label)
{
    public static AeiSurfaceOption FromSurface(AeiSurface surface)
    {
        return new AeiSurfaceOption(
            surface.ArrayElement,
            surface.Face,
            surface.MipLevel,
            surface.Width,
            surface.Height,
            $"Array {surface.ArrayElement} · Face {surface.Face} · Mip {surface.MipLevel} · " +
            $"{surface.Width}×{surface.Height}");
    }
}

public sealed class AeiDocumentViewModel :
    DocumentViewModelBase,
    IExportableDocument,
    IUndoableDocument
{
    private readonly AeiTextureDecoder decoder = new();
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;
    private readonly WorkspaceDefinition workspace;
    private readonly IWorkspaceService workspaceService;
    private readonly string originalSourceHash;
    private readonly RecoveryService recoveryService = new();
    private WriteableBitmap? previewBitmap;
    private RgbaImage? currentImage;
    private RgbaImage? originalImage;
    private AeiEditSession? editSession;
    private AeiSurfaceOption? selectedSurface;
    private AeiRegion? selectedRegion;
    private bool showCheckerboard = true;
    private bool showRegions = true;
    private bool showLabels = true;
    private bool isBusy;
    private bool showOriginal;
    private string decodeStatus;

    public AeiDocumentViewModel(
        IndexedAsset asset,
        AeiFile file,
        RgbaImage? initialImage,
        WorkspaceDefinition workspace,
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems,
        IWorkspaceService workspaceService,
        string originalSourceHash)
        : base(
            DocumentManager.NormalizeDocumentId(asset.FullPath),
            asset.FileName,
            "AEI Texture",
            asset.FullPath,
            asset.Ownership == AssetOwnership.Game)
    {
        Asset = asset;
        File = file;
        this.workspace = workspace;
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
        this.workspaceService = workspaceService;
        this.originalSourceHash = originalSourceHash;
        Surfaces = file.Surfaces.Select(AeiSurfaceOption.FromSurface).ToArray();
        selectedSurface = Surfaces.Count > 0 ? Surfaces[0] : null;
        selectedRegion = file.Regions.Count > 0 ? file.Regions[0] : null;
        decodeStatus = decoder.CanDecode(file.Format.Format)
            ? "Decoded"
            : $"Recognized, decoder unavailable: {file.Format.DisplayName}";
        ExportAtlasCommand = new AsyncRelayCommand(ExportAtlasAsync, () => currentImage is not null);
        ExportSelectedRegionCommand = new AsyncRelayCommand(
            ExportSelectedRegionAsync,
            () => currentImage is not null && SelectedRegion is not null);
        ExportAllCommand = new AsyncRelayCommand(ExportAllAsync);
        SaveAeiCopyCommand = new AsyncRelayCommand(SaveAeiCopyAsync);
        ImportRegionCommand = new AsyncRelayCommand(
            ImportRegionAsync,
            () => currentImage is not null && SelectedRegion is not null);
        UndoCommand = new RelayCommand(_ => Undo(), _ => editSession?.CanUndo == true);
        RedoCommand = new RelayCommand(_ => Redo(), _ => editSession?.CanRedo == true);
        ValidateWorkingCommand = new AsyncRelayCommand(
            ValidateWorkingAsync,
            () => editSession?.IsDirty == true);
        StageWorkingCommand = new AsyncRelayCommand(
            StageWorkingAsync,
            () => editSession?.ValidationState == EditValidationState.Valid
                && Asset.Ownership == AssetOwnership.Game);
        RevertWorkingCommand = new RelayCommand(
            _ => RevertWorking(),
            _ => editSession?.IsDirty == true);
        if (initialImage is not null)
        {
            originalImage = new RgbaImage(
                initialImage.Width,
                initialImage.Height,
                initialImage.ReadOnlyPixelBytes);
            SetImage(initialImage);
        }
    }

    public IndexedAsset Asset { get; }

    public AeiFile File { get; }

    public IReadOnlyList<AeiSurfaceOption> Surfaces { get; }

    public IReadOnlyList<AeiRegion> Regions => File.Regions;

    public WriteableBitmap? PreviewBitmap
    {
        get => previewBitmap;
        private set => SetProperty(ref previewBitmap, value);
    }

    public RgbaImage? CurrentImage => currentImage;

    public bool HasEditSession => editSession is not null;

    public bool IsDirty => editSession?.IsDirty == true;

    public string EditState => editSession switch
    {
        null => IsReadOnly ? "ORIGINAL · READ ONLY" : "MOD WORKSPACE",
        { ValidationState: EditValidationState.Valid } => "WORKING · VALIDATED",
        { ValidationState: EditValidationState.Invalid } => "WORKING · INVALID",
        { ValidationState: EditValidationState.Conflict } => "WORKING · SOURCE CONFLICT",
        _ => "WORKING · UNSAVED OPERATIONS",
    };

    public string DifferenceSummary
    {
        get
        {
            if (editSession is null)
            {
                return "No working changes";
            }

            AeiPixelDifference difference = AeiAtlasEditing.Compare(
                editSession.OriginalAtlas,
                editSession.WorkingAtlas);
            return $"{difference.ChangedPixels:N0} pixels changed · " +
                $"{difference.ChangedAlphaPixels:N0} alpha pixels · " +
                $"max Δ {difference.MaximumChannelError}";
        }
    }

    public bool ShowOriginal
    {
        get => showOriginal;
        set
        {
            if (SetProperty(ref showOriginal, value) && editSession is not null)
            {
                SetImage(value ? editSession.OriginalAtlas : editSession.WorkingAtlas);
            }
        }
    }

    public AeiSurfaceOption? SelectedSurface
    {
        get => selectedSurface;
        set
        {
            if (SetProperty(ref selectedSurface, value) && value is not null)
            {
                _ = DecodeSurfaceAsync(value);
                RaiseInspectorChanged();
            }
        }
    }

    public AeiRegion? SelectedRegion
    {
        get => selectedRegion;
        set
        {
            if (SetProperty(ref selectedRegion, value))
            {
                ((AsyncRelayCommand)ExportSelectedRegionCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)ImportRegionCommand).RaiseCanExecuteChanged();
                RaiseInspectorChanged();
            }
        }
    }

    public bool ShowCheckerboard
    {
        get => showCheckerboard;
        set => SetProperty(ref showCheckerboard, value);
    }

    public bool ShowRegions
    {
        get => showRegions;
        set => SetProperty(ref showRegions, value);
    }

    public bool ShowLabels
    {
        get => showLabels;
        set => SetProperty(ref showLabels, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public string DecodeStatus
    {
        get => decodeStatus;
        private set => SetProperty(ref decodeStatus, value);
    }

    public System.Windows.Input.ICommand ExportAtlasCommand { get; }

    public System.Windows.Input.ICommand ExportSelectedRegionCommand { get; }

    public System.Windows.Input.ICommand ExportAllCommand { get; }

    public System.Windows.Input.ICommand SaveAeiCopyCommand { get; }

    public System.Windows.Input.ICommand ImportRegionCommand { get; }

    public System.Windows.Input.ICommand UndoCommand { get; }

    public System.Windows.Input.ICommand RedoCommand { get; }

    public System.Windows.Input.ICommand ValidateWorkingCommand { get; }

    public System.Windows.Input.ICommand StageWorkingCommand { get; }

    public System.Windows.Input.ICommand RevertWorkingCommand { get; }

    public override IReadOnlyList<InspectorGroup> InspectorGroups
    {
        get
        {
            List<InspectorGroup> groups =
            [
                new(
                    "Texture",
                    [
                        new InspectorProperty("Dimensions", $"{File.Width} × {File.Height}"),
                        new InspectorProperty("Codec", File.Format.DisplayName),
                        new InspectorProperty("Decode", DecodeStatus),
                        new InspectorProperty("Surfaces", File.Surfaces.Count.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Mip levels", File.MipLevelCount.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Cube faces", File.FaceCount.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Atlas regions", File.Regions.Count.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Editing state", EditState),
                        new InspectorProperty("Difference", DifferenceSummary),
                    ]),
            ];
            if (SelectedRegion is not null)
            {
                groups.Add(
                    new InspectorGroup(
                        $"Region {SelectedRegion.Index}",
                        [
                            new InspectorProperty("X", SelectedRegion.X.ToString(CultureInfo.InvariantCulture)),
                            new InspectorProperty("Y", SelectedRegion.Y.ToString(CultureInfo.InvariantCulture)),
                            new InspectorProperty("Width", SelectedRegion.Width.ToString(CultureInfo.InvariantCulture)),
                            new InspectorProperty("Height", SelectedRegion.Height.ToString(CultureInfo.InvariantCulture)),
                        ]));
            }

            groups.Add(
                new InspectorGroup(
                    "Advanced",
                    [
                        new InspectorProperty("Raw format ID", $"0x{File.Format.RawId:X2}"),
                        new InspectorProperty("Payload offset", $"0x{File.PayloadFileOffset:X}"),
                        new InspectorProperty("Payload bytes", File.Payload.Length.ToString("N0", CultureInfo.CurrentCulture)),
                        new InspectorProperty("Unknown trailing bytes", File.UnknownTrailingData.Length.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Profile", File.ProfileId),
                        new InspectorProperty("Parser warnings", File.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)),
                    ],
                    IsAdvanced: true));
            return groups;
        }
    }

    public override string AssetDetails => JsonSerializer.Serialize(
        new
        {
            Asset = Asset.RelativePath,
            File.ProfileId,
            Format = File.Format,
            Dimensions = new { File.Width, File.Height },
            File.MipLevelCount,
            File.FaceCount,
            File.ArrayElementCount,
            RegionCount = File.Regions.Count,
            SymbolMaps = File.SymbolMaps.Count,
            Surfaces = File.Surfaces,
            File.Diagnostics,
        },
        DetailsJsonOptions);

    public Task ExportDefaultAsync() => ExportAllAsync();

    private async Task DecodeSurfaceAsync(AeiSurfaceOption surface)
    {
        if (!decoder.CanDecode(File.Format.Format))
        {
            return;
        }

        IsBusy = true;
        try
        {
            RgbaImage image = await Task.Run(
                () => decoder.DecodeSurface(
                    File,
                    surface.ArrayElement,
                    surface.Face,
                    surface.MipLevel));
            if (!IsDisposed)
            {
                SetImage(image);
                DecodeStatus = $"Decoded {surface.Label}";
            }
        }
        catch (Exception exception)
        {
            DecodeStatus = $"Decode failed: {exception.Message}";
            problems.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Asset.FileName,
                Asset.FullPath,
                File.Format.DisplayName,
                exception.Message,
                null,
                "texture surface",
                "Open Asset Details for technical metadata."));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetImage(RgbaImage image)
    {
        currentImage = image;
        WriteableBitmap next = AvaloniaBitmapFactory.Create(image);
        WriteableBitmap? previous = PreviewBitmap;
        PreviewBitmap = next;
        previous?.Dispose();
        ((AsyncRelayCommand)ExportAtlasCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ExportSelectedRegionCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ImportRegionCommand).RaiseCanExecuteChanged();
    }

    private async Task ImportRegionAsync()
    {
        if (SelectedRegion is null || originalImage is null)
        {
            return;
        }

        if (SelectedSurface is { ArrayElement: not 0 } or { Face: not 0 } or { MipLevel: not 0 })
        {
            throw new InvalidOperationException(
                "Region editing is constrained to the primary atlas surface.");
        }

        string? path = await dialogs.PickAssetFileAsync("Import Replacement Region PNG", ".png");
        if (path is null)
        {
            return;
        }

        RgbaImage replacement = await Task.Run(() => AvaloniaBitmapFactory.LoadRgba(path));
        AeiEditSession session = EnsureEditSession(originalImage);
        AeiRegion selected = SelectedRegion;
        int overlapCount = AeiAtlasEditing.FindOverlaps(File.Regions).Count(
            overlap => overlap.FirstRegionIndex == selected.Index
                || overlap.SecondRegionIndex == selected.Index);
        session.ReplaceRegion(selected.Index, replacement);
        ShowOriginal = false;
        SetImage(session.WorkingAtlas);
        if (overlapCount > 0)
        {
            problems.Add(new ProblemEntry(
                ProblemSeverity.Warning,
                Asset.FileName,
                Asset.FullPath,
                File.Format.DisplayName,
                $"Region {selected.Index} overlaps {overlapCount} other region(s).",
                null,
                "atlas region",
                "Review the original/working comparison before staging."));
        }

        await AutosaveAsync();
        output.Write(
            OutputLevel.Information,
            "Edit",
            $"Region {selected.Index} replaced from {Path.GetFileName(path)}; original remains unchanged.");
        RaiseEditStateChanged();
    }

    private AeiEditSession EnsureEditSession(RgbaImage original)
    {
        if (editSession is not null)
        {
            return editSession;
        }

        editSession = new AeiEditSession(
            Asset.RelativePath.Replace('\\', '/'),
            originalSourceHash,
            Path.Combine("Assets", "Textures", Asset.RelativePath).Replace('\\', '/'),
            File,
            original);
        editSession.Changed += OnEditSessionChanged;
        RaiseEditStateChanged();
        return editSession;
    }

    private void Undo()
    {
        editSession?.Undo();
        RefreshWorkingImageAndAutosave();
    }

    private void Redo()
    {
        editSession?.Redo();
        RefreshWorkingImageAndAutosave();
    }

    private void RevertWorking()
    {
        editSession?.Revert();
        if (editSession is not null)
        {
            ShowOriginal = false;
            SetImage(editSession.WorkingAtlas);
            _ = DiscardRecoveryAsync();
        }
    }

    private void RefreshWorkingImageAndAutosave()
    {
        if (editSession is null)
        {
            return;
        }

        ShowOriginal = false;
        SetImage(editSession.WorkingAtlas);
        _ = AutosaveAsync();
    }

    private async Task ValidateWorkingAsync()
    {
        if (editSession is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            AeiEncodingResult result = await Task.Run(() => editSession.Validate());
            output.Write(
                OutputLevel.Information,
                "Validate",
                $"{Asset.FileName}: reconstruction, reparse, and decode passed; " +
                $"absolute error {result.AbsolutePixelError:N0}, max Δ {result.MaximumChannelError}.");
        }
        catch (Exception exception)
        {
            problems.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Asset.FileName,
                Asset.FullPath,
                File.Format.DisplayName,
                exception.Message,
                null,
                "AEI reconstruction",
                "The working asset was not staged."));
            throw;
        }
        finally
        {
            IsBusy = false;
            RaiseEditStateChanged();
        }
    }

    private async Task StageWorkingAsync()
    {
        if (editSession?.LastValidation is not AeiEncodingResult validation
            || editSession.ValidationState != EditValidationState.Valid)
        {
            throw new InvalidOperationException("Validate the working AEI before staging.");
        }

        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        string validatedRoot = Path.Combine(modRoot, ".work", "validated");
        Directory.CreateDirectory(validatedRoot);
        string candidate = Path.Combine(validatedRoot, $"{Guid.NewGuid():N}.aei");
        new AeiWriter().Write(File, candidate, validation.Payload);
        try
        {
            ModStagingResult staged = await new ModStagingService().StageReplacementAsync(
                workspace,
                Asset,
                candidate,
                overwrite: true);
            output.Write(
                OutputLevel.Information,
                "Changes",
                $"Validated AEI staged at {Path.GetRelativePath(modRoot, staged.StagedPath)}.");
            await AutosaveAsync();
        }
        finally
        {
            if (System.IO.File.Exists(candidate))
            {
                System.IO.File.Delete(candidate);
            }
        }
    }

    private async Task AutosaveAsync()
    {
        if (editSession is null || workspace.FilePath is null)
        {
            return;
        }

        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        await recoveryService.SaveAsync(modRoot, editSession);
    }

    private async Task DiscardRecoveryAsync()
    {
        if (editSession is null || workspace.FilePath is null)
        {
            return;
        }

        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        await recoveryService.DiscardAsync(modRoot, editSession.SourceGameRelativePath);
    }

    private void OnEditSessionChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseEditStateChanged();
        }
        else
        {
            Dispatcher.UIThread.Post(RaiseEditStateChanged);
        }
    }

    private void RaiseEditStateChanged()
    {
        OnPropertyChanged(nameof(HasEditSession));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditState));
        OnPropertyChanged(nameof(DifferenceSummary));
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ValidateWorkingCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)StageWorkingCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RevertWorkingCommand).RaiseCanExecuteChanged();
        RaiseInspectorChanged();
    }

    private async Task ExportAtlasAsync()
    {
        if (currentImage is null)
        {
            return;
        }

        string? path = await dialogs.SaveFileAsync(
            "Export AEI Texture as PNG",
            Path.GetFileNameWithoutExtension(Title) + ".png",
            ".png",
            GetSuggestedOutputDirectory());
        if (path is null)
        {
            return;
        }

        path = PathPolicy.ValidateExportDestination(path, workspace.GameAssetRoot);
        await Task.Run(() => PngWriter.Write(currentImage, path));
        output.Write(OutputLevel.Information, "Export", $"PNG written: {path}");
    }

    private async Task ExportSelectedRegionAsync()
    {
        if (currentImage is null || SelectedRegion is null)
        {
            return;
        }

        AeiRegion region = SelectedRegion;
        if ((long)region.X + region.Width > currentImage.Width ||
            (long)region.Y + region.Height > currentImage.Height)
        {
            throw new InvalidOperationException("The selected region exceeds the decoded surface.");
        }

        string? path = await dialogs.SaveFileAsync(
            "Export Selected Atlas Region",
            $"{Path.GetFileNameWithoutExtension(Title)}-region-{region.Index:D4}.png",
            ".png",
            GetSuggestedOutputDirectory());
        if (path is null)
        {
            return;
        }

        path = PathPolicy.ValidateExportDestination(path, workspace.GameAssetRoot);
        RgbaImage crop = currentImage.Crop(region.X, region.Y, region.Width, region.Height);
        await Task.Run(() => PngWriter.Write(crop, path));
        output.Write(OutputLevel.Information, "Export", $"Region {region.Index} written: {path}");
    }

    private async Task ExportAllAsync()
    {
        string? directory = await dialogs.PickFolderAsync(
            "Export AEI Atlas, Regions, Overlay, and Metadata",
            workspace.FilePath is null
                ? null
                : Path.Combine(Path.GetDirectoryName(workspace.FilePath)!, workspace.OutputRoot));
        if (directory is null)
        {
            return;
        }

        directory = PathPolicy.ValidateExportDestination(directory, workspace.GameAssetRoot);
        AeiExportResult result = await Task.Run(
            () => new AeiExportService().Export(File, directory));
        output.Write(
            result.Decoded ? OutputLevel.Information : OutputLevel.Warning,
            "Export",
            $"{result.DecodeStatus} Output: {directory}");
    }

    private async Task SaveAeiCopyAsync()
    {
        string? path = await dialogs.SaveFileAsync(
            "Save AEI Container Copy",
            Title,
            ".aei",
            GetSuggestedOutputDirectory());
        if (path is null)
        {
            return;
        }

        path = PathPolicy.ValidateExportDestination(path, workspace.GameAssetRoot);
        byte[]? payload = null;
        if (editSession?.IsDirty == true)
        {
            AeiEncodingResult validated = await Task.Run(() => editSession.Validate());
            payload = validated.Payload;
        }

        string destinationPath = path;
        string temporaryPath = destinationPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            if (payload is null)
            {
                await Task.Run(() => new AeiWriter().Write(File, temporaryPath));
            }
            else
            {
                await Task.Run(
                    () => new AeiWriter().Write(
                        File,
                        temporaryPath,
                        new ReadOnlyMemory<byte>(payload)));
            }

            System.IO.File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath))
            {
                System.IO.File.Delete(temporaryPath);
            }
        }

        output.Write(OutputLevel.Information, "Save Copy", $"AEI container written: {path}");
    }

    private string? GetSuggestedOutputDirectory()
    {
        return workspace.FilePath is null
            ? null
            : Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(workspace.FilePath)!,
                    workspace.OutputRoot));
    }

    protected override void DisposeCore()
    {
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        currentImage = null;
        originalImage = null;
        if (editSession is not null)
        {
            editSession.Changed -= OnEditSessionChanged;
        }
    }
}

public sealed record AemSubmeshOption(
    int Index,
    string Name,
    int Vertices,
    int Indices,
    int Triangles,
    string Label);

public sealed class AemMaterialAssignment : ObservableObject
{
    private AssetRelationshipResolution resolution;
    private SceneTextureBinding? binding;

    public AemMaterialAssignment(
        int primitiveIndex,
        string primitiveName,
        AssetRelationshipResolution resolution,
        SceneTextureBinding? binding)
    {
        PrimitiveIndex = primitiveIndex;
        PrimitiveName = primitiveName;
        this.resolution = resolution;
        this.binding = binding;
    }

    public int PrimitiveIndex { get; }

    public string PrimitiveName { get; }

    public AssetRelationshipResolution Resolution => resolution;

    public SceneTextureBinding? Binding => binding;

    public string TextureName =>
        resolution.SelectedAsset?.FileName ?? "Unassigned";

    public string Confidence => resolution.Confidence.ToString();

    public string Source => resolution.Source.ToString();

    public string Reason => resolution.Reason;

    public string Label =>
        $"{PrimitiveIndex:D2} · {TextureName} · {Confidence}";

    public void Update(
        AssetRelationshipResolution nextResolution,
        SceneTextureBinding? nextBinding)
    {
        resolution = nextResolution;
        binding = nextBinding;
        OnPropertyChanged(nameof(Resolution));
        OnPropertyChanged(nameof(Binding));
        OnPropertyChanged(nameof(TextureName));
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(Label));
    }
}

public sealed class AemDocumentViewModel :
    DocumentViewModelBase,
    IExportableDocument
{
    private readonly ScenePreviewRenderer renderer = new();
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;
    private readonly WorkspaceDefinition workspace;
    private readonly IAssetRelationshipService relationships;
    private readonly IWorkspaceService workspaceService;
    private readonly Dictionary<int, SceneTextureBinding> textureBindings = [];
    private CancellationTokenSource? renderCancellation;
    private WriteableBitmap? previewBitmap;
    private SceneCamera camera = new();
    private AemSubmeshOption? selectedSubmesh;
    private bool isolateSubmesh;
    private bool solid = true;
    private bool wireframe = true;
    private bool showNormals;
    private bool showPivots = true;
    private bool showBounds = true;
    private bool showFaceWinding;
    private bool backFaceCulling = true;
    private bool perspective = true;
    private bool useSoftwareRenderer;
    private SceneViewportMode viewportMode = SceneViewportMode.LitTextured;
    private int? focusedPrimitiveIndex;
    private SceneViewportRendererInfo? rendererInfo;
    private SceneViewportFrameMetrics? frameMetrics;
    private bool gpuFailureReported;
    private int renderRevision;
    private bool isRendering;
    private string renderStatus = "Ready";
    private int viewportWidth = 1000;
    private int viewportHeight = 700;
    private readonly DispatcherTimer playbackTimer;
    private readonly System.Diagnostics.Stopwatch playbackClock = new();
    private float playbackStartTime;
    private float animationTimeSeconds;
    private bool isPlaying;

    public AemDocumentViewModel(
        IndexedAsset asset,
        AemFile file,
        SceneDocument scene,
        ScenePreviewResult initialPreview,
        WorkspaceDefinition workspace,
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems,
        IAssetRelationshipService relationships,
        IWorkspaceService workspaceService,
        IReadOnlyList<AemMaterialAssignment> materialAssignments)
        : base(
            DocumentManager.NormalizeDocumentId(asset.FullPath),
            asset.FileName,
            "AEM Model",
            asset.FullPath,
            asset.Ownership == AssetOwnership.Game)
    {
        Asset = asset;
        File = file;
        Scene = scene;
        this.workspace = workspace;
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
        this.relationships = relationships;
        this.workspaceService = workspaceService;
        MaterialAssignments = materialAssignments;
        foreach (AemMaterialAssignment assignment in materialAssignments)
        {
            if (assignment.Binding is not null)
            {
                textureBindings[assignment.PrimitiveIndex] = assignment.Binding;
            }
        }
        Submeshes = scene.Primitives.Select(
            (primitive, index) => new AemSubmeshOption(
                index,
                primitive.Name,
                primitive.Positions.Length,
                primitive.Indices.Length,
                primitive.Indices.Length / 3,
                $"{index:D2} · {primitive.Name} · {primitive.Positions.Length:N0} verts"))
            .ToArray();
        selectedSubmesh = Submeshes.Count > 0 ? Submeshes[0] : null;
        Winding = AggregateWinding(file);
        SetPreview(initialPreview);

        FrameAllCommand = new RelayCommand(FrameAll);
        FrameSelectedCommand = new RelayCommand(
            FrameSelected,
            () => SelectedSubmesh is not null);
        ResetCameraCommand = new RelayCommand(ResetCamera);
        AssignMaterialCommand = new AsyncRelayCommand(AssignMaterialAsync);
        ClearMaterialCommand = new AsyncRelayCommand(ClearMaterialAsync);
        ResetMaterialCommand = new AsyncRelayCommand(ResetMaterialAsync);
        ExportGltfCommand = new AsyncRelayCommand(ExportGltfAsync);
        ExportObjCommand = new AsyncRelayCommand(ExportObjAsync);
        SaveAemCopyCommand = new AsyncRelayCommand(SaveAemCopyAsync);
        PlayPauseCommand = new RelayCommand(
            TogglePlayback,
            () => Scene.Animations.Count > 0);
        StopAnimationCommand = new RelayCommand(
            StopPlayback,
            () => Scene.Animations.Count > 0);
        playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        playbackTimer.Tick += OnPlaybackTick;
    }

    public IndexedAsset Asset { get; }

    public AemFile File { get; }

    public SceneDocument Scene { get; }

    public WindingStatistics Winding { get; }

    public IReadOnlyList<AemSubmeshOption> Submeshes { get; }

    public IReadOnlyList<AemMaterialAssignment> MaterialAssignments { get; }

    public AemMaterialAssignment? SelectedMaterialAssignment =>
        SelectedSubmesh is null
            ? null
            : MaterialAssignments.FirstOrDefault(
                value => value.PrimitiveIndex == SelectedSubmesh.Index);

    public int VertexCount => Scene.Primitives.Sum(primitive => primitive.Positions.Length);

    public int IndexCount => Scene.Primitives.Sum(primitive => primitive.Indices.Length);

    public int TriangleCount => IndexCount / 3;

    public int AnimationCurveCount => File.Submeshes.Sum(mesh => mesh.Animation.Curves.Count);

    public int AnimationKeyCount => File.Submeshes.Sum(
        mesh => mesh.Animation.Curves.Sum(curve => curve.Keys.Count));

    public string AnimationSummary => AnimationCurveCount == 0
        ? "No animation keys"
        : Scene.Animations.Count == 0
            ? $"{AnimationCurveCount} curves · {AnimationKeyCount} keys · non-transform channels preserved"
            : $"{Scene.Animations[0].Name} · {Scene.Animations[0].DurationSeconds:G4} s · " +
              $"{AnimationKeyCount} source keys";

    public float AnimationDurationSeconds => Scene.Animations.Count == 0
        ? 0
        : Scene.Animations[0].DurationSeconds;

    public bool HasAnimation => Scene.Animations.Count > 0;

    public float AnimationTimeSeconds
    {
        get => animationTimeSeconds;
        set
        {
            float clamped = Math.Clamp(value, 0, Math.Max(0, AnimationDurationSeconds));
            if (SetProperty(ref animationTimeSeconds, clamped))
            {
                OnPropertyChanged(nameof(AnimationTimeLabel));
                RequestRender();
                RaiseInspectorChanged();
            }
        }
    }

    public string AnimationTimeLabel =>
        $"{AnimationTimeSeconds:G3} / {AnimationDurationSeconds:G3} s";

    public bool IsPlaying
    {
        get => isPlaying;
        private set
        {
            if (SetProperty(ref isPlaying, value))
            {
                OnPropertyChanged(nameof(PlaybackLabel));
            }
        }
    }

    public string PlaybackLabel => IsPlaying ? "Pause" : "Play";

    public string ViewportSizeLabel => $"{viewportWidth}×{viewportHeight}";

    public string CameraStatus => string.Create(
        CultureInfo.InvariantCulture,
        $"Yaw {camera.Yaw * 180 / MathF.PI:F0}° · Pitch {camera.Pitch * 180 / MathF.PI:F0}° · Zoom {camera.Zoom:F2}");

    public IReadOnlyList<SceneViewportMode> ViewportModes { get; } =
        Enum.GetValues<SceneViewportMode>();

    public SceneViewportMode ViewportMode
    {
        get => viewportMode;
        set
        {
            if (SetProperty(ref viewportMode, value))
            {
                ShowFaceWinding = value == SceneViewportMode.Winding;
                RequestRender();
                RaiseInspectorChanged();
            }
        }
    }

    public bool BackFaceCulling
    {
        get => backFaceCulling;
        set
        {
            if (SetProperty(ref backFaceCulling, value))
            {
                RequestRender();
            }
        }
    }

    public bool UseSoftwareRenderer
    {
        get => useSoftwareRenderer;
        set
        {
            if (SetProperty(ref useSoftwareRenderer, value))
            {
                OnPropertyChanged(nameof(UseOpenGlRenderer));
                if (value)
                {
                    RenderStatus = "Software fallback active";
                    RequestRender();
                }
                else
                {
                    RenderStatus = rendererInfo is null
                        ? "Initializing OpenGL…"
                        : $"{rendererInfo.Name} · {rendererInfo.Device}";
                    OnPropertyChanged(nameof(RenderRevision));
                }

                RaiseInspectorChanged();
            }
        }
    }

    public bool UseOpenGlRenderer => !UseSoftwareRenderer;

    public int RenderRevision => renderRevision;

    public SceneCamera Camera => camera;

    public SceneViewportRendererInfo? RendererInfo => rendererInfo;

    public SceneViewportFrameMetrics? FrameMetrics => frameMetrics;

    public WriteableBitmap? PreviewBitmap
    {
        get => previewBitmap;
        private set => SetProperty(ref previewBitmap, value);
    }

    public AemSubmeshOption? SelectedSubmesh
    {
        get => selectedSubmesh;
        set
        {
            if (SetProperty(ref selectedSubmesh, value))
            {
                FrameSelectedCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedMaterialAssignment));
                RaiseInspectorChanged();
                if (IsolateSubmesh)
                {
                    RequestRender();
                }
            }
        }
    }

    public bool IsolateSubmesh
    {
        get => isolateSubmesh;
        set
        {
            if (SetProperty(ref isolateSubmesh, value))
            {
                RequestRender();
            }
        }
    }

    public bool Solid
    {
        get => solid;
        set
        {
            if (SetProperty(ref solid, value))
            {
                RequestRender();
            }
        }
    }

    public bool Wireframe
    {
        get => wireframe;
        set
        {
            if (SetProperty(ref wireframe, value))
            {
                RequestRender();
            }
        }
    }

    public bool ShowNormals
    {
        get => showNormals;
        set
        {
            if (SetProperty(ref showNormals, value))
            {
                RequestRender();
            }
        }
    }

    public bool ShowPivots
    {
        get => showPivots;
        set
        {
            if (SetProperty(ref showPivots, value))
            {
                RequestRender();
            }
        }
    }

    public bool ShowBounds
    {
        get => showBounds;
        set
        {
            if (SetProperty(ref showBounds, value))
            {
                RequestRender();
            }
        }
    }

    public bool ShowFaceWinding
    {
        get => showFaceWinding;
        set
        {
            if (SetProperty(ref showFaceWinding, value))
            {
                RequestRender();
            }
        }
    }

    public bool Perspective
    {
        get => perspective;
        set
        {
            if (SetProperty(ref perspective, value))
            {
                camera = camera with { Perspective = value };
                RequestRender();
            }
        }
    }

    public bool IsRendering
    {
        get => isRendering;
        private set => SetProperty(ref isRendering, value);
    }

    public string RenderStatus
    {
        get => renderStatus;
        private set => SetProperty(ref renderStatus, value);
    }

    public System.Windows.Input.ICommand FrameAllCommand { get; }

    public RelayCommand FrameSelectedCommand { get; }

    public System.Windows.Input.ICommand ResetCameraCommand { get; }

    public System.Windows.Input.ICommand AssignMaterialCommand { get; }

    public System.Windows.Input.ICommand ClearMaterialCommand { get; }

    public System.Windows.Input.ICommand ResetMaterialCommand { get; }

    public System.Windows.Input.ICommand ExportGltfCommand { get; }

    public System.Windows.Input.ICommand ExportObjCommand { get; }

    public System.Windows.Input.ICommand SaveAemCopyCommand { get; }

    public System.Windows.Input.ICommand PlayPauseCommand { get; }

    public System.Windows.Input.ICommand StopAnimationCommand { get; }

    public override IReadOnlyList<InspectorGroup> InspectorGroups
    {
        get
        {
            List<InspectorGroup> groups =
            [
                new(
                    "Geometry",
                    [
                        new InspectorProperty("Version", $"AEM v{(int)File.Version}"),
                        new InspectorProperty("Submeshes", Scene.Primitives.Count.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Vertices", VertexCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new InspectorProperty("Indices", IndexCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new InspectorProperty("Triangles", TriangleCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new InspectorProperty("Winding", Winding.Interpretation),
                    ]),
                new(
                    "Animation",
                    [
                        new InspectorProperty("Status", AnimationSummary),
                        new InspectorProperty(
                            "Playback time",
                            AnimationTimeLabel),
                        new InspectorProperty(
                            "Export",
                            Scene.Animations.Count == 0
                                ? "No transform clip"
                                : "glTF transform animation enabled"),
                    ]),
                new(
                    "Material",
                    [
                        new InspectorProperty(
                            "Texture",
                            SelectedMaterialAssignment?.TextureName ?? "Unassigned"),
                        new InspectorProperty(
                            "Confidence",
                            SelectedMaterialAssignment?.Confidence ?? "None"),
                        new InspectorProperty(
                            "Relationship source",
                            SelectedMaterialAssignment?.Source ?? "Unresolved"),
                        new InspectorProperty(
                            "Reason",
                            SelectedMaterialAssignment?.Reason ?? "No material selected"),
                    ]),
            ];
            if (SelectedSubmesh is not null)
            {
                ScenePrimitive primitive = Scene.Primitives[SelectedSubmesh.Index];
                groups.Add(
                    new InspectorGroup(
                        $"Submesh {SelectedSubmesh.Index}",
                        [
                            new InspectorProperty("Name", primitive.Name),
                            new InspectorProperty("Vertices", primitive.Positions.Length.ToString("N0", CultureInfo.CurrentCulture)),
                            new InspectorProperty("Triangles", (primitive.Indices.Length / 3).ToString("N0", CultureInfo.CurrentCulture)),
                            new InspectorProperty("Pivot", FormatVector(primitive.SourcePivot)),
                            new InspectorProperty("Bounding radius", primitive.BoundingSphereRadius.ToString("G6", CultureInfo.InvariantCulture)),
                        ]));
            }

            groups.Add(
                new InspectorGroup(
                    "Advanced",
                    [
                        new InspectorProperty("Signature", File.Signature),
                        new InspectorProperty("Flags", $"0x{(byte)File.Flags:X2}"),
                        new InspectorProperty("Profile", File.ProfileId),
                        new InspectorProperty("Source convention", Scene.SourceCoordinateConvention),
                        new InspectorProperty("Unknown trailing bytes", File.UnknownTrailingData.Length.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Parser warnings", File.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Viewport buffer", ViewportSizeLabel),
                        new InspectorProperty(
                            "Renderer",
                            UseSoftwareRenderer
                                ? "Software fallback"
                                : rendererInfo?.Name ?? "OpenGL initializing"),
                        new InspectorProperty(
                            "GPU",
                            rendererInfo?.Device ?? "Not available"),
                        new InspectorProperty(
                            "Frame",
                            frameMetrics is null
                                ? "Not measured"
                                : $"{frameMetrics.FrameMilliseconds:N2} ms · " +
                                  $"{frameMetrics.DrawCalls} draws"),
                    ],
                    IsAdvanced: true));
            return groups;
        }
    }

    public override string AssetDetails => JsonSerializer.Serialize(
        new
        {
            Asset = Asset.RelativePath,
            File.Signature,
            Version = (int)File.Version,
            Flags = $"0x{(byte)File.Flags:X2}",
            Submeshes = Submeshes,
            VertexCount,
            IndexCount,
            TriangleCount,
            AnimationCurveCount,
            AnimationKeyCount,
            Winding,
            Scene.Bounds,
            File.Diagnostics,
        },
        DetailsJsonOptions);

    public Task ExportDefaultAsync() => ExportGltfAsync();

    public void Orbit(double deltaX, double deltaY)
    {
        camera = camera with
        {
            Yaw = camera.Yaw + (float)(deltaX * 0.01),
            Pitch = Math.Clamp(camera.Pitch + (float)(deltaY * 0.01), -1.5f, 1.5f),
        };
        OnPropertyChanged(nameof(CameraStatus));
        RequestRender();
    }

    public void Pan(double deltaX, double deltaY, double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        camera = camera with
        {
            PanX = camera.PanX + (float)(deltaX / viewportWidth),
            PanY = camera.PanY + (float)(deltaY / viewportHeight),
        };
        OnPropertyChanged(nameof(CameraStatus));
        RequestRender();
    }

    public void Zoom(double wheelDelta)
    {
        camera = camera with
        {
            Zoom = Math.Clamp(
                camera.Zoom * (wheelDelta > 0 ? 1.12f : 0.89f),
                0.05f,
                40f),
        };
        OnPropertyChanged(nameof(CameraStatus));
        RequestRender();
    }

    public void FrameAll()
    {
        focusedPrimitiveIndex = null;
        camera = camera with { PanX = 0, PanY = 0, Zoom = 1 };
        OnPropertyChanged(nameof(CameraStatus));
        RequestRender();
    }

    public void FrameSelected()
    {
        if (SelectedSubmesh is null)
        {
            return;
        }

        focusedPrimitiveIndex = SelectedSubmesh.Index;
        camera = camera with { PanX = 0, PanY = 0, Zoom = 1 };
        OnPropertyChanged(nameof(CameraStatus));
        RequestRender();
    }

    public void ResetCamera()
    {
        focusedPrimitiveIndex = null;
        camera = new SceneCamera(Perspective: Perspective);
        OnPropertyChanged(nameof(CameraStatus));
        RequestRender();
    }

    public SceneViewportRequest CreateViewportRequest()
    {
        return new SceneViewportRequest(
            Scene,
            camera,
            ViewportMode,
            Wireframe,
            ShowNormals,
            ShowPivots,
            ShowBounds,
            BackFaceCulling,
            SelectedSubmesh?.Index,
            focusedPrimitiveIndex,
            IsolateSubmesh ? SelectedSubmesh?.Index : null,
            Scene.Animations.Count == 0 ? null : AnimationTimeSeconds,
            textureBindings,
            new System.Numerics.Vector4(0.045f, 0.058f, 0.078f, 1));
    }

    private async Task AssignMaterialAsync()
    {
        AemMaterialAssignment? assignment = SelectedMaterialAssignment;
        if (assignment is null)
        {
            return;
        }

        string? path = await dialogs.PickAssetFileAsync(
            "Assign AEI Texture",
            ".aei");
        if (path is null)
        {
            return;
        }

        try
        {
            FileInfo info = new(path);
            string fullPath = info.FullName;
            string relativePath = !string.IsNullOrWhiteSpace(workspace.GameAssetRoot) &&
                PathPolicy.IsWithin(fullPath, workspace.GameAssetRoot)
                    ? Path.GetRelativePath(workspace.GameAssetRoot, fullPath)
                    : info.Name;
            AssetOwnership ownership = !string.IsNullOrWhiteSpace(workspace.GameAssetRoot) &&
                PathPolicy.IsWithin(fullPath, workspace.GameAssetRoot)
                    ? AssetOwnership.Game
                    : AssetOwnership.Mod;
            IndexedAsset texture = new(
                fullPath,
                relativePath,
                info.Name,
                AssetKind.Aei,
                ownership,
                info.Length,
                info.LastWriteTimeUtc,
                "Manual AEI material",
                null,
                AssetSupport.Supported,
                true,
                null);
            SceneTextureBinding binding = await AemEditorProvider.DecodeTextureAsync(
                texture,
                workspace,
                CancellationToken.None);
            relationships.SetMaterialOverride(
                workspace,
                Asset,
                assignment.PrimitiveIndex,
                texture);
            AssetRelationshipCandidate candidate = new(
                texture,
                AssetRelationshipSource.WorkspaceOverride,
                AssetRelationshipConfidence.Confirmed,
                "Workspace-level manual material assignment.",
                10_000);
            AssetRelationshipResolution resolution = new(
                Asset,
                assignment.PrimitiveIndex,
                candidate.Source,
                candidate.Confidence,
                texture,
                [candidate],
                candidate.Reason,
                []);
            assignment.Update(resolution, binding);
            textureBindings[assignment.PrimitiveIndex] = binding;
            await workspaceService.SaveAsync(workspace);
            NotifyMaterialChanged(assignment, "assigned");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                Gof2Workshop.Binary.FormatParseException)
        {
            problems.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Asset.FileName,
                Asset.FullPath,
                "AEM material",
                exception.Message,
                null,
                "manual material assignment",
                "Choose a valid, decodable AEI texture."));
            output.Write(OutputLevel.Error, "Materials", exception.Message);
        }
    }

    private async Task ClearMaterialAsync()
    {
        AemMaterialAssignment? assignment = SelectedMaterialAssignment;
        if (assignment is null)
        {
            return;
        }

        relationships.DisableMaterial(
            workspace,
            Asset,
            assignment.PrimitiveIndex);
        AssetRelationshipResolution resolution = relationships.ResolveMaterial(
            workspace,
            Asset,
            assignment.PrimitiveIndex);
        assignment.Update(resolution, null);
        textureBindings.Remove(assignment.PrimitiveIndex);
        await workspaceService.SaveAsync(workspace);
        NotifyMaterialChanged(assignment, "cleared");
    }

    private async Task ResetMaterialAsync()
    {
        AemMaterialAssignment? assignment = SelectedMaterialAssignment;
        if (assignment is null)
        {
            return;
        }

        relationships.ClearMaterialOverride(
            workspace,
            Asset,
            assignment.PrimitiveIndex);
        AssetRelationshipResolution resolution = relationships.ResolveMaterial(
            workspace,
            Asset,
            assignment.PrimitiveIndex);
        SceneTextureBinding? binding = resolution.SelectedAsset is null
            ? null
            : await AemEditorProvider.DecodeTextureAsync(
                resolution.SelectedAsset,
                workspace,
                CancellationToken.None);
        assignment.Update(resolution, binding);
        if (binding is null)
        {
            textureBindings.Remove(assignment.PrimitiveIndex);
        }
        else
        {
            textureBindings[assignment.PrimitiveIndex] = binding;
        }

        await workspaceService.SaveAsync(workspace);
        NotifyMaterialChanged(assignment, "reset to automatic resolution");
    }

    private void NotifyMaterialChanged(
        AemMaterialAssignment assignment,
        string action)
    {
        OnPropertyChanged(nameof(SelectedMaterialAssignment));
        RaiseInspectorChanged();
        RequestRender();
        output.Write(
            OutputLevel.Information,
            "Materials",
            $"Material for {Asset.FileName} primitive {assignment.PrimitiveIndex} {action}: " +
            $"{assignment.TextureName} ({assignment.Confidence}).");
    }

    public void PickSubmesh(
        double x,
        double y,
        double viewportWidth,
        double viewportHeight)
    {
        int? picked = SceneViewportPicking.PickPrimitive(
            CreateViewportRequest(),
            x,
            y,
            viewportWidth,
            viewportHeight);
        SelectedSubmesh = picked is int index && index >= 0 && index < Submeshes.Count
            ? Submeshes[index]
            : null;
    }

    public void ReportGpuRendererReady(SceneViewportRendererInfo info)
    {
        rendererInfo = info;
        gpuFailureReported = false;
        OnPropertyChanged(nameof(RendererInfo));
        if (!UseSoftwareRenderer)
        {
            RenderStatus = $"{info.Name} · {info.ApiVersion} · {info.Device}";
        }

        output.Write(
            OutputLevel.Information,
            "Renderer",
            $"OpenGL initialized: {info.ApiVersion}; {info.Vendor}; {info.Device}; " +
            $"max texture {info.MaximumTextureSize:N0}.");
        RaiseInspectorChanged();
    }

    public void ReportGpuFrame(
        SceneViewportFrameMetrics metrics,
        int width,
        int height)
    {
        frameMetrics = metrics;
        viewportWidth = width;
        viewportHeight = height;
        OnPropertyChanged(nameof(FrameMetrics));
        OnPropertyChanged(nameof(ViewportSizeLabel));
        if (!UseSoftwareRenderer)
        {
            RenderStatus = $"OpenGL · {metrics.FrameMilliseconds:N2} ms · " +
                $"{metrics.DrawCalls} draws · {metrics.TriangleCount:N0} triangles";
        }
    }

    public void ReportGpuRendererFailure(string reason)
    {
        if (!gpuFailureReported)
        {
            gpuFailureReported = true;
            output.Write(
                OutputLevel.Warning,
                "Renderer",
                $"OpenGL unavailable for {Asset.FileName}: {reason}. Using software fallback.");
            problems.Add(new ProblemEntry(
                ProblemSeverity.Warning,
                Asset.FileName,
                Asset.FullPath,
                $"AEM v{(int)File.Version}",
                $"Realtime OpenGL viewport unavailable: {reason}",
                null,
                "OpenGL viewport",
                "Update the graphics driver or continue with the software fallback."));
        }

        UseSoftwareRenderer = true;
    }

    public void ReportGpuRendererReleased()
    {
        rendererInfo = null;
        frameMetrics = null;
        OnPropertyChanged(nameof(RendererInfo));
        OnPropertyChanged(nameof(FrameMetrics));
    }

    public void ResizeViewport(double logicalWidth, double logicalHeight, double renderScaling)
    {
        if (!double.IsFinite(logicalWidth) ||
            !double.IsFinite(logicalHeight) ||
            logicalWidth < 1 ||
            logicalHeight < 1)
        {
            return;
        }

        double scale = double.IsFinite(renderScaling)
            ? Math.Clamp(renderScaling, 0.5, 4)
            : 1;
        int width = Math.Clamp((int)Math.Round(logicalWidth * scale), 160, 2048);
        int height = Math.Clamp((int)Math.Round(logicalHeight * scale), 120, 2048);
        const int maximumPixels = 3_000_000;
        long pixels = (long)width * height;
        if (pixels > maximumPixels)
        {
            double reduction = Math.Sqrt(maximumPixels / (double)pixels);
            width = Math.Max(160, (int)Math.Round(width * reduction));
            height = Math.Max(120, (int)Math.Round(height * reduction));
        }

        if (Math.Abs(width - viewportWidth) < 16 &&
            Math.Abs(height - viewportHeight) < 16)
        {
            return;
        }

        viewportWidth = width;
        viewportHeight = height;
        OnPropertyChanged(nameof(ViewportSizeLabel));
        RaiseInspectorChanged();
        RequestRender();
    }

    private void TogglePlayback()
    {
        if (Scene.Animations.Count == 0)
        {
            return;
        }

        if (IsPlaying)
        {
            playbackTimer.Stop();
            IsPlaying = false;
            return;
        }

        playbackStartTime = AnimationTimeSeconds;
        playbackClock.Restart();
        playbackTimer.Start();
        IsPlaying = true;
    }

    private void StopPlayback()
    {
        playbackTimer.Stop();
        playbackClock.Reset();
        IsPlaying = false;
        AnimationTimeSeconds = 0;
    }

    private void OnPlaybackTick(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (AnimationDurationSeconds <= 0)
        {
            StopPlayback();
            return;
        }

        AnimationTimeSeconds = (playbackStartTime + (float)playbackClock.Elapsed.TotalSeconds)
            % AnimationDurationSeconds;
    }

    private void RequestRender()
    {
        renderRevision++;
        OnPropertyChanged(nameof(RenderRevision));
        if (UseSoftwareRenderer)
        {
            _ = RenderAsync();
        }
    }

    private async Task RenderAsync()
    {
        renderCancellation?.Cancel();
        renderCancellation?.Dispose();
        renderCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = renderCancellation.Token;
        ScenePreviewOptions options = CreateOptions();
        IsRendering = true;
        DateTimeOffset start = DateTimeOffset.UtcNow;
        try
        {
            await Task.Delay(35, cancellationToken);
            ScenePreviewResult result = await Task.Run(
                () => renderer.Render(Scene, options, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetPreview(result);
            RenderStatus = $"Rendered {result.RenderedTriangleCount:N0} triangles in " +
                $"{(DateTimeOffset.UtcNow - start).TotalMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RenderStatus = $"Render failed: {exception.Message}";
            problems.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Asset.FileName,
                Asset.FullPath,
                $"AEM v{(int)File.Version}",
                exception.Message,
                null,
                "software viewport",
                "Disable diagnostics or inspect Asset Details."));
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsRendering = false;
            }
        }
    }

    private ScenePreviewOptions CreateOptions()
    {
        return new ScenePreviewOptions(
            Width: viewportWidth,
            Height: viewportHeight,
            Solid: Solid,
            Wireframe: Wireframe,
            ShowNormals: ShowNormals,
            ShowPivots: ShowPivots,
            ShowBoundingSpheres: ShowBounds,
            Camera: camera,
            IsolatedPrimitiveIndex: IsolateSubmesh ? SelectedSubmesh?.Index : null,
            ShowFaceWinding: ShowFaceWinding,
            AnimationTimeSeconds: Scene.Animations.Count == 0
                ? null
                : AnimationTimeSeconds);
    }

    private void SetPreview(ScenePreviewResult result)
    {
        WriteableBitmap next = AvaloniaBitmapFactory.Create(result.Image);
        WriteableBitmap? previous = PreviewBitmap;
        PreviewBitmap = next;
        previous?.Dispose();
    }

    private async Task ExportGltfAsync()
    {
        string? directory = await PickExportDirectoryAsync("Export AEM as glTF 2.0");
        if (directory is null)
        {
            return;
        }

        GltfExportResult result = await Task.Run(
            () => new GltfExporter().ExportWithMaterials(
                Scene,
                directory,
                Path.GetFileNameWithoutExtension(Title),
                MaterialAssignments
                    .Where(assignment =>
                        assignment.Binding is not null &&
                        assignment.Binding.MipImages.Count > 0)
                    .Select(assignment => new GltfTextureAssignment(
                        assignment.PrimitiveIndex,
                        assignment.Binding!.CacheKey,
                        assignment.Binding.DisplayName,
                        assignment.Binding.MipImages[0],
                        assignment.Binding.HasAlpha))
                    .ToArray()));
        string dependencyPath = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(Title) + ".dependencies.json");
        await System.IO.File.WriteAllTextAsync(
            dependencyPath,
            JsonSerializer.Serialize(
                MaterialAssignments.Select(assignment => new
                {
                    assignment.PrimitiveIndex,
                    Texture = assignment.Resolution.SelectedAsset?.RelativePath,
                    assignment.Confidence,
                    assignment.Source,
                    assignment.Reason,
                    Resolved = assignment.Binding is not null,
                }),
                DetailsJsonOptions));
        output.Write(
            OutputLevel.Information,
            "Export",
            $"glTF written: {result.GltfPath}; {result.TexturePaths?.Count ?? 0} texture(s); " +
            $"{result.UnresolvedMaterialPrimitives?.Count ?? 0} unresolved material(s). " +
            result.AnimationStatus);
    }

    private async Task ExportObjAsync()
    {
        string? directory = await PickExportDirectoryAsync("Export AEM as OBJ and MTL");
        if (directory is null)
        {
            return;
        }

        ObjExportResult result = await Task.Run(
            () => new ObjExporter().Export(
                Scene,
                directory,
                Path.GetFileNameWithoutExtension(Title)));
        output.Write(OutputLevel.Information, "Export", $"OBJ/MTL written: {result.ObjPath}");
    }

    private async Task SaveAemCopyAsync()
    {
        string? path = await dialogs.SaveFileAsync(
            "Save Reconstructed AEM Copy",
            Title,
            ".aem",
            GetSuggestedOutputDirectory());
        if (path is null)
        {
            return;
        }

        path = PathPolicy.ValidateExportDestination(path, workspace.GameAssetRoot);
        await Task.Run(() => new AemWriter().Write(File, path));
        output.Write(OutputLevel.Information, "Save Copy", $"Validated AEM copy written: {path}");
    }

    private async Task<string?> PickExportDirectoryAsync(string title)
    {
        string? directory = await dialogs.PickFolderAsync(
            title,
            GetSuggestedOutputDirectory());
        return directory is null
            ? null
            : PathPolicy.ValidateExportDestination(directory, workspace.GameAssetRoot);
    }

    private string? GetSuggestedOutputDirectory()
    {
        return workspace.FilePath is null
            ? null
            : Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(workspace.FilePath)!,
                    workspace.OutputRoot));
    }

    private static WindingStatistics AggregateWinding(AemFile file)
    {
        WindingStatistics[] all = file.Submeshes
            .Select(AemSceneConverter.AnalyzeWinding)
            .ToArray();
        return new WindingStatistics(
            all.Sum(value => value.TriangleCount),
            all.Sum(value => value.AlignedWithNormals),
            all.Sum(value => value.ReversedAgainstNormals),
            all.Sum(value => value.DegenerateOrUnclassified));
    }

    private static string FormatVector(System.Numerics.Vector3 vector)
    {
        return FormattableString.Invariant($"{vector.X:G5}, {vector.Y:G5}, {vector.Z:G5}");
    }

    protected override void DisposeCore()
    {
        renderCancellation?.Cancel();
        renderCancellation?.Dispose();
        renderCancellation = null;
        playbackTimer.Stop();
        playbackTimer.Tick -= OnPlaybackTick;
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
    }
}
