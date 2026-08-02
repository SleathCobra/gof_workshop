using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Gof2Workshop.App.Documents;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.GameData;
using Gof2Workshop.Import;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Presentation;

public sealed record DependencyEdgeRow(
    string Direction,
    string Relationship,
    string Target,
    string Evidence,
    string Confidence,
    string State,
    DependencyEdge Edge);

public sealed record SaveDifferenceRow(string Offset, int Length, string Before, string After);

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
    private readonly ModBuildService modBuildService;
    private readonly AssetRelationshipService relationshipService;
    private readonly DependencyGraph dependencyGraph;
    private readonly DependencyGraphBuilder dependencyGraphBuilder;
    private readonly MaterialDependencyContributor materialDependencyContributor;
    private readonly MissionEvidenceService missionEvidenceService = new();
    private readonly MissionEvidenceQueryService missionQueryService = new();
    private readonly TutorialSession tutorialSession = new();
    private readonly List<IndexedAsset> gameAssets = [];
    private readonly List<IndexedAsset> modAssets = [];
    private readonly List<IndexedAsset> inspectionAssets = [];
    private InspectionCollection? inspectionCollection;
    private WorkspaceDefinition? inspectionWorkspace;
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
    private IUndoableDocument? activeUndoableDocument;
    private DependencyNode? selectedDependencyNode;
    private DependencyEdgeRow? selectedDependencyEdge;
    private MissionEvidence? selectedMission;
    private MissionResearchDocument? missionResearch;
    private string missionSearchText = string.Empty;
    private string missionKindFilter = "All";
    private string missionConfidenceFilter = "All";
    private string missionHandlerFilter = "All";

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
        relationshipService = new AssetRelationshipService();
        dependencyGraph = new DependencyGraph();
        dependencyGraphBuilder = new DependencyGraphBuilder(dependencyGraph);
        materialDependencyContributor = new MaterialDependencyContributor(dependencyGraph);
        modBuildService = new ModBuildService(workspaceService, dependencyGraph);
        relationshipService.Changed += OnMaterialRelationshipsChanged;

        DocumentEditorRegistry registry = new();
        registry.Register(new LanguageEditorProvider(
            dialogs,
            outputService,
            problemService));
        registry.Register(new GameDataEditorProvider(
            dialogs,
            outputService,
            problemService));
        registry.Register(new AeiEditorProvider(
            dialogs,
            outputService,
            problemService,
            workspaceService));
        registry.Register(new AemEditorProvider(
            dialogs,
            outputService,
            problemService,
            relationshipService,
            workspaceService));
        registry.Register(new UnsupportedEditorProvider());
        documentManager = new DocumentManager(registry);
        documentManager.Changed += OnDocumentsChanged;
        problemService.Changed += OnProblemsChanged;
        outputService.Changed += OnOutputChanged;

        NewWorkspaceCommand = new AsyncRelayCommand(NewWorkspaceAsync);
        OpenWorkspaceCommand = new AsyncRelayCommand(OpenWorkspacePickerAsync);
        OpenFilesCommand = new AsyncRelayCommand(OpenFilesPickerAsync);
        CreateWorkspaceFromInspectionCommand = new AsyncRelayCommand(
            CreateWorkspaceFromInspectionAsync,
            () => inspectionAssets.Count > 0);
        NewAemCommand = new AsyncRelayCommand(CreateNewAemAsync);
        ImportModelCommand = new AsyncRelayCommand(ImportModelAsync);
        BlenderIntegrationCommand = new AsyncRelayCommand(CheckBlenderIntegrationAsync);
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
        CloseDocumentsToRightCommand = new RelayCommand(
            CloseDocumentsToRight,
            CanCloseDocumentsToRight);
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
        UndoActiveDocumentCommand = new RelayCommand(
            () => ExecuteActiveDocumentCommand(undo: true),
            () => CanExecuteActiveDocumentCommand(undo: true));
        RedoActiveDocumentCommand = new RelayCommand(
            () => ExecuteActiveDocumentCommand(undo: false),
            () => CanExecuteActiveDocumentCommand(undo: false));
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
        ValidateModCommand = new AsyncRelayCommand(
            ValidateModAsync,
            () => Workspace is not null);
        BuildModCommand = new AsyncRelayCommand(
            BuildModAsync,
            () => Workspace is not null);
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
        RefreshDependenciesCommand = new AsyncRelayCommand(RefreshDependencyGraphAsync);
        OpenDependencyCommand = new AsyncRelayCommand(OpenSelectedDependencyAsync, () => SelectedDependencyNode is not null);
        OpenDependencyGraphCommand = new RelayCommand(OpenDependencyGraph, () => SelectedDependencyNode is not null);
        ConfirmDependencyCommand = new AsyncRelayCommand(() => SetDependencyDecisionAsync(RelationshipDecision.Confirmed), () => SelectedDependencyEdge is not null && Workspace is not null);
        RejectDependencyCommand = new AsyncRelayCommand(() => SetDependencyDecisionAsync(RelationshipDecision.Rejected), () => SelectedDependencyEdge is not null && Workspace is not null);
        OpenMissionCommand = new RelayCommand(OpenSelectedMission, () => SelectedMission is not null);
        OpenMissionDependenciesCommand = new RelayCommand(OpenSelectedMissionDependencies, () => SelectedMission is not null);
        ExportMissionResearchCommand = new AsyncRelayCommand(ExportMissionResearchAsync, () => missionResearch is not null);
        CompareMissionSavesCommand = new AsyncRelayCommand(CompareMissionSavesAsync);
        StartTutorialCommand = new RelayCommand(StartTutorial);
        TutorialNextCommand = new RelayCommand(TutorialNext, () => tutorialSession.IsActive);
        TutorialBackCommand = new RelayCommand(TutorialBack, () => tutorialSession.CanGoBack);
        TutorialRestartCommand = new RelayCommand(TutorialRestart, () => tutorialSession.IsActive);
        TutorialSkipCommand = new RelayCommand(TutorialSkip, () => tutorialSession.IsActive);
    }

    public ObservableCollection<IDocument> Documents => documents;

    public ObservableCollection<IndexedAsset> SearchResults { get; } = [];

    public ObservableCollection<AssetTreeNode> GameAssetTree { get; } = [];

    public ObservableCollection<AssetTreeNode> ModAssetTree { get; } = [];

    public ObservableCollection<AssetTreeNode> InspectionAssetTree { get; } = [];

    public ObservableCollection<ProblemEntry> Problems { get; } = [];

    public ObservableCollection<OutputEntry> OutputEntries { get; } = [];

    public ObservableCollection<InspectorGroup> InspectorGroups { get; } = [];

    public ObservableCollection<ModManifestAsset> Changes { get; } = [];

    public ObservableCollection<ModValidationIssue> ChangeIssues { get; } = [];

    public ObservableCollection<SaveDifferenceRow> MissionSaveDifferences { get; } = [];

    public ObservableCollection<DependencyNode> DependencyNodes { get; } = [];

    public ObservableCollection<DependencyEdgeRow> DependencyEdges { get; } = [];

    public ObservableCollection<MissionEvidence> Missions { get; } = [];

    public ObservableCollection<NativeHandlerEvidence> MissionHandlers { get; } = [];

    public IReadOnlyList<string> MissionKindFilters { get; } = ["All", .. Enum.GetNames<MissionEvidenceKind>()];

    public IReadOnlyList<string> MissionConfidenceFilters { get; } = ["All", .. Enum.GetNames<MissionEvidenceConfidence>()];

    public ObservableCollection<string> MissionHandlerFilters { get; } = ["All"];

    public IReadOnlyList<AssetPlatformProfile> Profiles => ProfileCatalog.All;

    public IReadOnlyList<string> KindFilters { get; } = ["All", "AEI", "AEM", "Language", "BIN"];

    public IReadOnlyList<TutorialDefinition> Tutorials => TutorialCatalog.All;

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

    public string WorkspaceName => Workspace?.Name
        ?? (inspectionAssets.Count > 0 ? "Quick Inspect" : "No workspace");

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
            if (!SetProperty(ref selectedProfile, value))
            {
                return;
            }

            inspectionCollection?.ChangeProfile(value);
            if (inspectionWorkspace is not null)
            {
                inspectionWorkspace.ProfileId = value.Id;
            }

            if (Workspace is not null)
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
                AttachActiveUndoableDocument(value as IUndoableDocument);
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

    public DependencyNode? SelectedDependencyNode
    {
        get => selectedDependencyNode;
        set
        {
            if (SetProperty(ref selectedDependencyNode, value))
            {
                RefreshDependencyEdges();
                ((AsyncRelayCommand)OpenDependencyCommand).RaiseCanExecuteChanged();
                ((RelayCommand)OpenDependencyGraphCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public DependencyEdgeRow? SelectedDependencyEdge
    {
        get => selectedDependencyEdge;
        set
        {
            if (SetProperty(ref selectedDependencyEdge, value))
            {
                ((AsyncRelayCommand)ConfirmDependencyCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)RejectDependencyCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public MissionEvidence? SelectedMission
    {
        get => selectedMission;
        set
        {
            if (SetProperty(ref selectedMission, value))
            {
                ((RelayCommand)OpenMissionCommand).RaiseCanExecuteChanged();
                ((RelayCommand)OpenMissionDependenciesCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string MissionSearchText
    {
        get => missionSearchText;
        set
        {
            if (SetProperty(ref missionSearchText, value ?? string.Empty))
            {
                ApplyMissionFilters();
            }
        }
    }

    public string MissionKindFilter
    {
        get => missionKindFilter;
        set
        {
            if (SetProperty(ref missionKindFilter, value ?? "All"))
            {
                ApplyMissionFilters();
            }
        }
    }

    public string MissionConfidenceFilter
    {
        get => missionConfidenceFilter;
        set
        {
            if (SetProperty(ref missionConfidenceFilter, value ?? "All"))
            {
                ApplyMissionFilters();
            }
        }
    }

    public string MissionHandlerFilter
    {
        get => missionHandlerFilter;
        set
        {
            if (SetProperty(ref missionHandlerFilter, value ?? "All"))
            {
                ApplyMissionFilters();
            }
        }
    }

    public string MissionSummary => missionResearch is null
        ? "Mission evidence is built after scanning game data."
        : $"{Missions.Count:N0}/{missionResearch.Missions.Count:N0} evidence groups · {missionResearch.Handlers.Count:N0} native handlers · " +
          $"creation {(missionResearch.MissionCreationEnabled ? "enabled" : "safely gated")}";

    public string DependencySummary
    {
        get
        {
            DependencyGraphSnapshot snapshot = dependencyGraph.Snapshot();
            int broken = snapshot.Edges.Count(edge => edge.ValidationState == DependencyValidationState.Broken);
            return $"{snapshot.Nodes.Count:N0} nodes · {snapshot.Edges.Count:N0} relationships · {broken:N0} broken";
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
                OnPropertyChanged(nameof(IsChangesActivity));
                OnPropertyChanged(nameof(IsDependenciesActivity));
                OnPropertyChanged(nameof(IsMissionsActivity));
                OnPropertyChanged(nameof(IsPlaceholderActivity));
                if (value == "Changes")
                {
                    _ = RefreshChangesAsync();
                }
            }
        }
    }

    public bool IsExplorerActivity => ActiveActivity == "Explorer";

    public bool IsSearchActivity => ActiveActivity == "Search";

    public bool IsChangesActivity => ActiveActivity == "Changes";

    public bool IsDependenciesActivity => ActiveActivity == "Dependencies";

    public bool IsMissionsActivity => ActiveActivity == "Missions";

    public bool IsPlaceholderActivity =>
        !IsExplorerActivity && !IsSearchActivity && !IsChangesActivity && !IsDependenciesActivity && !IsMissionsActivity;

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

    public bool TutorialVisible => tutorialSession.IsActive;

    public string TutorialTitle => tutorialSession.ActiveTutorial?.Title ?? string.Empty;

    public string TutorialStepTitle => tutorialSession.CurrentStep?.Title ?? string.Empty;

    public string TutorialInstruction => tutorialSession.CurrentStep?.Instruction ?? string.Empty;

    public string TutorialTarget => tutorialSession.CurrentStep is null
        ? string.Empty
        : $"Focus: {tutorialSession.CurrentStep.Target}";

    public string TutorialProgress => tutorialSession.ActiveTutorial is null
        ? string.Empty
        : $"Step {tutorialSession.StepIndex + 1} of {tutorialSession.ActiveTutorial.Steps.Count}";

    public string AssetDetails => activeInspectorSource?.AssetDetails ??
        (SelectedGameAsset is null ? "No asset selected." : FormatAssetDetails(SelectedGameAsset));

    public WindowPlacementState WindowPlacement => applicationState.Window;

    public event EventHandler? LayoutChanged;

    public System.Windows.Input.ICommand NewWorkspaceCommand { get; }

    public System.Windows.Input.ICommand OpenWorkspaceCommand { get; }

    public System.Windows.Input.ICommand OpenFilesCommand { get; }

    public System.Windows.Input.ICommand CreateWorkspaceFromInspectionCommand { get; }

    public System.Windows.Input.ICommand ImportModelCommand { get; }

    public System.Windows.Input.ICommand NewAemCommand { get; }

    public System.Windows.Input.ICommand BlenderIntegrationCommand { get; }

    public System.Windows.Input.ICommand CloseWorkspaceCommand { get; }

    public System.Windows.Input.ICommand SelectGameFolderCommand { get; }

    public System.Windows.Input.ICommand RescanCommand { get; }

    public System.Windows.Input.ICommand CancelScanCommand { get; }

    public System.Windows.Input.ICommand RefreshDependenciesCommand { get; }

    public System.Windows.Input.ICommand OpenDependencyCommand { get; }

    public System.Windows.Input.ICommand OpenDependencyGraphCommand { get; }

    public System.Windows.Input.ICommand ConfirmDependencyCommand { get; }

    public System.Windows.Input.ICommand RejectDependencyCommand { get; }

    public System.Windows.Input.ICommand OpenMissionCommand { get; }

    public System.Windows.Input.ICommand OpenMissionDependenciesCommand { get; }

    public System.Windows.Input.ICommand ExportMissionResearchCommand { get; }

    public System.Windows.Input.ICommand CompareMissionSavesCommand { get; }

    public System.Windows.Input.ICommand OpenAssetCommand { get; }

    public System.Windows.Input.ICommand CloseDocumentCommand { get; }

    public System.Windows.Input.ICommand CloseOtherDocumentsCommand { get; }

    public System.Windows.Input.ICommand CloseDocumentsToRightCommand { get; }

    public System.Windows.Input.ICommand CloseAllDocumentsCommand { get; }

    public System.Windows.Input.ICommand PreviousDocumentCommand { get; }

    public System.Windows.Input.ICommand NextDocumentCommand { get; }

    public System.Windows.Input.ICommand UndoActiveDocumentCommand { get; }

    public System.Windows.Input.ICommand RedoActiveDocumentCommand { get; }

    public System.Windows.Input.ICommand ExportCurrentCommand { get; }

    public System.Windows.Input.ICommand RevealCommand { get; }

    public System.Windows.Input.ICommand AddToModCommand { get; }

    public System.Windows.Input.ICommand ReplaceInModCommand { get; }

    public System.Windows.Input.ICommand ValidateModCommand { get; }

    public System.Windows.Input.ICommand BuildModCommand { get; }

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

    public System.Windows.Input.ICommand StartTutorialCommand { get; }

    public System.Windows.Input.ICommand TutorialNextCommand { get; }

    public System.Windows.Input.ICommand TutorialBackCommand { get; }

    public System.Windows.Input.ICommand TutorialRestartCommand { get; }

    public System.Windows.Input.ICommand TutorialSkipCommand { get; }

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
        string? profileArgument = GetOption(arguments, "--profile");
        string? tutorialArgument = GetOption(arguments, "--tutorial");
        string? newAemTemplateArgument = GetOption(arguments, "--new-aem-template");
        List<string> openArguments = GetOpenPaths(arguments);
        if (profileArgument is not null)
        {
            SelectedProfile = ProfileCatalog.Resolve(profileArgument);
        }
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

        if (openArguments.Count > 0)
        {
            await OpenStandalonePathsAsync(openArguments);
        }

        if (!string.IsNullOrWhiteSpace(tutorialArgument))
        {
            StartTutorial(tutorialArgument);
        }

        if (!string.IsNullOrWhiteSpace(newAemTemplateArgument))
        {
            if (!Enum.TryParse(newAemTemplateArgument, ignoreCase: true, out AemAuthoringTemplate template))
            {
                throw new ArgumentException($"Unknown AEM authoring template '{newAemTemplateArgument}'.");
            }
            OpenSyntheticAemTemplate(template);
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

    private async Task OpenFilesPickerAsync()
    {
        IReadOnlyList<string> paths = await dialogs.PickAssetFilesAsync("Quick Inspect Files");
        if (paths.Count > 0)
        {
            await OpenStandalonePathsAsync(paths);
        }
    }

    private async Task CreateWorkspaceFromInspectionAsync()
    {
        if (inspectionAssets.Count == 0)
        {
            return;
        }

        string? directory = await dialogs.PickFolderAsync(
            "Create Workspace from Quick Inspect Files");
        if (directory is null)
        {
            return;
        }

        string fullDirectory = Path.GetFullPath(directory);
        foreach (string corpusName in new[]
        {
            "data", "android_data", "ios_data", "macos_data", "ios2_data", "ios_data2",
        })
        {
            string corpus = Path.Combine(Environment.CurrentDirectory, corpusName);
            if (Directory.Exists(corpus) && PathPolicy.IsWithin(fullDirectory, corpus))
            {
                problemService.Add(new ProblemEntry(
                    ProblemSeverity.Error,
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(fullDirectory)),
                    fullDirectory,
                    "Workspace",
                    "The selected workspace folder is beneath a read-only compatibility corpus.",
                    null,
                    "workspace path",
                    "Choose a separate user-owned folder."));
                return;
            }
        }

        WorkspaceDefinition created = await workspaceService.CreateAsync(
            fullDirectory,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(fullDirectory)),
            SelectedProfile.Id);
        string modRoot = workspaceService.ResolveModPath(created, created.ModRoot);
        foreach (IndexedAsset source in inspectionAssets)
        {
            string category = source.Kind switch
            {
                AssetKind.Aei => Path.Combine("Assets", "Textures"),
                AssetKind.Aem => Path.Combine("Assets", "Models"),
                AssetKind.Language => Path.Combine("Assets", "Data"),
                AssetKind.GameData => Path.Combine("Assets", "Data"),
                _ => Path.Combine("Assets", "Other"),
            };
            string targetDirectory = Path.Combine(modRoot, category);
            Directory.CreateDirectory(targetDirectory);
            string target = GetUnusedDestination(targetDirectory, source.FileName);
            File.Copy(source.FullPath, target, overwrite: false);
        }

        Workspace = created;
        AddRecentWorkspace(created.FilePath!);
        ApplyWorkspaceLayout(created.Layout);
        await ScanModAssetsAsync();
        outputService.Write(
            OutputLevel.Information,
            "Quick Inspect",
            $"Created '{created.Name}' with {inspectionAssets.Count:N0} user-owned asset copies. Original inspection files remain unchanged.");
        StatusMessage = "Quick Inspect collection copied into a new workspace";
    }

    private static string GetUnusedDestination(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(directory, fileName);
        for (int suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{stem}-{suffix}{extension}");
        }

        return candidate;
    }

    public async Task OpenStandalonePathsAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        inspectionCollection ??= new InspectionCollection(SelectedProfile);
        inspectionWorkspace ??= inspectionCollection.CreateTransientWorkspace();
        InspectionCollectionUpdate update = await inspectionCollection.AddAsync(paths, cancellationToken);
        foreach (IndexedAsset asset in update.AddedAssets)
        {
            inspectionAssets.Add(asset);
            AddRecentStandaloneFile(asset.FullPath);
        }

        problemService.AddRange(update.Problems);
        relationshipService.UpdateAssets(gameAssets.Concat(modAssets).Concat(inspectionAssets));
        RebuildTree(InspectionAssetTree, inspectionAssets);
        RebuildFormats();
        RefreshSearch();
        RaiseCommandStates();
        OnPropertyChanged(nameof(WorkspaceName));
        if (update.AddedAssets.Count == 0)
        {
            StatusMessage = update.CompanionFiles.Count > 0
                ? $"Added {update.CompanionFiles.Count:N0} companion files; add an AEI or AEM to inspect."
                : "No new supported inspection files were added.";
            return;
        }

        foreach (IndexedAsset asset in update.AddedAssets)
        {
            await OpenAssetAsync(asset);
        }

        outputService.Write(
            OutputLevel.Information,
            "Quick Inspect",
            $"Added {update.AddedAssets.Count:N0} assets and {update.CompanionFiles.Count:N0} companion files without creating a workspace.");
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
            relationshipService.UpdateAssets(gameAssets.Concat(modAssets).Concat(inspectionAssets));
            problemService.AddRange(result.Problems);
            RebuildTree(GameAssetTree, gameAssets);
            RebuildFormats();
            RefreshSearch();
            await RefreshDependencyGraphAsync();
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
        relationshipService.UpdateAssets(gameAssets.Concat(modAssets).Concat(inspectionAssets));
        RebuildTree(ModAssetTree, modAssets);
        await RefreshDependencyGraphAsync();
    }

    private async Task RefreshDependencyGraphAsync()
    {
        IndexedAsset[] assets = gameAssets.Concat(modAssets).Concat(inspectionAssets).ToArray();
        if (assets.Length == 0)
        {
            dependencyGraph.ReplaceScope("corpus:" + SelectedProfile.Id, [], []);
            RefreshDependencyCollections();
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            DependencyGraphSnapshot snapshot = await dependencyGraphBuilder.BuildAsync(
                SelectedProfile.Id,
                assets,
                scanCancellation?.Token ?? CancellationToken.None);
            if (Workspace is not null)
            {
                materialDependencyContributor.Update(SelectedProfile.Id, Workspace, assets);
            }
            await RefreshMissionResearchAsync(assets, scanCancellation?.Token ?? CancellationToken.None);
            snapshot = dependencyGraph.Snapshot();
            RefreshDependencyCollections(snapshot);
            outputService.Write(
                OutputLevel.Information,
                "Dependencies",
                $"Built {snapshot.Nodes.Count:N0} nodes and {snapshot.Edges.Count:N0} relationships in " +
                $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        }
        catch (OperationCanceledException)
        {
            outputService.Write(OutputLevel.Warning, "Dependencies", "Dependency analysis cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatParseException)
        {
            outputService.Write(OutputLevel.Error, "Dependencies", exception.Message);
        }
    }

    private void RefreshDependencyCollections(DependencyGraphSnapshot? supplied = null)
    {
        DependencyGraphSnapshot snapshot = supplied ?? dependencyGraph.Snapshot();
        string? selectedId = SelectedDependencyNode?.Id.Value;
        DependencyNodes.Clear();
        foreach (DependencyNode node in snapshot.Nodes
            .Where(node => node.Kind != DependencyNodeKind.UnknownExternalReference)
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(20_000))
        {
            DependencyNodes.Add(node);
        }

        SelectedDependencyNode = selectedId is null
            ? DependencyNodes.FirstOrDefault()
            : DependencyNodes.FirstOrDefault(node => node.Id.Value.Equals(selectedId, StringComparison.Ordinal));
        OnPropertyChanged(nameof(DependencySummary));
    }

    private void OnMaterialRelationshipsChanged(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (Workspace is null)
        {
            return;
        }

        materialDependencyContributor.Update(
            SelectedProfile.Id,
            Workspace,
            gameAssets.Concat(modAssets).Concat(inspectionAssets));
        RefreshDependencyCollections();
    }

    private void RefreshDependencyEdges()
    {
        DependencyEdges.Clear();
        SelectedDependencyEdge = null;
        if (SelectedDependencyNode is null)
        {
            return;
        }

        foreach (DependencyEdge edge in dependencyGraph.GetUses(SelectedDependencyNode.Id))
        {
            dependencyGraph.TryGetNode(edge.Target, out DependencyNode? target);
            DependencyEdges.Add(new DependencyEdgeRow(
                "Uses",
                edge.Kind.ToString(),
                target?.DisplayName ?? edge.Target.Value,
                edge.Evidence,
                DependencyConfidence(edge),
                edge.ValidationState.ToString(),
                edge));
        }

        foreach (DependencyEdge edge in dependencyGraph.GetReferencedBy(SelectedDependencyNode.Id))
        {
            dependencyGraph.TryGetNode(edge.Source, out DependencyNode? source);
            DependencyEdges.Add(new DependencyEdgeRow(
                "Referenced by",
                edge.Kind.ToString(),
                source?.DisplayName ?? edge.Source.Value,
                edge.Evidence,
                DependencyConfidence(edge),
                edge.ValidationState.ToString(),
                edge));
        }
    }

    private string DependencyConfidence(DependencyEdge edge)
    {
        if (Workspace is null)
        {
            return edge.EvidenceLevel.ToString();
        }

        RelationshipDecision decision = new RelationshipEvidenceService().GetDecision(Workspace, edge);
        return decision == RelationshipDecision.None
            ? edge.EvidenceLevel.ToString()
            : $"{edge.EvidenceLevel} · user {decision.ToString().ToLowerInvariant()}";
    }

    private async Task OpenSelectedDependencyAsync()
    {
        DependencyNode? node = SelectedDependencyNode;
        if (SelectedDependencyEdge is { } row)
        {
            DependencyNodeId related = row.Direction == "Uses" ? row.Edge.Target : row.Edge.Source;
            dependencyGraph.TryGetNode(related, out node);
        }

        if (node is null)
        {
            return;
        }

        await OpenDependencyNodeAsync(node);
    }

    private async Task OpenDependencyNodeAsync(DependencyNode node)
    {
        if (string.IsNullOrWhiteSpace(node.SourcePath))
        {
            return;
        }

        IndexedAsset? asset = gameAssets.Concat(modAssets).Concat(inspectionAssets).FirstOrDefault(candidate =>
            candidate.RelativePath.Replace('\\', '/').Equals(
                node.SourcePath.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase));
        if (asset is not null)
        {
            await OpenAssetAsync(asset);
        }
    }

    private void OpenDependencyGraph()
    {
        if (SelectedDependencyNode is null)
        {
            return;
        }

        _ = documentManager.Add(new DependencyGraphDocumentViewModel(
            dependencyGraph,
            SelectedDependencyNode,
            OpenDependencyNodeAsync,
            ExportDependencyReportAsync));
    }

    private async Task ExportDependencyReportAsync(string json)
    {
        try
        {
            string? start = Workspace is null
                ? null
                : workspaceService.ResolveModPath(Workspace, Workspace.OutputRoot);
            string? destination = await dialogs.SaveFileAsync(
                "Export dependency report",
                "dependency-report.json",
                ".json",
                start);
            if (string.IsNullOrWhiteSpace(destination))
            {
                return;
            }
            destination = PathPolicy.ValidateExportDestination(destination, Workspace?.GameAssetRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, destination, overwrite: true);
            outputService.Write(OutputLevel.Information, "Dependencies",
                $"Dependency report written to {Path.GetFileName(destination)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            outputService.Write(OutputLevel.Error, "Dependencies", exception.Message);
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                "Dependency report",
                null,
                "Dependencies",
                exception.Message,
                null,
                "Export",
                "Choose a writable destination outside the immutable game root."));
        }
    }

    private async Task SetDependencyDecisionAsync(RelationshipDecision decision)
    {
        if (Workspace is null || SelectedDependencyEdge is null)
        {
            return;
        }

        RelationshipEvidenceService evidence = new();
        if (decision == RelationshipDecision.Confirmed)
        {
            evidence.Confirm(Workspace, SelectedDependencyEdge.Edge);
        }
        else
        {
            evidence.Reject(Workspace, SelectedDependencyEdge.Edge);
        }
        if (Workspace.FilePath is not null)
        {
            await workspaceService.SaveAsync(Workspace);
        }
        RefreshDependencyEdges();
    }

    private async Task RefreshMissionResearchAsync(
        IReadOnlyList<IndexedAsset> assets,
        CancellationToken cancellationToken)
    {
        List<GameDataDocument> documents = [];
        GameDataFormatRegistry registry = new();
        foreach (IndexedAsset asset in assets.Where(asset => asset.Kind == AssetKind.GameData))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await File.ReadAllBytesAsync(asset.FullPath, cancellationToken);
            documents.Add(registry.Parse(asset.FileName, bytes));
        }

        missionResearch = missionEvidenceService.Build(SelectedProfile.Id, documents);
        new MissionDependencyContributor(dependencyGraph).Update(missionResearch);
        MissionHandlers.Clear();
        foreach (NativeHandlerEvidence handler in missionResearch.Handlers)
        {
            MissionHandlers.Add(handler);
        }
        MissionHandlerFilters.Clear();
        MissionHandlerFilters.Add("All");
        foreach (NativeHandlerEvidence handler in missionResearch.Handlers.OrderBy(value => value.DisplayName, StringComparer.Ordinal))
        {
            MissionHandlerFilters.Add(handler.Id);
        }
        if (!MissionHandlerFilters.Contains(MissionHandlerFilter, StringComparer.Ordinal))
        {
            missionHandlerFilter = "All";
            OnPropertyChanged(nameof(MissionHandlerFilter));
        }
        ApplyMissionFilters();
        ((AsyncRelayCommand)ExportMissionResearchCommand).RaiseCanExecuteChanged();
    }

    private void ApplyMissionFilters()
    {
        string? selectedId = SelectedMission?.Id;
        Missions.Clear();
        if (missionResearch is not null)
        {
            MissionEvidenceFilter filter = new(
                MissionSearchText,
                Enum.TryParse(MissionKindFilter, out MissionEvidenceKind kind) ? kind : null,
                Enum.TryParse(MissionConfidenceFilter, out MissionEvidenceConfidence confidence) ? confidence : null,
                MissionHandlerFilter == "All" ? null : MissionHandlerFilter);
            foreach (MissionEvidence mission in missionQueryService.Filter(missionResearch, filter))
            {
                Missions.Add(mission);
            }
        }
        SelectedMission = selectedId is null
            ? Missions.FirstOrDefault()
            : Missions.FirstOrDefault(mission => mission.Id == selectedId) ?? Missions.FirstOrDefault();
        OnPropertyChanged(nameof(MissionSummary));
    }

    private void OpenSelectedMissionDependencies()
    {
        if (SelectedMission is null || missionResearch is null)
        {
            return;
        }
        DependencyNodeId id = new($"{missionResearch.ProfileId}|mission|{SelectedMission.Id}");
        if (!dependencyGraph.TryGetNode(id, out DependencyNode? node) || node is null)
        {
            return;
        }
        SelectedDependencyNode = node;
        _ = documentManager.Add(new DependencyGraphDocumentViewModel(
            dependencyGraph,
            node,
            OpenDependencyNodeAsync,
            ExportDependencyReportAsync));
    }

    private async Task ExportMissionResearchAsync()
    {
        if (missionResearch is null)
        {
            return;
        }
        try
        {
            string? start = Workspace is null
                ? null
                : workspaceService.ResolveModPath(Workspace, Workspace.OutputRoot);
            string? destination = await dialogs.SaveFileAsync(
                "Export mission research evidence",
                $"mission-evidence-{missionResearch.ProfileId}.json",
                ".json",
                start);
            if (string.IsNullOrWhiteSpace(destination))
            {
                return;
            }
            destination = PathPolicy.ValidateExportDestination(destination, Workspace?.GameAssetRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporary, missionResearch.ExportJson());
            File.Move(temporary, destination, overwrite: true);
            outputService.Write(OutputLevel.Information, "Mission research",
                $"Mission evidence report written to {Path.GetFileName(destination)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            outputService.Write(OutputLevel.Error, "Mission research", exception.Message);
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                "Mission evidence",
                null,
                "Mission research",
                exception.Message,
                null,
                "Export",
                "Choose a writable destination outside the immutable game root."));
        }
    }

    private async Task CompareMissionSavesAsync()
    {
        IReadOnlyList<string> paths = await dialogs.PickAssetFilesAsync("Select two private save snapshots to compare");
        if (paths.Count != 2)
        {
            outputService.Write(OutputLevel.Warning, "Mission research", "Select exactly two equal-length save snapshots.");
            return;
        }

        try
        {
            byte[] before = await File.ReadAllBytesAsync(paths[0]);
            byte[] after = await File.ReadAllBytesAsync(paths[1]);
            IReadOnlyList<SaveDifferenceRange> ranges = SaveStateDiffer.Compare(before, after);
            MissionSaveDifferences.Clear();
            foreach (SaveDifferenceRange range in ranges.Take(10_000))
            {
                MissionSaveDifferences.Add(new SaveDifferenceRow(
                    $"0x{range.Offset:X8}", range.Length,
                    Convert.ToHexString(range.Before.AsSpan(0, Math.Min(16, range.Before.Length))),
                    Convert.ToHexString(range.After.AsSpan(0, Math.Min(16, range.After.Length)))));
            }
            outputService.Write(OutputLevel.Information, "Mission research",
                $"Private save differential found {ranges.Count:N0} changed byte range(s); no semantics were inferred or persisted.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problemService.Add(new ProblemEntry(ProblemSeverity.Warning, "Private save comparison", null,
                "Mission research", exception.Message, null, null,
                "Use two snapshots from the same game/profile with identical file length."));
        }
    }

    private void OpenSelectedMission()
    {
        if (SelectedMission is null || missionResearch is null)
        {
            return;
        }

        SelectedDocument = documentManager.Add(new MissionDocumentViewModel(
            SelectedMission,
            missionResearch,
            dependencyGraph));
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
        WorkspaceDefinition? contextWorkspace = inspectionAssets.Any(candidate =>
                string.Equals(candidate.StableKey, asset.StableKey, StringComparison.OrdinalIgnoreCase))
            ? inspectionWorkspace
            : Workspace ?? inspectionWorkspace;
        if (contextWorkspace is null)
        {
            return;
        }

        try
        {
            IDocument document = await documentManager.OpenAsync(asset, contextWorkspace);
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
        if (keep is not null)
        {
            _ = documentManager.CloseOthers(keep);
        }
    }

    private bool CanCloseDocumentsToRight(object? parameter)
    {
        IDocument? document = parameter as IDocument ?? SelectedDocument;
        int index = document is null ? -1 : Documents.IndexOf(document);
        return index >= 0 && index < Documents.Count - 1;
    }

    private void CloseDocumentsToRight(object? parameter)
    {
        IDocument? keep = parameter as IDocument ?? SelectedDocument;
        int index = keep is null ? -1 : Documents.IndexOf(keep);
        if (index < 0)
        {
            return;
        }

        _ = documentManager.CloseToRight(keep!);
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
            await RefreshChangesAsync();
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
            await RefreshChangesAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ReportStagingError(source, exception);
        }
    }

    private void AttachActiveUndoableDocument(IUndoableDocument? document)
    {
        if (activeUndoableDocument is not null)
        {
            activeUndoableDocument.UndoCommand.CanExecuteChanged -= OnActiveUndoCommandStateChanged;
            activeUndoableDocument.RedoCommand.CanExecuteChanged -= OnActiveUndoCommandStateChanged;
        }

        activeUndoableDocument = document;
        if (activeUndoableDocument is not null)
        {
            activeUndoableDocument.UndoCommand.CanExecuteChanged += OnActiveUndoCommandStateChanged;
            activeUndoableDocument.RedoCommand.CanExecuteChanged += OnActiveUndoCommandStateChanged;
        }
    }

    private void OnActiveUndoCommandStateChanged(object? sender, EventArgs eventArgs)
    {
        ((RelayCommand)UndoActiveDocumentCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoActiveDocumentCommand).RaiseCanExecuteChanged();
    }

    private async Task RefreshChangesAsync()
    {
        Changes.Clear();
        ChangeIssues.Clear();
        if (Workspace is null)
        {
            return;
        }

        try
        {
            ModValidationResult result = await modBuildService.ValidateAsync(Workspace);
            foreach (ModManifestAsset asset in result.Assets)
            {
                Changes.Add(asset);
            }

            foreach (ModValidationIssue issue in result.Issues)
            {
                ChangeIssues.Add(issue);
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            outputService.Write(OutputLevel.Error, "Changes", exception.Message);
        }
    }

    private async Task ValidateModAsync()
    {
        if (Workspace is null)
        {
            return;
        }

        ModValidationResult result = await modBuildService.ValidateAsync(Workspace);
        await RefreshChangesAsync();
        foreach (ModValidationIssue issue in result.Issues)
        {
            OutputLevel level = issue.Severity switch
            {
                ModValidationSeverity.Error => OutputLevel.Error,
                ModValidationSeverity.Warning => OutputLevel.Warning,
                _ => OutputLevel.Information,
            };
            outputService.Write(level, "Validate Mod", $"{issue.Target ?? "Mod"}: {issue.Message}");
        }

        StatusMessage = result.IsValid
            ? $"Mod valid: {result.Assets.Count} replacement(s) ready."
            : "Mod validation failed; review Changes and Problems.";
        ActiveActivity = "Changes";
    }

    private async Task BuildModAsync()
    {
        if (Workspace is null)
        {
            return;
        }

        try
        {
            ModBuildResult result = await modBuildService.BuildAsync(Workspace);
            outputService.Write(
                OutputLevel.Information,
                "Build Mod",
                $"Deterministic build complete: {result.OutputDirectory} " +
                $"({result.Report.Assets.Count} assets, content {result.Report.ContentSha256[..12]}…).");
            StatusMessage = $"Mod built: {result.Report.Assets.Count} asset(s).";
            dialogs.RevealInExplorer(result.OutputDirectory);
            await RefreshChangesAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            outputService.Write(OutputLevel.Error, "Build Mod", exception.Message);
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Workspace.Name,
                Workspace.FilePath,
                "Mod build",
                exception.Message,
                null,
                "manifest",
                "Resolve validation errors before building."));
            StatusMessage = "Mod build failed.";
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

        IndexedAsset? asset = gameAssets.Concat(modAssets).Concat(inspectionAssets).FirstOrDefault(
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

    private void StartTutorial(object? parameter)
    {
        TutorialDefinition tutorial = parameter switch
        {
            TutorialDefinition definition => definition,
            string id => TutorialCatalog.Resolve(id),
            _ => TutorialCatalog.QuickInspect,
        };
        applicationState.TutorialProgress.TryGetValue(tutorial.Id, out int restoredStep);
        tutorialSession.Start(tutorial, restoredStep);
        outputService.Write(
            OutputLevel.Information,
            "Tutorial",
            $"Started '{tutorial.Title}' at step {tutorialSession.StepIndex + 1}.");
        RaiseTutorialState();
    }

    private void TutorialNext()
    {
        if (tutorialSession.ActiveTutorial is null)
        {
            return;
        }

        string id = tutorialSession.ActiveTutorial.Id;
        if (tutorialSession.IsLastStep)
        {
            applicationState.TutorialProgress[id] = 0;
            outputService.Write(OutputLevel.Information, "Tutorial", "Tutorial completed.");
            tutorialSession.Stop();
        }
        else
        {
            tutorialSession.Next();
            applicationState.TutorialProgress[id] = tutorialSession.StepIndex;
        }

        RaiseTutorialState();
    }

    private void TutorialBack()
    {
        if (tutorialSession.Back() && tutorialSession.ActiveTutorial is not null)
        {
            applicationState.TutorialProgress[tutorialSession.ActiveTutorial.Id] = tutorialSession.StepIndex;
        }

        RaiseTutorialState();
    }

    private void TutorialRestart()
    {
        tutorialSession.Restart();
        if (tutorialSession.ActiveTutorial is not null)
        {
            applicationState.TutorialProgress[tutorialSession.ActiveTutorial.Id] = 0;
        }

        RaiseTutorialState();
    }

    private void TutorialSkip()
    {
        if (tutorialSession.ActiveTutorial is not null)
        {
            applicationState.TutorialProgress[tutorialSession.ActiveTutorial.Id] = tutorialSession.StepIndex;
            outputService.Write(OutputLevel.Information, "Tutorial", "Tutorial dismissed; progress was retained.");
        }

        tutorialSession.Stop();
        RaiseTutorialState();
    }

    private void RaiseTutorialState()
    {
        OnPropertyChanged(nameof(TutorialVisible));
        OnPropertyChanged(nameof(TutorialTitle));
        OnPropertyChanged(nameof(TutorialStepTitle));
        OnPropertyChanged(nameof(TutorialInstruction));
        OnPropertyChanged(nameof(TutorialTarget));
        OnPropertyChanged(nameof(TutorialProgress));
        foreach (System.Windows.Input.ICommand command in new[]
        {
            TutorialNextCommand,
            TutorialBackCommand,
            TutorialRestartCommand,
            TutorialSkipCommand,
        })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
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
            "Language" => AssetKind.Language,
            "BIN" => AssetKind.GameData,
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
            gameAssets.Concat(modAssets).Concat(inspectionAssets),
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
        foreach (string value in gameAssets.Concat(modAssets).Concat(inspectionAssets)
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

    private async Task ImportModelAsync()
    {
        IReadOnlyList<string> selected = await dialogs.PickAssetFilesAsync(
            "Import AEM, glTF, GLB, or OBJ submeshes into Authoring Studio");
        string[] sources = selected.Where(path => Path.GetExtension(path).Equals(".aem", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".obj", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            return;
        }

        try
        {
            StatusMessage = $"Importing {sources.Length} source model(s) into AEM Authoring Studio…";
            AemAuthoringProject project = await Task.Run(() =>
            {
                AemAuthoringProject created = new(
                    Path.GetFileNameWithoutExtension(sources[0]),
                    AemVersion.V4,
                    ProfileCatalog.Pc1X.Id);
                AemAuthoringDocumentViewModel.AddSources(created, sources, ProfileCatalog.Pc1X);

                if (created.Current.Submeshes.Count == 0)
                {
                    throw new InvalidDataException("The selected files did not contain any representable submeshes.");
                }

                return created;
            });
            WorkspaceDefinition context = GetAuthoringWorkspace();
            SelectedDocument = documentManager.Add(new AemAuthoringDocumentViewModel(
                project, context, dialogs, outputService, problemService, relationshipService, workspaceService));
            outputService.Write(
                OutputLevel.Information,
                "Model import",
                $"Opened {project.Current.Submeshes.Count:N0} imported submeshes in the operation-based Authoring Studio.");
            StatusMessage = "AEM Authoring Studio opened";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            problemService.Add(new ProblemEntry(
                ProblemSeverity.Error,
                Path.GetFileName(sources[0]),
                sources[0],
                "Model import",
                exception.Message,
                null,
                "glTF/OBJ conversion",
                "Use triangle topology, 16-bit representable counts, and supported vertex channels."));
            outputService.Write(OutputLevel.Error, "Model import", exception.Message);
            StatusMessage = "AEM composition failed validation";
        }
    }

    private async Task CreateNewAemAsync(object? parameter)
    {
        AemVersion initialVersion = string.Equals(parameter as string, "v5", StringComparison.OrdinalIgnoreCase)
            ? AemVersion.V5
            : AemVersion.V4;
        NewAemProjectOptions? options = await dialogs.PickNewAemProjectAsync(initialVersion);
        if (options is null)
        {
            return;
        }

        AemAuthoringProject project = new(options.Name, options.Version, ProfileCatalog.Pc1X.Id);
        AemAuthoringTemplateFactory.Populate(project, options.Template);
        AemAuthoringDocumentViewModel document = new(
            project, GetAuthoringWorkspace(), dialogs, outputService, problemService, relationshipService, workspaceService);
        document.OutputRelativePath = options.OutputRelativePath;
        SelectedDocument = documentManager.Add(document);
        StatusMessage = $"New PC AEM v{(int)options.Version} {options.Template} authoring project";
    }

    private void OpenSyntheticAemTemplate(AemAuthoringTemplate template)
    {
        string name = "synthetic_" + template.ToString().ToLowerInvariant();
        AemAuthoringProject project = new(name, AemVersion.V4, ProfileCatalog.Pc1X.Id);
        AemAuthoringTemplateFactory.Populate(project, template);
        AemAuthoringDocumentViewModel document = new(
            project, GetAuthoringWorkspace(), dialogs, outputService, problemService, relationshipService, workspaceService)
        {
            OutputRelativePath = $"assets/main/3d/meshes/{name}.aem",
        };
        SelectedDocument = documentManager.Add(document);
        StatusMessage = $"Synthetic {template} AEM authoring smoke document opened";
    }

    private WorkspaceDefinition GetAuthoringWorkspace()
    {
        if (Workspace is not null)
        {
            return Workspace;
        }

        inspectionWorkspace ??= new WorkspaceDefinition
        {
            Name = "Temporary AEM Authoring",
            ProfileId = ProfileCatalog.Pc1X.Id,
            ModRoot = ".",
            FilePath = Path.Combine(Path.GetTempPath(), "gof2-workshop-authoring", "temporary.gof2workspace"),
        };
        return inspectionWorkspace;
    }

    private async Task CheckBlenderIntegrationAsync()
    {
        string[] candidates =
        [
            Environment.GetEnvironmentVariable("GOF2_WORKSHOP_BLENDER") ?? string.Empty,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Blender Foundation", "Blender 5.1", "blender.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Blender Foundation", "Blender 5.0", "blender.exe"),
        ];
        string? executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            StatusMessage = "Blender was not detected";
            outputService.Write(
                OutputLevel.Warning,
                "Blender",
                "Blender was not detected. Set GOF2_WORKSHOP_BLENDER to its executable path.");
            return;
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        string version = await process.StandardOutput.ReadLineAsync() ?? "Blender (version unavailable)";
        await process.WaitForExitAsync();
        string addOn = Path.Combine(Environment.CurrentDirectory, "tools", "blender", "gof2_workshop", "__init__.py");
        outputService.Write(
            process.ExitCode == 0 ? OutputLevel.Information : OutputLevel.Error,
            "Blender",
            $"{version}; executable: {executable}; Workshop add-on source: " +
            (File.Exists(addOn) ? addOn : "not found"));
        StatusMessage = process.ExitCode == 0
            ? $"{version} detected"
            : $"Blender validation exited with code {process.ExitCode}";
    }

    private void AddRecentStandaloneFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        applicationState.RecentStandaloneFiles.RemoveAll(
            value => string.Equals(value, fullPath, StringComparison.OrdinalIgnoreCase));
        applicationState.RecentStandaloneFiles.Insert(0, fullPath);
        if (applicationState.RecentStandaloneFiles.Count > 20)
        {
            applicationState.RecentStandaloneFiles.RemoveRange(
                20,
                applicationState.RecentStandaloneFiles.Count - 20);
        }
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
            CreateWorkspaceFromInspectionCommand,
            SelectGameFolderCommand,
            RescanCommand,
            CancelScanCommand,
            OpenAssetCommand,
            CloseDocumentCommand,
            CloseOtherDocumentsCommand,
            CloseDocumentsToRightCommand,
            CloseAllDocumentsCommand,
            UndoActiveDocumentCommand,
            RedoActiveDocumentCommand,
            ExportCurrentCommand,
            RevealCommand,
            AddToModCommand,
            ReplaceInModCommand,
            ValidateModCommand,
            BuildModCommand,
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

    private bool CanExecuteActiveDocumentCommand(bool undo)
    {
        if (SelectedDocument is not IUndoableDocument document)
        {
            return false;
        }

        System.Windows.Input.ICommand command = undo
            ? document.UndoCommand
            : document.RedoCommand;
        return command.CanExecute(null);
    }

    private void ExecuteActiveDocumentCommand(bool undo)
    {
        if (SelectedDocument is not IUndoableDocument document)
        {
            return;
        }

        System.Windows.Input.ICommand command = undo
            ? document.UndoCommand
            : document.RedoCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            RaiseCommandStates();
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

    private static List<string> GetOpenPaths(IReadOnlyList<string> arguments)
    {
        List<string> paths = [];
        HashSet<string> optionsWithValues = new(StringComparer.OrdinalIgnoreCase)
        {
            "--workspace",
            "--asset-root",
            "--profile",
            "--tutorial",
            "--new-aem-template",
        };
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--open", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Count)
            {
                paths.Add(arguments[++index]);
            }
            else if (optionsWithValues.Contains(argument) && index + 1 < arguments.Count)
            {
                index++;
            }
            else if (!argument.StartsWith("--", StringComparison.Ordinal)
                && (File.Exists(argument) || Directory.Exists(argument)))
            {
                paths.Add(argument);
            }
        }

        return paths;
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
        AttachActiveUndoableDocument(null);
        documentManager.Changed -= OnDocumentsChanged;
        problemService.Changed -= OnProblemsChanged;
        outputService.Changed -= OnOutputChanged;
        relationshipService.Changed -= OnMaterialRelationshipsChanged;
        documentManager.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
