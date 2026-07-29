using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Gof2Workshop.App.Documents;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Presentation;

public sealed class WorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceService workspaceService;
    private readonly ApplicationStateService applicationStateService;
    private readonly AssetIndexService assetIndex;
    private readonly ProblemService problemService;
    private readonly OutputService outputService;
    private readonly UserDialogService dialogs;
    private readonly DocumentManager documentManager;
    private readonly ModStagingService modStagingService;
    private readonly List<IndexedAsset> gameAssets = [];
    private readonly List<IndexedAsset> modAssets = [];
    private ApplicationState applicationState = new();
    private WorkspaceDefinition? workspace;
    private AssetPlatformProfile selectedProfile = ProfileCatalog.Pc1X;
    private IDocument? selectedDocument;
    private IndexedAsset? selectedGameAsset;
    private IndexedAsset? selectedModAsset;
    private AssetTreeNode? selectedTreeNode;
    private string searchText = string.Empty;
    private string kindFilter = "All";
    private string supportFilter = "All";
    private string formatFilter = "All";
    private string activeActivity = "Explorer";
    private string activeBottomTab = "Output";
    private bool explorerVisible = true;
    private bool inspectorVisible = true;
    private bool bottomVisible = true;
    private bool explorerFloating;
    private bool inspectorFloating;
    private bool bottomFloating;
    private bool isScanning;
    private int scanAssetsFound;
    private string scanStatus = "No game folder selected";
    private string statusMessage = "Ready";
    private CancellationTokenSource? scanCancellation;
    private IInspectorSource? activeInspectorSource;
    private bool disposed;
    private bool syncingDocuments;
    private readonly ObservableCollection<IDocument> documents = [];
    private readonly List<string> documentHistory = [];
    private int documentHistoryIndex = -1;
    private bool navigatingDocumentHistory;

    public WorkbenchViewModel(UserDialogService dialogs)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        workspaceService = new WorkspaceService();
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalaxyOnFire2Workshop",
            "application-state.json");
        applicationStateService = new ApplicationStateService(settingsPath);
        assetIndex = new AssetIndexService();
        problemService = new ProblemService();
        outputService = new OutputService();
        modStagingService = new ModStagingService(workspaceService);

        DocumentEditorRegistry registry = new();
        registry.Register(new AeiEditorProvider(dialogs, outputService, problemService));
        registry.Register(new AemEditorProvider(dialogs, outputService, problemService));
        registry.Register(new UnsupportedEditorProvider());
        documentManager = new DocumentManager(registry);
        documentManager.Changed += OnDocumentsChanged;
        problemService.Changed += OnProblemsChanged;
        outputService.Changed += OnOutputChanged;

        NewWorkspaceCommand = new AsyncRelayCommand(NewWorkspaceAsync);
        OpenWorkspaceCommand = new AsyncRelayCommand(OpenWorkspacePickerAsync);
        CloseWorkspaceCommand = new AsyncRelayCommand(CloseWorkspaceAsync, () => Workspace is not null);
        SelectGameFolderCommand = new AsyncRelayCommand(
            SelectGameFolderAsync,
            () => Workspace is not null);
        RescanCommand = new AsyncRelayCommand(
            RescanAsync,
            () => Workspace?.GameAssetRoot is not null && !IsScanning);
        CancelScanCommand = new RelayCommand(
            () => scanCancellation?.Cancel(),
            () => IsScanning);
        OpenAssetCommand = new AsyncRelayCommand(
            OpenAssetFromParameterAsync,
            parameter => parameter is IndexedAsset or AssetTreeNode || SelectedGameAsset is not null);
        CloseDocumentCommand = new RelayCommand(
            CloseDocumentFromParameter,
            parameter => parameter is IDocument || SelectedDocument is not null);
        CloseOtherDocumentsCommand = new RelayCommand(
            CloseOtherDocuments,
            parameter => parameter is IDocument || SelectedDocument is not null);
        CloseAllDocumentsCommand = new RelayCommand(
            CloseAllDocuments,
            () => Documents.Count > 0);
        PreviousDocumentCommand = new RelayCommand(
            NavigateToPreviousDocument,
            () => documentHistoryIndex > 0);
        NextDocumentCommand = new RelayCommand(
            NavigateToNextDocument,
            () => documentHistoryIndex >= 0 &&
                documentHistoryIndex < documentHistory.Count - 1);
        ExportCurrentCommand = new AsyncRelayCommand(
            ExportCurrentAsync,
            () => SelectedDocument is IExportableDocument);
        RevealCommand = new RelayCommand(
            RevealSelected,
            () => SelectedGameAsset is not null || SelectedDocument?.SourcePath is not null);
        AddToModCommand = new AsyncRelayCommand(
            AddToModAsync,
            () => Workspace is not null && GetSelectedOriginalAsset() is not null);
        ReplaceInModCommand = new AsyncRelayCommand(
            ReplaceInModAsync,
            () => Workspace is not null && GetSelectedOriginalAsset() is not null);
        ShowExplorerCommand = new RelayCommand(() => ExplorerVisible = !ExplorerVisible);
        ShowInspectorCommand = new RelayCommand(() => InspectorVisible = !InspectorVisible);
        ShowBottomCommand = new RelayCommand(() => BottomVisible = !BottomVisible);
        FloatExplorerCommand = new RelayCommand(() => ExplorerFloating = !ExplorerFloating);
        FloatInspectorCommand = new RelayCommand(() => InspectorFloating = !InspectorFloating);
        FloatBottomCommand = new RelayCommand(() => BottomFloating = !BottomFloating);
        ResetLayoutCommand = new RelayCommand(ResetLayout);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        AboutCommand = new RelayCommand(ShowAbout);
        OpenProblemCommand = new AsyncRelayCommand(OpenProblemAsync);
        SetActivityCommand = new RelayCommand(
            parameter => ActiveActivity = parameter as string ?? "Explorer");
        SetBottomTabCommand = new RelayCommand(
            parameter =>
            {
                ActiveBottomTab = parameter as string ?? "Output";
                BottomVisible = true;
            });
    }

    public ObservableCollection<IDocument> Documents => documents;

    public ObservableCollection<IndexedAsset> SearchResults { get; } = [];

    public ObservableCollection<AssetTreeNode> GameAssetTree { get; } = [];

    public ObservableCollection<AssetTreeNode> ModAssetTree { get; } = [];

    public ObservableCollection<ProblemEntry> Problems { get; } = [];

    public ObservableCollection<OutputEntry> OutputEntries { get; } = [];

    public ObservableCollection<InspectorGroup> InspectorGroups { get; } = [];

    public IReadOnlyList<AssetPlatformProfile> Profiles => ProfileCatalog.All;

    public IReadOnlyList<string> KindFilters { get; } = ["All", "AEI", "AEM"];

    public IReadOnlyList<string> SupportFilters { get; } =
        ["All", "Supported", "Unsupported", "Unknown"];

    public ObservableCollection<string> FormatFilters { get; } = ["All"];

    public WorkspaceDefinition? Workspace
    {
        get => workspace;
        private set
        {
            if (SetProperty(ref workspace, value))
            {
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(WorkspacePath));
                OnPropertyChanged(nameof(GameRootStatus));
                RaiseCommandStates();
            }
        }
    }

    public string WorkspaceName => Workspace?.Name ?? "No workspace";

    public string WorkspacePath => Workspace?.FilePath ?? string.Empty;

    public string GameRootStatus => Workspace?.GameAssetRoot is null
        ? "Game root not selected"
        : Directory.Exists(Workspace.GameAssetRoot)
            ? $"Game root: {Path.GetFileName(Path.TrimEndingDirectorySeparator(Workspace.GameAssetRoot))}"
            : "Game root missing";

    public AssetPlatformProfile SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (SetProperty(ref selectedProfile, value) && Workspace is not null)
            {
                Workspace.ProfileId = value.Id;
                StatusMessage = $"Profile changed to {value.DisplayName}. Rescan recommended.";
                _ = SaveWorkspaceSafeAsync();
            }
        }
    }

    public IDocument? SelectedDocument
    {
        get => selectedDocument;
        set
        {
            if (SetProperty(ref selectedDocument, value))
            {
                if (!syncingDocuments)
                {
                    documentManager.ActiveDocument = value;
                }

                AttachInspector(value as IInspectorSource);
                if (!navigatingDocumentHistory && value is not null)
                {
                    RecordDocumentHistory(value);
                }

                OnPropertyChanged(nameof(ActiveFileType));
                RaiseCommandStates();
            }
        }
    }

    public IndexedAsset? SelectedGameAsset
    {
        get => selectedGameAsset;
        set
        {
            if (SetProperty(ref selectedGameAsset, value))
            {
                RaiseCommandStates();
                if (value is not null)
                {
                    SetAssetInspector(value);
                }
            }
        }
    }

    public IndexedAsset? SelectedModAsset
    {
        get => selectedModAsset;
        set
        {
            if (SetProperty(ref selectedModAsset, value) && value is not null)
            {
                SetAssetInspector(value);
            }
        }
    }

    public AssetTreeNode? SelectedTreeNode
    {
        get => selectedTreeNode;
        set
        {
            if (SetProperty(ref selectedTreeNode, value) && value?.Asset is not null)
            {
                if (value.Asset.Ownership == AssetOwnership.Game)
                {
                    SelectedGameAsset = value.Asset;
                }
                else
                {
                    SelectedModAsset = value.Asset;
                }
            }
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value))
            {
                RefreshSearch();
            }
        }
    }

    public string KindFilter
    {
        get => kindFilter;
        set
        {
            if (SetProperty(ref kindFilter, value))
            {
                RefreshSearch();
            }
        }
    }

    public string SupportFilter
    {
        get => supportFilter;
        set
        {
            if (SetProperty(ref supportFilter, value))
            {
                RefreshSearch();
            }
        }
    }

    public string FormatFilter
    {
        get => formatFilter;
        set
        {
            if (SetProperty(ref formatFilter, value))
            {
                RefreshSearch();
            }
        }
    }

    public string ActiveActivity
    {
        get => activeActivity;
        set
        {
            if (SetProperty(ref activeActivity, value))
            {
                OnPropertyChanged(nameof(IsExplorerActivity));
                OnPropertyChanged(nameof(IsSearchActivity));
                OnPropertyChanged(nameof(IsPlaceholderActivity));
            }
        }
    }

    public bool IsExplorerActivity => ActiveActivity == "Explorer";

    public bool IsSearchActivity => ActiveActivity == "Search";

    public bool IsPlaceholderActivity => !IsExplorerActivity && !IsSearchActivity;

    public string ActiveBottomTab
    {
        get => activeBottomTab;
        set
        {
            if (SetProperty(ref activeBottomTab, value))
            {
                OnPropertyChanged(nameof(ActiveBottomTabIndex));
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int ActiveBottomTabIndex
    {
        get => ActiveBottomTab switch
        {
            "Problems" => 1,
            "Asset Details" => 2,
            _ => 0,
        };
        set => ActiveBottomTab = value switch
        {
            1 => "Problems",
            2 => "Asset Details",
            _ => "Output",
        };
    }

    public bool ExplorerVisible
    {
        get => explorerVisible;
        set
        {
            if (SetProperty(ref explorerVisible, value))
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool InspectorVisible
    {
        get => inspectorVisible;
        set
        {
            if (SetProperty(ref inspectorVisible, value))
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool BottomVisible
    {
        get => bottomVisible;
        set
        {
            if (SetProperty(ref bottomVisible, value))
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsScanning
    {
        get => isScanning;
        private set
        {
            if (SetProperty(ref isScanning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public int ScanAssetsFound
    {
        get => scanAssetsFound;
        private set => SetProperty(ref scanAssetsFound, value);
    }

    public string ScanStatus
    {
        get => scanStatus;
        private set => SetProperty(ref scanStatus, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string ActiveFileType => SelectedDocument?.Kind ?? "Welcome";

    public string AssetDetails => activeInspectorSource?.AssetDetails ??
        (SelectedGameAsset is null ? "No asset selected." : FormatAssetDetails(SelectedGameAsset));

    public WindowPlacementState WindowPlacement => applicationState.Window;

    public event EventHandler? LayoutChanged;

    public System.Windows.Input.ICommand NewWorkspaceCommand { get; }

    public System.Windows.Input.ICommand OpenWorkspaceCommand { get; }

    public System.Windows.Input.ICommand CloseWorkspaceCommand { get; }

    public System.Windows.Input.ICommand SelectGameFolderCommand { get; }

    public System.Windows.Input.ICommand RescanCommand { get; }

    public System.Windows.Input.ICommand CancelScanCommand { get; }

    public System.Windows.Input.ICommand OpenAssetCommand { get; }

    public System.Windows.Input.ICommand CloseDocumentCommand { get; }

    public System.Windows.Input.ICommand CloseOtherDocumentsCommand { get; }

    public System.Windows.Input.ICommand CloseAllDocumentsCommand { get; }

    public System.Windows.Input.ICommand PreviousDocumentCommand { get; }

    public System.Windows.Input.ICommand NextDocumentCommand { get; }

    public System.Windows.Input.ICommand ExportCurrentCommand { get; }

    public System.Windows.Input.ICommand RevealCommand { get; }

    public System.Windows.Input.ICommand AddToModCommand { get; }

    public System.Windows.Input.ICommand ReplaceInModCommand { get; }

    public System.Windows.Input.ICommand ShowExplorerCommand { get; }

    public System.Windows.Input.ICommand ShowInspectorCommand { get; }

    public System.Windows.Input.ICommand ShowBottomCommand { get; }

    public System.Windows.Input.ICommand FloatExplorerCommand { get; }

    public System.Windows.Input.ICommand FloatInspectorCommand { get; }

    public System.Windows.Input.ICommand FloatBottomCommand { get; }

    public System.Windows.Input.ICommand ResetLayoutCommand { get; }

    public System.Windows.Input.ICommand OpenLogsCommand { get; }

    public System.Windows.Input.ICommand AboutCommand { get; }

    public System.Windows.Input.ICommand OpenProblemCommand { get; }

    public System.Windows.Input.ICommand SetActivityCommand { get; }

    public System.Windows.Input.ICommand SetBottomTabCommand { get; }

    public async Task InitializeAsync(IReadOnlyList<string> arguments)
    {
        ApplicationStateLoadResult stateResult = await applicationStateService.LoadAsync();
        applicationState = stateResult.State;
        if (stateResult.Warning is not null)
        {
            outputService.Write(OutputLevel.Warning, "Settings", stateResult.Warning);
        }

        string? workspaceArgument = GetOption(arguments, "--workspace");
        string? assetRootArgument = GetOption(arguments, "--asset-root");
        string? openArgument = GetOption(arguments, "--open");
        string? workspaceToOpen = workspaceArgument ??
            (applicationState.LastWorkspace is not null &&
             File.Exists(applicationState.LastWorkspace)
                ? applicationState.LastWorkspace
                : null);
        if (workspaceToOpen is not null)
        {
            await OpenWorkspaceAsync(workspaceToOpen);
        }
        else
        {
            ShowWelcome();
        }

        if (assetRootArgument is not null)
        {
            if (Workspace is null)
            {
                string demoRoot = Path.Combine(
                    Environment.CurrentDirectory,
                    "work",
                    "desktop-smoke-workspace");
                Workspace = await workspaceService.CreateAsync(
                    demoRoot,
                    "Desktop Smoke Workspace",
                    SelectedProfile.Id);
            }

            Workspace.GameAssetRoot = Path.GetFullPath(assetRootArgument);
            await SaveWorkspaceSafeAsync();
            await RescanAsync();
        }

        if (openArgument is not null && Workspace is not null)
        {
            string fullOpen = Path.GetFullPath(openArgument);
            IndexedAsset? asset = gameAssets.Concat(modAssets).FirstOrDefault(
                value => string.Equals(
                    value.FullPath,
                    fullOpen,
                    StringComparison.OrdinalIgnoreCase));
            if (asset is not null)
            {
                await OpenAssetAsync(asset);
            }
        }
    }

    public async Task PersistAsync(
        double windowWidth,
        double windowHeight,
        double? windowX,
        double? windowY,
        bool maximized,
        double explorerWidth,
        double inspectorWidth,
        double bottomHeight)
    {
        if (Workspace is not null)
        {
            Workspace.Layout.ExplorerWidth = explorerWidth;
            Workspace.Layout.InspectorWidth = inspectorWidth;
            Workspace.Layout.BottomHeight = bottomHeight;
            Workspace.Layout.ExplorerVisible = ExplorerVisible;
            Workspace.Layout.InspectorVisible = InspectorVisible;
            Workspace.Layout.BottomVisible = BottomVisible;
            Workspace.Layout.ExplorerFloating = ExplorerFloating;
            Workspace.Layout.InspectorFloating = InspectorFloating;
            Workspace.Layout.BottomFloating = BottomFloating;
            Workspace.Layout.ActiveActivity = ActiveActivity;
            Workspace.Layout.ActiveBottomTab = ActiveBottomTab;
            Workspace.OpenDocuments = documentManager.CaptureState(Workspace.GameAssetRoot).ToList();
            Workspace.ActiveDocumentPath =
                documentManager.CaptureActiveState(Workspace.GameAssetRoot)?.AssetPath;
            await SaveWorkspaceSafeAsync();
        }

        applicationState.Window.Width = windowWidth;
        applicationState.Window.Height = windowHeight;
        applicationState.Window.X = windowX;
        applicationState.Window.Y = windowY;
        applicationState.Window.Maximized = maximized;
        applicationState.LastWorkspace = Workspace?.FilePath;
        await applicationStateService.SaveAsync(applicationState);
    }

    public void StatusMessageForUnhandled(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StatusMessage = $"Operation failed: {exception.Message}";
        outputService.Write(OutputLevel.Error, "Application", exception.ToString());
        problemService.Add(new ProblemEntry(
            ProblemSeverity.Error,
            WorkspaceName,
            WorkspacePath,
            "Application",
            exception.Message,
            null,
            null,
            "See Output for technical details."));
    }

    private async Task NewWorkspaceAsync()
    {
        string? directory = await dialogs.PickFolderAsync("Choose Mod Workspace Folder");
        if (directory is null)
        {
            return;
        }

        string localCorpusRoot = Path.Combine(Environment.CurrentDirectory, "data");
        if (Directory.Exists(localCorpusRoot) &&
            PathPolicy.IsWithin(directory, localCorpusRoot))
        {
            StatusMessage = "A mod workspace cannot be created inside the local data corpus.";
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)),
                directory,
                "Workspace",
                "The selected folder is beneath the read-only local data corpus.",
                null,
                "workspace path",
                "Choose a separate mod-owned folder."));
            return;
        }

        await CloseWorkspaceAsync();
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        Workspace = await workspaceService.CreateAsync(directory, name, SelectedProfile.Id);
        AddRecentWorkspace(Workspace.FilePath!);
        ApplyWorkspaceLayout(Workspace.Layout);
        outputService.Write(OutputLevel.Information, "Workspace", $"Created '{Workspace.Name}'.");
        ShowWelcome();
        await ScanModAssetsAsync();
    }

    private async Task OpenWorkspacePickerAsync()
    {
        string? path = await dialogs.PickWorkspaceFileAsync();
        if (path is not null)
        {
            await OpenWorkspaceAsync(path);
        }
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        await CloseWorkspaceAsync();
        WorkspaceLoadResult result = await workspaceService.LoadAsync(path);
        Workspace = result.Workspace;
        SelectedProfile = ProfileCatalog.Resolve(Workspace.ProfileId);
        ApplyWorkspaceLayout(Workspace.Layout);
        AddRecentWorkspace(path);
        foreach (string warning in result.Warnings)
        {
            outputService.Write(OutputLevel.Warning, "Workspace", warning);
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Warning,
                Workspace.Name,
                Workspace.FilePath,
                "Workspace",
                warning,
                null,
                null,
                "Use File > Select Game Folder when the configured root is stale."));
        }

        outputService.Write(OutputLevel.Information, "Workspace", $"Opened '{Workspace.Name}'.");
        ShowWelcome();
        await ScanModAssetsAsync();
        if (Workspace.GameAssetRoot is not null && Directory.Exists(Workspace.GameAssetRoot))
        {
            await RescanAsync();
            await documentManager.RestoreAsync(
                Workspace.OpenDocuments,
                gameAssets.Concat(modAssets),
                Workspace);
            SyncDocuments();
            if (Workspace.ActiveDocumentPath is not null)
            {
                string activePath = Path.IsPathRooted(Workspace.ActiveDocumentPath)
                    ? Workspace.ActiveDocumentPath
                    : Path.Combine(
                        Workspace.GameAssetRoot ?? string.Empty,
                        Workspace.ActiveDocumentPath);
                IDocument? active = Documents.FirstOrDefault(
                    document => string.Equals(
                        document.SourcePath,
                        Path.GetFullPath(activePath),
                        StringComparison.OrdinalIgnoreCase));
                if (active is not null)
                {
                    SelectedDocument = active;
                }
            }
        }
    }

    private async Task CloseWorkspaceAsync()
    {
        if (Workspace is not null)
        {
            Workspace.OpenDocuments = documentManager.CaptureState(Workspace.GameAssetRoot).ToList();
            Workspace.ActiveDocumentPath =
                documentManager.CaptureActiveState(Workspace.GameAssetRoot)?.AssetPath;
            await SaveWorkspaceSafeAsync();
        }

        scanCancellation?.Cancel();
        documentManager.CloseAll();
        gameAssets.Clear();
        modAssets.Clear();
        SearchResults.Clear();
        GameAssetTree.Clear();
        ModAssetTree.Clear();
        problemService.Clear();
        Workspace = null;
        ScanAssetsFound = 0;
        ScanStatus = "No game folder selected";
        ShowWelcome();
    }

    private async Task SelectGameFolderAsync()
    {
        if (Workspace is null)
        {
            return;
        }

        string? root = await dialogs.PickFolderAsync(
            "Select Extracted Galaxy on Fire 2 Asset Folder",
            Workspace.GameAssetRoot);
        if (root is null)
        {
            return;
        }

        string fullRoot = Path.GetFullPath(root);
        if (Workspace.FilePath is not null &&
            PathPolicy.IsWithin(Workspace.FilePath, fullRoot))
        {
            StatusMessage = "The game root cannot contain the mod workspace.";
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Workspace.Name,
                Workspace.FilePath,
                "Workspace",
                "The selected game root contains the mod workspace configuration.",
                null,
                "game asset root",
                "Move or create the mod workspace outside the original asset tree."));
            return;
        }

        Workspace.GameAssetRoot = fullRoot;
        OnPropertyChanged(nameof(GameRootStatus));
        await SaveWorkspaceSafeAsync();
        await RescanAsync();
    }

    private async Task RescanAsync()
    {
        if (Workspace?.GameAssetRoot is null || !Directory.Exists(Workspace.GameAssetRoot))
        {
            StatusMessage = "Select a valid game asset folder first.";
            return;
        }

        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        scanCancellation = new CancellationTokenSource();
        CancellationToken token = scanCancellation.Token;
        IsScanning = true;
        ScanAssetsFound = 0;
        ScanStatus = "Scanning game assets…";
        problemService.Clear();
        Stopwatch stopwatch = Stopwatch.StartNew();
        Progress<AssetIndexProgress> progress = new(value =>
        {
            ScanAssetsFound = value.AssetsFound;
            ScanStatus = $"Scanning… {value.AssetsFound:N0} assets indexed";
        });

        try
        {
            AssetIndexResult result = await assetIndex.ScanAsync(
                Workspace.GameAssetRoot,
                AssetOwnership.Game,
                SelectedProfile,
                progress,
                token);
            gameAssets.Clear();
            gameAssets.AddRange(result.Assets);
            problemService.AddRange(result.Problems);
            RebuildTree(GameAssetTree, gameAssets);
            RebuildFormats();
            RefreshSearch();
            ScanAssetsFound = gameAssets.Count;
            ScanStatus = $"{gameAssets.Count:N0} assets indexed in {result.Duration.TotalSeconds:N2} s";
            StatusMessage =
                $"Scan complete · +{result.Delta.Added} −{result.Delta.Removed} Δ{result.Delta.Changed}";
            outputService.Write(
                OutputLevel.Information,
                "Scan",
                $"{gameAssets.Count:N0} assets indexed in {stopwatch.Elapsed.TotalSeconds:N2} s; " +
                $"{result.Problems.Count} warnings/errors.");
            OnPropertyChanged(nameof(GameRootStatus));
        }
        catch (OperationCanceledException)
        {
            ScanStatus = $"Scan cancelled after {ScanAssetsFound:N0} assets";
            outputService.Write(OutputLevel.Warning, "Scan", "Game asset scan cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ScanStatus = "Scan failed";
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Workspace.Name,
                Workspace.GameAssetRoot,
                "Asset root",
                exception.Message,
                null,
                null,
                "Check the folder path and permissions."));
            outputService.Write(OutputLevel.Error, "Scan", exception.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ScanModAssetsAsync()
    {
        if (Workspace is null)
        {
            return;
        }

        string modRoot = workspaceService.ResolveModPath(Workspace, Workspace.ModRoot);
        if (!Directory.Exists(modRoot))
        {
            return;
        }

        AssetIndexResult result = await assetIndex.ScanAsync(
            modRoot,
            AssetOwnership.Mod,
            SelectedProfile);
        modAssets.Clear();
        modAssets.AddRange(result.Assets);
        RebuildTree(ModAssetTree, modAssets);
    }

    private async Task OpenAssetFromParameterAsync(object? parameter)
    {
        IndexedAsset? asset = parameter switch
        {
            IndexedAsset indexed => indexed,
            AssetTreeNode node => node.Asset,
            _ => SelectedGameAsset ?? SelectedModAsset,
        };
        if (asset is not null)
        {
            await OpenAssetAsync(asset);
        }
    }

    private async Task OpenAssetAsync(IndexedAsset asset)
    {
        if (Workspace is null)
        {
            return;
        }

        try
        {
            IDocument document = await documentManager.OpenAsync(asset, Workspace);
            SyncDocuments();
            SelectedDocument = document;
            AddRecentAsset(asset.RelativePath);
            StatusMessage = $"{asset.FileName} · {asset.Classification}";
        }
        catch (FormatParseException exception)
        {
            problemService.Add(new ProblemEntry(
                exception.FailureKind == FormatFailureKind.Unsupported
                    ? ProblemSeverity.Warning
                    : ProblemSeverity.Error,
                asset.FileName,
                asset.FullPath,
                asset.Classification,
                exception.Reason,
                exception.Offset,
                exception.Field,
                exception.FailureKind == FormatFailureKind.Unsupported
                    ? "Metadata can still be inspected from the asset index."
                    : "Inspect the parser offset and field in Asset Details."));
            outputService.Write(OutputLevel.Error, "Open", exception.Message);
            IDocument unsupported = documentManager.Add(new UnsupportedDocumentViewModel(asset));
            SelectedDocument = unsupported;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problemService.Add(ProblemEntry.Error(asset, exception.Message, "Check file permissions."));
            outputService.Write(OutputLevel.Error, "Open", exception.Message);
        }
    }

    private void CloseDocumentFromParameter(object? parameter)
    {
        IDocument? document = parameter as IDocument ?? SelectedDocument;
        if (document is not null)
        {
            _ = documentManager.Close(document);
        }
    }

    private void CloseOtherDocuments(object? parameter)
    {
        IDocument? keep = parameter as IDocument ?? SelectedDocument;
        foreach (IDocument document in Documents.Where(value => !ReferenceEquals(value, keep)).ToArray())
        {
            _ = documentManager.Close(document);
        }
    }

    private void CloseAllDocuments()
    {
        documentManager.CloseAll();
        documentHistory.Clear();
        documentHistoryIndex = -1;
        ShowWelcome();
    }

    private void RecordDocumentHistory(IDocument document)
    {
        if (documentHistoryIndex >= 0 &&
            documentHistoryIndex < documentHistory.Count &&
            string.Equals(
                documentHistory[documentHistoryIndex],
                document.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (documentHistoryIndex < documentHistory.Count - 1)
        {
            documentHistory.RemoveRange(
                documentHistoryIndex + 1,
                documentHistory.Count - documentHistoryIndex - 1);
        }

        documentHistory.Add(document.Id);
        if (documentHistory.Count > 100)
        {
            documentHistory.RemoveAt(0);
        }

        documentHistoryIndex = documentHistory.Count - 1;
        RaiseDocumentHistoryStates();
    }

    private void NavigateToPreviousDocument()
    {
        NavigateDocumentHistory(-1);
    }

    private void NavigateToNextDocument()
    {
        NavigateDocumentHistory(1);
    }

    private void NavigateDocumentHistory(int direction)
    {
        int index = documentHistoryIndex + direction;
        while (index >= 0 && index < documentHistory.Count)
        {
            IDocument? target = Documents.FirstOrDefault(document =>
                string.Equals(
                    document.Id,
                    documentHistory[index],
                    StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                navigatingDocumentHistory = true;
                try
                {
                    documentHistoryIndex = index;
                    SelectedDocument = target;
                }
                finally
                {
                    navigatingDocumentHistory = false;
                }

                RaiseDocumentHistoryStates();
                return;
            }

            index += direction;
        }
    }

    public bool ExplorerFloating
    {
        get => explorerFloating;
        set
        {
            if (SetProperty(ref explorerFloating, value))
            {
                if (value)
                {
                    ExplorerVisible = true;
                }

                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool InspectorFloating
    {
        get => inspectorFloating;
        set
        {
            if (SetProperty(ref inspectorFloating, value))
            {
                if (value)
                {
                    InspectorVisible = true;
                }

                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool BottomFloating
    {
        get => bottomFloating;
        set
        {
            if (SetProperty(ref bottomFloating, value))
            {
                if (value)
                {
                    BottomVisible = true;
                }

                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void RaiseDocumentHistoryStates()
    {
        ((RelayCommand)PreviousDocumentCommand).RaiseCanExecuteChanged();
        ((RelayCommand)NextDocumentCommand).RaiseCanExecuteChanged();
    }

    private async Task ExportCurrentAsync()
    {
        if (SelectedDocument is IExportableDocument exportable)
        {
            try
            {
                await exportable.ExportDefaultAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                outputService.Write(OutputLevel.Error, "Export", exception.Message);
                problemService.Add(new ProblemEntry(
                    ProblemSeverity.Error,
                    SelectedDocument.Title,
                    SelectedDocument.SourcePath,
                    SelectedDocument.Kind,
                    exception.Message,
                    null,
                    "export destination",
                    "Choose a destination outside the original game asset root."));
            }
        }
    }

    private async Task AddToModAsync()
    {
        IndexedAsset? source = GetSelectedOriginalAsset();
        if (Workspace is null || source is null)
        {
            return;
        }

        try
        {
            ModStagingResult result = await modStagingService.AddOriginalAsync(
                Workspace,
                source);
            outputService.Write(
                OutputLevel.Information,
                "Add to Mod",
                $"Created mod-owned copy: {Path.GetRelativePath(
                    workspaceService.ResolveModPath(Workspace, Workspace.ModRoot),
                    result.StagedPath)}");
            StatusMessage = $"{source.FileName} added to the mod workspace.";
            await ScanModAssetsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ReportStagingError(source, exception);
        }
    }

    private async Task ReplaceInModAsync()
    {
        IndexedAsset? source = GetSelectedOriginalAsset();
        if (Workspace is null || source is null)
        {
            return;
        }

        string? replacement = await dialogs.PickAssetFileAsync(
            $"Choose replacement for {source.FileName}",
            Path.GetExtension(source.FullPath));
        if (replacement is null)
        {
            return;
        }

        try
        {
            ModStagingResult result = await modStagingService.StageReplacementAsync(
                Workspace,
                source,
                replacement,
                overwrite: true);
            outputService.Write(
                OutputLevel.Information,
                "Replace in Mod",
                $"Validated replacement staged: {Path.GetRelativePath(
                    workspaceService.ResolveModPath(Workspace, Workspace.ModRoot),
                    result.StagedPath)}");
            StatusMessage = $"{source.FileName} replacement staged; the original is unchanged.";
            await ScanModAssetsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ReportStagingError(source, exception);
        }
    }

    private void ReportStagingError(IndexedAsset source, Exception exception)
    {
        outputService.Write(OutputLevel.Error, "Mod staging", exception.Message);
        problemService.Add(new ProblemEntry(
            ProblemSeverity.Error,
            source.FileName,
            source.FullPath,
            source.Classification,
            exception.Message,
            null,
            "mod staging",
            "Choose a valid asset replacement; original game files were not changed."));
        StatusMessage = "Mod staging failed; the original asset is unchanged.";
    }

    private IndexedAsset? GetSelectedOriginalAsset()
    {
        if (SelectedGameAsset is { Ownership: AssetOwnership.Game } selected)
        {
            return selected;
        }

        string? sourcePath = SelectedDocument?.SourcePath;
        return sourcePath is null
            ? null
            : gameAssets.FirstOrDefault(asset =>
                string.Equals(asset.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    private void RevealSelected()
    {
        string? path = SelectedGameAsset?.FullPath ?? SelectedDocument?.SourcePath;
        if (path is not null)
        {
            dialogs.RevealInExplorer(path);
        }
    }

    private async Task OpenProblemAsync(object? parameter)
    {
        if (parameter is not ProblemEntry problem || problem.AssetPath is null)
        {
            return;
        }

        IndexedAsset? asset = gameAssets.Concat(modAssets).FirstOrDefault(
            value => string.Equals(
                value.FullPath,
                problem.AssetPath,
                StringComparison.OrdinalIgnoreCase));
        if (asset is not null)
        {
            await OpenAssetAsync(asset);
        }
    }

    private void ResetLayout()
    {
        WorkbenchLayoutState defaults = new();
        if (Workspace is not null)
        {
            Workspace.Layout = defaults;
        }

        ApplyWorkspaceLayout(defaults);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Workbench layout reset.";
    }

    private void OpenLogs()
    {
        dialogs.RevealInExplorer(Path.GetDirectoryName(applicationStateService.FilePath)!);
    }

    private void ShowAbout()
    {
        StatusMessage = "Galaxy on Fire 2 Workshop · clean-room MIT workbench · Avalonia 12.1";
        outputService.Write(
            OutputLevel.Information,
            "About",
            "Galaxy on Fire 2 Workshop IDE Workbench milestone. Original game assets remain read-only.");
        ShowWelcome();
    }

    private void ShowWelcome()
    {
        IDocument? existing = Documents.FirstOrDefault(document => document.Id == "welcome");
        SelectedDocument = existing ?? documentManager.Add(
            new WelcomeDocumentViewModel(
                NewWorkspaceCommand,
                OpenWorkspaceCommand,
                SelectGameFolderCommand));
    }

    private void RefreshSearch()
    {
        AssetKind? kind = KindFilter switch
        {
            "AEI" => AssetKind.Aei,
            "AEM" => AssetKind.Aem,
            _ => null,
        };
        AssetSupport? support = SupportFilter switch
        {
            "Supported" => AssetSupport.Supported,
            "Unsupported" => AssetSupport.RecognizedUnsupported,
            "Unknown" => AssetSupport.Unknown,
            _ => null,
        };
        string? format = FormatFilter == "All" ? null : FormatFilter;
        IReadOnlyList<IndexedAsset> results = AssetSearchService.Search(
            gameAssets,
            new AssetSearchQuery(SearchText, kind, support, format));
        SearchResults.Clear();
        foreach (IndexedAsset result in results)
        {
            SearchResults.Add(result);
        }
    }

    private void RebuildFormats()
    {
        string previousFormat = FormatFilter;
        FormatFilters.Clear();
        FormatFilters.Add("All");
        foreach (string value in gameAssets
            .Select(asset => asset.Version ?? asset.Classification)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            FormatFilters.Add(value);
        }

        FormatFilter = FormatFilters.Contains(previousFormat) ? previousFormat : "All";
    }

    private static void RebuildTree(
        ObservableCollection<AssetTreeNode> target,
        IEnumerable<IndexedAsset> assets)
    {
        target.Clear();
        foreach (AssetTreeNode node in AssetTreeNode.Build(assets))
        {
            target.Add(node);
        }
    }

    private void OnDocumentsChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncDocuments);
            return;
        }

        SyncDocuments();
    }

    private void SyncDocuments()
    {
        IDocument? desiredActive = documentManager.ActiveDocument;
        bool selectionChanged = !ReferenceEquals(selectedDocument, desiredActive);
        syncingDocuments = true;
        try
        {
            ReconcileDocuments(documentManager.Documents);
            if (selectionChanged)
            {
                selectedDocument = desiredActive;
                OnPropertyChanged(nameof(SelectedDocument));
            }
        }
        finally
        {
            syncingDocuments = false;
        }

        if (documentManager.ActiveDocument != desiredActive)
        {
            documentManager.ActiveDocument = desiredActive;
        }

        if (selectionChanged &&
            !navigatingDocumentHistory &&
            desiredActive is not null)
        {
            RecordDocumentHistory(desiredActive);
        }

        AttachInspector(selectedDocument as IInspectorSource);
        OnPropertyChanged(nameof(ActiveFileType));
        RaiseCommandStates();
        RaiseDocumentHistoryStates();
    }

    private void ReconcileDocuments(IReadOnlyList<IDocument> desired)
    {
        for (int index = 0; index < desired.Count; index++)
        {
            IDocument document = desired[index];
            if (index < Documents.Count && ReferenceEquals(Documents[index], document))
            {
                continue;
            }

            int existingIndex = Documents.IndexOf(document);
            if (existingIndex >= 0)
            {
                Documents.Move(existingIndex, index);
            }
            else
            {
                Documents.Insert(index, document);
            }
        }

        while (Documents.Count > desired.Count)
        {
            Documents.RemoveAt(Documents.Count - 1);
        }
    }

    private void AttachInspector(IInspectorSource? source)
    {
        if (activeInspectorSource is not null)
        {
            activeInspectorSource.InspectorChanged -= OnInspectorChanged;
        }

        activeInspectorSource = source;
        if (activeInspectorSource is not null)
        {
            activeInspectorSource.InspectorChanged += OnInspectorChanged;
        }

        RefreshInspector();
    }

    private void OnInspectorChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        RefreshInspector();
    }

    private void RefreshInspector()
    {
        InspectorGroups.Clear();
        if (activeInspectorSource is not null)
        {
            foreach (InspectorGroup group in activeInspectorSource.InspectorGroups)
            {
                InspectorGroups.Add(group);
            }
        }

        OnPropertyChanged(nameof(AssetDetails));
    }

    private void SetAssetInspector(IndexedAsset asset)
    {
        if (SelectedDocument is not null && SelectedDocument is not WelcomeDocumentViewModel)
        {
            return;
        }

        InspectorGroups.Clear();
        InspectorGroups.Add(
            new InspectorGroup(
                "Indexed Asset",
                [
                    new InspectorProperty("Name", asset.FileName),
                    new InspectorProperty("Location", asset.RelativePath),
                    new InspectorProperty("Kind", asset.Kind.ToString()),
                    new InspectorProperty("Format", asset.Classification),
                    new InspectorProperty("Status", asset.Support.ToString()),
                    new InspectorProperty("Size", $"{asset.Size:N0} bytes"),
                ]));
        OnPropertyChanged(nameof(AssetDetails));
    }

    private void OnProblemsChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        void Update()
        {
            Problems.Clear();
            foreach (ProblemEntry entry in problemService.Entries)
            {
                Problems.Add(entry);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.UIThread.Post(Update);
        }
    }

    private void OnOutputChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        void Update()
        {
            OutputEntries.Clear();
            foreach (OutputEntry entry in outputService.Entries.TakeLast(2000))
            {
                OutputEntries.Add(entry);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.UIThread.Post(Update);
        }
    }

    private void ApplyWorkspaceLayout(WorkbenchLayoutState layout)
    {
        layout.Normalize();
        ExplorerVisible = layout.ExplorerVisible;
        InspectorVisible = layout.InspectorVisible;
        BottomVisible = layout.BottomVisible;
        ExplorerFloating = layout.ExplorerFloating;
        InspectorFloating = layout.InspectorFloating;
        BottomFloating = layout.BottomFloating;
        ActiveActivity = layout.ActiveActivity;
        ActiveBottomTab = layout.ActiveBottomTab;
    }

    private async Task SaveWorkspaceSafeAsync()
    {
        if (Workspace is null)
        {
            return;
        }

        try
        {
            await workspaceService.SaveAsync(Workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            outputService.Write(OutputLevel.Error, "Workspace", exception.Message);
        }
    }

    private void AddRecentWorkspace(string path)
    {
        applicationState.RecentWorkspaces.RemoveAll(
            value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase));
        applicationState.RecentWorkspaces.Insert(0, path);
        if (applicationState.RecentWorkspaces.Count > 10)
        {
            applicationState.RecentWorkspaces.RemoveRange(
                10,
                applicationState.RecentWorkspaces.Count - 10);
        }

        applicationState.LastWorkspace = path;
    }

    private void AddRecentAsset(string relativePath)
    {
        if (Workspace is null)
        {
            return;
        }

        Workspace.RecentAssets.RemoveAll(
            value => string.Equals(value, relativePath, StringComparison.OrdinalIgnoreCase));
        Workspace.RecentAssets.Insert(0, relativePath);
        if (Workspace.RecentAssets.Count > 30)
        {
            Workspace.RecentAssets.RemoveRange(30, Workspace.RecentAssets.Count - 30);
        }
    }

    private void RaiseCommandStates()
    {
        foreach (System.Windows.Input.ICommand command in new[]
        {
            CloseWorkspaceCommand,
            SelectGameFolderCommand,
            RescanCommand,
            CancelScanCommand,
            OpenAssetCommand,
            CloseDocumentCommand,
            CloseOtherDocumentsCommand,
            CloseAllDocumentsCommand,
            ExportCurrentCommand,
            RevealCommand,
            AddToModCommand,
            ReplaceInModCommand,
        })
        {
            switch (command)
            {
                case RelayCommand relay:
                    relay.RaiseCanExecuteChanged();
                    break;
                case AsyncRelayCommand asyncRelay:
                    asyncRelay.RaiseCanExecuteChanged();
                    break;
            }
        }
    }

    private static string? GetOption(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 0; index + 1 < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static string FormatAssetDetails(IndexedAsset asset)
    {
        return $"Name: {asset.FileName}\n" +
            $"Relative path: {asset.RelativePath}\n" +
            $"Kind: {asset.Kind}\n" +
            $"Classification: {asset.Classification}\n" +
            $"Version / format: {asset.Version ?? "Unknown"}\n" +
            $"Support: {asset.Support}\n" +
            $"Preview: {(asset.PreviewSupported ? "Supported" : "Unavailable")}\n" +
            $"Size: {asset.Size:N0} bytes\n" +
            $"Modified: {asset.LastWriteTimeUtc.LocalDateTime:G}\n" +
            $"Warning: {asset.Warning ?? "None"}";
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        documentManager.Changed -= OnDocumentsChanged;
        problemService.Changed -= OnProblemsChanged;
        outputService.Changed -= OnOutputChanged;
        documentManager.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
