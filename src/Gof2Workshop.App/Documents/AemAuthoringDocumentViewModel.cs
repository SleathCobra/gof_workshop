using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.App.Rendering;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Import;
using Gof2Workshop.Scene;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public sealed record AemAuthoringSubmeshRow(
    string StableId,
    string Name,
    int Index,
    int Vertices,
    int Triangles,
    string Material,
    bool Hidden,
    bool Locked)
{
    public string Label => $"{Index:D2} · {Name}";

    public string Detail => $"{Vertices:N0} vertices · {Triangles:N0} triangles · {Material}";
}

public sealed record AemAuthoringTrackRow(
    AemAnimationChannel Channel,
    int KeyCount,
    string Label);

public sealed record AemAuthoringKeyRow(
    AemAnimationChannel Channel,
    int Index,
    float Time,
    float Value)
{
    public string Label => $"{Index:D3} · {Time:F4}s · {Value:R}";
}

public sealed record AemAuthoringValidationRow(
    string Severity,
    string Area,
    string Message);

public sealed record AemImportPreflightRow(
    string Source,
    string Name,
    string Status,
    string Detail);

public sealed class AemAuthoringDocumentViewModel :
    DocumentViewModelBase,
    IUndoableDocument,
    IExportableDocument
{
    private readonly AemAuthoringProject project;
    private readonly WorkspaceDefinition workspace;
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;
    private readonly IAssetRelationshipService relationships;
    private readonly IWorkspaceService workspaceService;
    private CancellationTokenSource? previewCancellation;
    private AemAuthoringSubmeshRow? selectedSubmesh;
    private readonly List<string> selectedStableIds = [];
    private AemAuthoringTrackRow? selectedTrack;
    private AemAuthoringKeyRow? selectedKey;
    private AemDocumentViewModel? previewDocument;
    private string editName = string.Empty;
    private float pivotX;
    private float pivotY;
    private float pivotZ;
    private float keyTime;
    private float keyValue;
    private AemAnimationChannel selectedChannel = AemAnimationChannel.TranslationX;
    private float translateX;
    private float translateY;
    private float translateZ;
    private float rotateX;
    private float rotateY;
    private float rotateZ;
    private float scaleX = 1;
    private float scaleY = 1;
    private float scaleZ = 1;
    private int animationSourceSubmeshIndex;
    private bool mergeImportedAnimation;
    private float importScale = 1;
    private bool importGenerateNormals = true;
    private bool importCenterPivots;
    private bool importReverseWinding;
    private bool importFlipV;
    private bool importWeldVertices;
    private bool importRemoveDegenerates = true;
    private bool importAnimations = true;
    private bool importMaterials = true;
    private string outputRelativePath;
    private bool isBusy;
    private string status = "Empty authoring project";

    public AemAuthoringDocumentViewModel(
        AemAuthoringProject project,
        WorkspaceDefinition workspace,
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems,
        IAssetRelationshipService relationships,
        IWorkspaceService workspaceService)
        : base(
            $"aem-authoring:{Guid.NewGuid():N}",
            project?.Current.Name ?? throw new ArgumentNullException(nameof(project)),
            "AEM Authoring",
            null,
            isReadOnly: false)
    {
        this.project = project;
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.problems = problems ?? throw new ArgumentNullException(nameof(problems));
        this.relationships = relationships ?? throw new ArgumentNullException(nameof(relationships));
        this.workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        outputRelativePath = $"assets/main/3d/meshes/{project.Current.Name}.aem";

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        DuplicateCommand = new RelayCommand(DuplicateSelected, HasUnlockedSelection);
        DeleteCommand = new RelayCommand(DeleteSelected, HasUnlockedSelection);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanMove(1));
        RenameCommand = new RelayCommand(RenameSelected, HasUnlockedSelection);
        ToggleHiddenCommand = new RelayCommand(ToggleHidden, () => SelectedSubmesh is not null);
        ToggleLockedCommand = new RelayCommand(ToggleLocked, () => SelectedSubmesh is not null);
        ApplyPivotCommand = new RelayCommand(ApplyPivot, HasUnlockedSelection);
        CenterPivotCommand = new RelayCommand(CenterPivot, HasUnlockedSelection);
        RecalculateBoundsCommand = new RelayCommand(RecalculateBounds, HasUnlockedSelection);
        NormalizeNormalsCommand = new RelayCommand(NormalizeNormals, HasUnlockedSelection);
        ReverseWindingCommand = new RelayCommand(ReverseWinding, HasUnlockedSelection);
        RemoveDegeneratesCommand = new RelayCommand(RemoveDegenerates, HasUnlockedSelection);
        WeldVerticesCommand = new RelayCommand(WeldVertices, HasUnlockedSelection);
        ApplyTransformCommand = new RelayCommand(ApplyTransform, HasUnlockedSelection);
        AssignTextureCommand = new AsyncRelayCommand(AssignTextureAsync, HasUnlockedSelection);
        AddKeyCommand = new RelayCommand(AddKey, HasUnlockedSelection);
        DeleteTrackCommand = new RelayCommand(DeleteTrack, () => HasUnlockedSelection() && SelectedTrack is not null);
        UpdateKeyCommand = new RelayCommand(UpdateSelectedKey, () => HasUnlockedSelection() && SelectedKey is not null);
        DeleteKeyCommand = new RelayCommand(DeleteSelectedKey, () => HasUnlockedSelection() && SelectedKey is not null);
        DuplicateKeyCommand = new RelayCommand(DuplicateSelectedKey, () => HasUnlockedSelection() && SelectedKey is not null);
        ImportAnimationCommand = new AsyncRelayCommand(ImportAnimationAsync, HasUnlockedSelection);
        UndoCommand = new RelayCommand(Undo, () => project.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => project.CanRedo);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => project.Current.Submeshes.Count > 0 && !IsBusy);
        SaveCommand = new AsyncRelayCommand(ExportDefaultAsync, () => project.Current.Submeshes.Count > 0 && !IsBusy);
        StageCommand = new AsyncRelayCommand(StageAsync, () => project.Current.Submeshes.Count > 0 && !IsBusy && workspace.GameAssetRoot is not null);
        ExportGltfCommand = new AsyncRelayCommand(() => ExportSceneAsync("gltf"), () => project.Current.Submeshes.Count > 0 && !IsBusy);
        ExportObjCommand = new AsyncRelayCommand(() => ExportSceneAsync("obj"), () => project.Current.Submeshes.Count > 0 && !IsBusy);
        OpenBlenderCommand = new AsyncRelayCommand(OpenBlenderAsync);
        RefreshRows();
        if (project.Current.Submeshes.Count > 0)
        {
            _ = RefreshPreviewAsync();
        }
    }

    public ObservableCollection<AemAuthoringSubmeshRow> Submeshes { get; } = [];

    public ObservableCollection<AemAuthoringTrackRow> Tracks { get; } = [];

    public ObservableCollection<AemAuthoringKeyRow> Keys { get; } = [];

    public ObservableCollection<AemAuthoringValidationRow> Validation { get; } = [];

    public ObservableCollection<AemImportPreflightRow> ImportPreflight { get; } = [];

    public IReadOnlyList<AemAnimationChannel> Channels { get; } =
    [
        AemAnimationChannel.TranslationX, AemAnimationChannel.TranslationY, AemAnimationChannel.TranslationZ,
        AemAnimationChannel.RotationX, AemAnimationChannel.RotationY, AemAnimationChannel.RotationZ,
        AemAnimationChannel.ScaleX, AemAnimationChannel.ScaleY, AemAnimationChannel.ScaleZ,
    ];

    public AemAuthoringSubmeshRow? SelectedSubmesh
    {
        get => selectedSubmesh;
        set
        {
            if (SetProperty(ref selectedSubmesh, value))
            {
                AemAuthoringSubmesh? model = CurrentSelected();
                EditName = model?.Name ?? string.Empty;
                PivotX = model?.Pivot.X ?? 0;
                PivotY = model?.Pivot.Y ?? 0;
                PivotZ = model?.Pivot.Z ?? 0;
                RefreshTracks();
                RaiseCommandStates();
                RaiseInspectorChanged();
            }
        }
    }

    public AemAuthoringTrackRow? SelectedTrack
    {
        get => selectedTrack;
        set
        {
            if (SetProperty(ref selectedTrack, value))
            {
                RefreshKeys();
                ((RelayCommand)DeleteTrackCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public AemAuthoringKeyRow? SelectedKey
    {
        get => selectedKey;
        set
        {
            if (SetProperty(ref selectedKey, value))
            {
                if (value is not null)
                {
                    KeyTime = value.Time;
                    KeyValue = value.Value;
                }
                ((RelayCommand)UpdateKeyCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteKeyCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DuplicateKeyCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public AemDocumentViewModel? PreviewDocument
    {
        get => previewDocument;
        private set
        {
            if (ReferenceEquals(previewDocument, value))
            {
                return;
            }

            AemDocumentViewModel? old = previewDocument;
            if (SetProperty(ref previewDocument, value))
            {
                old?.Dispose();
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(HasNoPreview));
            }
        }
    }

    public bool HasPreview => PreviewDocument is not null;

    public bool HasNoPreview => PreviewDocument is null;

    public string EditName { get => editName; set => SetProperty(ref editName, value); }

    public float PivotX { get => pivotX; set => SetProperty(ref pivotX, value); }

    public float PivotY { get => pivotY; set => SetProperty(ref pivotY, value); }

    public float PivotZ { get => pivotZ; set => SetProperty(ref pivotZ, value); }

    public float KeyTime { get => keyTime; set => SetProperty(ref keyTime, value); }

    public float KeyValue { get => keyValue; set => SetProperty(ref keyValue, value); }

    public AemAnimationChannel SelectedChannel { get => selectedChannel; set => SetProperty(ref selectedChannel, value); }

    public float TranslateX { get => translateX; set => SetProperty(ref translateX, value); }
    public float TranslateY { get => translateY; set => SetProperty(ref translateY, value); }
    public float TranslateZ { get => translateZ; set => SetProperty(ref translateZ, value); }
    public float RotateX { get => rotateX; set => SetProperty(ref rotateX, value); }
    public float RotateY { get => rotateY; set => SetProperty(ref rotateY, value); }
    public float RotateZ { get => rotateZ; set => SetProperty(ref rotateZ, value); }
    public float ScaleX { get => scaleX; set => SetProperty(ref scaleX, value); }
    public float ScaleY { get => scaleY; set => SetProperty(ref scaleY, value); }
    public float ScaleZ { get => scaleZ; set => SetProperty(ref scaleZ, value); }
    public int AnimationSourceSubmeshIndex { get => animationSourceSubmeshIndex; set => SetProperty(ref animationSourceSubmeshIndex, value); }
    public bool MergeImportedAnimation { get => mergeImportedAnimation; set => SetProperty(ref mergeImportedAnimation, value); }
    public float ImportScale { get => importScale; set => SetProperty(ref importScale, value); }
    public bool ImportGenerateNormals { get => importGenerateNormals; set => SetProperty(ref importGenerateNormals, value); }
    public bool ImportCenterPivots { get => importCenterPivots; set => SetProperty(ref importCenterPivots, value); }
    public bool ImportReverseWinding { get => importReverseWinding; set => SetProperty(ref importReverseWinding, value); }
    public bool ImportFlipV { get => importFlipV; set => SetProperty(ref importFlipV, value); }
    public bool ImportWeldVertices { get => importWeldVertices; set => SetProperty(ref importWeldVertices, value); }
    public bool ImportRemoveDegenerates { get => importRemoveDegenerates; set => SetProperty(ref importRemoveDegenerates, value); }
    public bool ImportAnimations { get => importAnimations; set => SetProperty(ref importAnimations, value); }
    public bool ImportMaterials { get => importMaterials; set => SetProperty(ref importMaterials, value); }
    public string OutputRelativePath { get => outputRelativePath; set => SetProperty(ref outputRelativePath, value); }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string Status { get => status; private set => SetProperty(ref status, value); }

    public string ImportPreflightSummary => ImportPreflight.Count == 0
        ? "No source has been inspected in this session."
        : $"{ImportPreflight.Count:N0} imported primitive(s) inspected; red entries require correction.";

    public string ProjectSummary =>
        $"{project.Current.TargetProfile} · AEM v{(int)project.Current.Version} · " +
        $"{project.Current.Submeshes.Count:N0} submeshes · " +
        $"{project.Current.Submeshes.Sum(value => value.Geometry.Positions.Length):N0} vertices · " +
        $"{project.AppliedOperations.Count:N0} operations";

    public string SelectionSummary => selectedStableIds.Count switch
    {
        0 => "No submesh selected",
        1 => "1 submesh selected",
        _ => $"{selectedStableIds.Count:N0} submeshes selected",
    };

    public System.Windows.Input.ICommand ImportCommand { get; }
    public System.Windows.Input.ICommand DuplicateCommand { get; }
    public System.Windows.Input.ICommand DeleteCommand { get; }
    public System.Windows.Input.ICommand MoveUpCommand { get; }
    public System.Windows.Input.ICommand MoveDownCommand { get; }
    public System.Windows.Input.ICommand RenameCommand { get; }
    public System.Windows.Input.ICommand ToggleHiddenCommand { get; }
    public System.Windows.Input.ICommand ToggleLockedCommand { get; }
    public System.Windows.Input.ICommand ApplyPivotCommand { get; }
    public System.Windows.Input.ICommand CenterPivotCommand { get; }
    public System.Windows.Input.ICommand RecalculateBoundsCommand { get; }
    public System.Windows.Input.ICommand NormalizeNormalsCommand { get; }
    public System.Windows.Input.ICommand ReverseWindingCommand { get; }
    public System.Windows.Input.ICommand RemoveDegeneratesCommand { get; }
    public System.Windows.Input.ICommand WeldVerticesCommand { get; }
    public System.Windows.Input.ICommand ApplyTransformCommand { get; }
    public System.Windows.Input.ICommand AssignTextureCommand { get; }
    public System.Windows.Input.ICommand AddKeyCommand { get; }
    public System.Windows.Input.ICommand DeleteTrackCommand { get; }
    public System.Windows.Input.ICommand UpdateKeyCommand { get; }
    public System.Windows.Input.ICommand DeleteKeyCommand { get; }
    public System.Windows.Input.ICommand DuplicateKeyCommand { get; }
    public System.Windows.Input.ICommand ImportAnimationCommand { get; }
    public System.Windows.Input.ICommand UndoCommand { get; }
    public System.Windows.Input.ICommand RedoCommand { get; }
    public System.Windows.Input.ICommand ValidateCommand { get; }
    public System.Windows.Input.ICommand SaveCommand { get; }
    public System.Windows.Input.ICommand StageCommand { get; }
    public System.Windows.Input.ICommand ExportGltfCommand { get; }
    public System.Windows.Input.ICommand ExportObjCommand { get; }
    public System.Windows.Input.ICommand OpenBlenderCommand { get; }

    public override IReadOnlyList<InspectorGroup> InspectorGroups
    {
        get
        {
            List<InspectorGroup> groups =
            [
                new("AEM target",
                [
                    new InspectorProperty("Profile", project.Current.TargetProfile),
                    new InspectorProperty("Version", $"v{(int)project.Current.Version}"),
                    new InspectorProperty("Submeshes", project.Current.Submeshes.Count.ToString(CultureInfo.InvariantCulture)),
                    new InspectorProperty("Writer", "Serialize → reparse → scene validate → preview"),
                ]),
            ];
            if (CurrentSelected() is { } selected)
            {
                groups.Add(new InspectorGroup("Selected submesh",
                [
                    new InspectorProperty("Stable ID", selected.StableId),
                    new InspectorProperty("Vertices", selected.Geometry.Positions.Length.ToString("N0", CultureInfo.InvariantCulture)),
                    new InspectorProperty("Triangles", (selected.Geometry.Indices.Length / 3).ToString("N0", CultureInfo.InvariantCulture)),
                    new InspectorProperty("Pivot", selected.Pivot.ToString()),
                    new InspectorProperty("Bounds", $"{selected.Bounds.Center}; r={selected.Bounds.Radius:R}"),
                    new InspectorProperty("Material", selected.MaterialAsset ?? "Unassigned"),
                ]));
            }

            return groups;
        }
    }

    public override string AssetDetails => string.Join(Environment.NewLine,
        project.AppliedOperations.Select((operation, index) => $"{index + 1}. {operation.Description}"));

    public async Task ExportDefaultAsync()
    {
        AemAuthoringResult result = await BuildAsync();
        string start = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        string? destination = await dialogs.SaveFileAsync("Save validated authored AEM", project.Current.Name + ".aem", ".aem", start);
        if (destination is null)
        {
            return;
        }

        if (workspace.GameAssetRoot is string gameRoot && PathPolicy.IsWithin(destination, gameRoot))
        {
            throw new InvalidOperationException("Authored AEM files cannot be written beneath the immutable game root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temporary, result.Bytes);
        File.Move(temporary, destination, overwrite: true);
        Status = $"Saved, reparsed and preview-validated {Path.GetFileName(destination)}";
        output.Write(OutputLevel.Information, "AEM Authoring", Status);
    }

    private async Task StageAsync()
    {
        AemAuthoringResult result = await BuildAsync();
        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        string validated = Path.Combine(modRoot, ".work", "validated");
        Directory.CreateDirectory(validated);
        string candidate = Path.Combine(validated, $"{Guid.NewGuid():N}.aem");
        await File.WriteAllBytesAsync(candidate, result.Bytes);
        try
        {
            ModStagingResult staged = await new ModStagingService(workspaceService).StageNewAssetAsync(
                workspace,
                OutputRelativePath,
                AssetKind.Aem,
                candidate,
                overwrite: true);
            Status = $"Validated new AEM staged at {Path.GetRelativePath(modRoot, staged.StagedPath)}";
            output.Write(OutputLevel.Information, "Changes", Status);
        }
        finally
        {
            File.Delete(candidate);
        }
    }

    private async Task ExportSceneAsync(string format)
    {
        string? directory = await dialogs.PickFolderAsync(
            format == "gltf" ? "Export authored glTF package" : "Export authored OBJ package",
            workspaceService.ResolveModPath(workspace, workspace.OutputRoot));
        if (directory is null)
        {
            return;
        }
        directory = PathPolicy.ValidateExportDestination(directory, workspace.GameAssetRoot);
        AemAuthoringResult result = await BuildAsync();
        if (format == "gltf")
        {
            List<GltfTextureAssignment> textures = [];
            for (int index = 0; index < project.Current.Submeshes.Count; index++)
            {
                string? material = project.Current.Submeshes[index].MaterialAsset;
                if (material is null || !File.Exists(material) || !Path.GetExtension(material).Equals(".aei", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                AeiFile file = new AeiParser().Parse(material, new AeiParserOptions(ProfileCatalog.Pc1X));
                RgbaImage image = new AeiTextureDecoder().DecodeAtlas(file);
                string key = Convert.ToHexStringLower(SHA256.HashData(file.Payload));
                textures.Add(new GltfTextureAssignment(index, key, Path.GetFileNameWithoutExtension(material), image, HasAlpha(image)));
            }
            GltfExportResult exported = new GltfExporter().ExportWithMaterials(
                result.Scene, directory, project.Current.Name, textures);
            Status = $"glTF exported · {exported.PrimitiveCount} primitives · {exported.TexturePaths?.Count ?? 0} textures";
        }
        else
        {
            ObjExportResult exported = new ObjExporter().Export(result.Scene, directory, project.Current.Name);
            Status = $"OBJ exported · {Path.GetFileName(exported.ObjPath)}";
        }
        output.Write(OutputLevel.Information, "AEM Authoring", Status);
    }

    private static bool HasAlpha(RgbaImage image)
    {
        ReadOnlySpan<byte> pixels = image.ReadOnlyPixelBytes;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != byte.MaxValue)
            {
                return true;
            }
        }
        return false;
    }

    private async Task OpenBlenderAsync()
    {
        try
        {
            string[] candidates =
            [
                Environment.GetEnvironmentVariable("GOF2_WORKSHOP_BLENDER") ?? string.Empty,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender 5.1", "blender.exe"),
            ];
            string? executable = candidates.FirstOrDefault(File.Exists);
            if (executable is null)
            {
                throw new FileNotFoundException("Blender was not detected. Set GOF2_WORKSHOP_BLENDER to its executable path.");
            }
            using Process version = new()
            {
                StartInfo = new ProcessStartInfo(executable, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                },
            };
            version.Start();
            string detected = await version.StandardOutput.ReadLineAsync() ?? "Blender";
            await version.WaitForExitAsync();
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            Status = detected + " launched · export a glTF package, edit it, then import it as geometry or animation.";
            output.Write(OutputLevel.Information, "Blender", Status);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status = exception.Message;
            output.Write(OutputLevel.Warning, "Blender", Status);
            problems.Add(new ProblemEntry(
                ProblemSeverity.Warning,
                project.Current.Name,
                null,
                "AEM Authoring",
                Status,
                null,
                "Blender integration",
                "Configure GOF2_WORKSHOP_BLENDER or install Blender 5.1."));
        }
    }

    private async Task ImportAsync()
    {
        IReadOnlyList<string> files = await dialogs.PickAssetFilesAsync("Import AEM, glTF, GLB, or OBJ submeshes");
        string[] supported = files.Where(path => Path.GetExtension(path).ToLowerInvariant() is ".aem" or ".gltf" or ".glb" or ".obj").ToArray();
        if (supported.Length == 0)
        {
            return;
        }

        IsBusy = true;
        int operationMark = project.AppliedOperations.Count;
        try
        {
            if (!float.IsFinite(ImportScale) || ImportScale <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ImportScale), "Import scale must be finite and greater than zero.");
            }
            string[] before = project.Current.Submeshes.Select(value => value.StableId).ToArray();
            IReadOnlyList<AemImportPreflightReport> reports = [];
            await Task.Run(() =>
            {
                reports = AddSources(project, supported, ProfileCatalog.Pc1X);
                string[] imported = project.Current.Submeshes
                    .Where(value => !before.Contains(value.StableId, StringComparer.Ordinal))
                    .Select(value => value.StableId)
                    .ToArray();
                foreach (string stableId in imported)
                {
                    AemAuthoringSubmesh value = project.Current.Submeshes.First(candidate => candidate.StableId == stableId);
                    if (ImportGenerateNormals)
                    {
                        project.NormalizeNormals(stableId);
                    }
                    if (ImportRemoveDegenerates)
                    {
                        project.RemoveDegenerateTriangles(stableId);
                    }
                    if (ImportWeldVertices)
                    {
                        project.WeldDuplicateVertices(stableId);
                    }
                    if (ImportReverseWinding)
                    {
                        project.ReverseWinding(stableId);
                    }
                    if (ImportFlipV && value.Geometry.TextureCoordinates is not null)
                    {
                        project.FlipTextureV(stableId);
                    }
                    if (ImportCenterPivots)
                    {
                        project.CenterPivot(stableId);
                    }
                    if (Math.Abs(ImportScale - 1) > 0.000001f)
                    {
                        project.TransformGeometry(stableId, Matrix4x4.CreateScale(ImportScale));
                    }
                    if (!ImportAnimations)
                    {
                        project.ClearAnimation(stableId);
                    }
                    if (!ImportMaterials)
                    {
                        project.AssignMaterial(stableId, null);
                    }
                }
            });
            ImportPreflight.Clear();
            foreach (AemImportPreflightReport report in reports)
            {
                foreach (AemImportPreflightPrimitive primitive in report.Primitives)
                {
                    string features = string.Join(", ", new[]
                    {
                        primitive.HasNormals ? "normals" : "no normals",
                        primitive.HasTextureCoordinates ? "UV0" : "no UV",
                        primitive.HasAuxiliaryFloat4 ? "aux float4" : "no aux",
                        primitive.Material,
                    });
                    ImportPreflight.Add(new AemImportPreflightRow(
                        report.SourceName,
                        primitive.Name,
                        primitive.IsRepresentable ? "Ready" : "Blocked",
                        $"{primitive.VertexCount:N0} vertices · {primitive.TriangleCount:N0} triangles · {features} · {primitive.Summary}"));
                }
                foreach (ModelImportDiagnostic diagnostic in report.Diagnostics)
                {
                    ImportPreflight.Add(new AemImportPreflightRow(
                        report.SourceName,
                        diagnostic.Code,
                        diagnostic.Severity.ToString(),
                        diagnostic.Message));
                }
            }
            OnPropertyChanged(nameof(ImportPreflightSummary));
            RefreshRows();
            await RefreshPreviewAsync();
            Status = $"Imported {supported.Length:N0} source file(s) with explicit conversion options";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            while (project.AppliedOperations.Count > operationMark && project.CanUndo)
            {
                project.Undo();
            }
            RefreshRows();
            ReportError(exception, "Use triangle meshes within the AEM 16-bit limits and review unsupported import channels.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static IReadOnlyList<AemImportPreflightReport> AddSources(
        AemAuthoringProject target,
        IEnumerable<string> paths,
        AssetPlatformProfile profile)
    {
        AemImportPreflightService preflight = new();
        List<AemImportPreflightReport> reports = [];
        foreach (string path in paths)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".aem":
                    AemFile aem = new AemParser().Parse(path, new AemParserOptions(profile));
                    reports.Add(preflight.Inspect(aem, Path.GetFileName(path)));
                    target.AddFromAem(aem);
                    break;
                case ".gltf":
                case ".glb":
                    ImportedScene gltf = new GltfModelImporter().Import(path);
                    reports.Add(preflight.Inspect(gltf));
                    target.AddImportedScene(gltf);
                    break;
                case ".obj":
                    ImportedScene obj = new ObjModelImporter().Import(path);
                    reports.Add(preflight.Inspect(obj));
                    target.AddImportedScene(obj);
                    break;
            }
        }
        return reports;
    }

    public void SetSubmeshSelection(IEnumerable<AemAuthoringSubmeshRow> selection)
    {
        string[] ids = selection.Select(value => value.StableId).Distinct(StringComparer.Ordinal).ToArray();
        selectedStableIds.Clear();
        selectedStableIds.AddRange(ids);
        OnPropertyChanged(nameof(SelectionSummary));
        RaiseCommandStates();
    }

    public void MoveSubmesh(string stableId, int targetIndex)
    {
        if (project.Current.Submeshes.FirstOrDefault(value => value.StableId == stableId) is not { Locked: false })
        {
            return;
        }
        Apply(() => project.Move(stableId, targetIndex));
    }

    private void DuplicateSelected() => Apply(() =>
    {
        foreach (string stableId in SelectedIds())
        {
            project.Duplicate(stableId);
        }
    });

    private void DeleteSelected() => Apply(() =>
    {
        foreach (string stableId in SelectedIds()
                     .OrderByDescending(id => project.Current.Submeshes.ToList().FindIndex(value => value.StableId == id)))
        {
            project.Remove(stableId);
        }
    });
    private void RenameSelected() => Apply(() => project.Rename(SelectedSubmesh!.StableId, EditName));
    private void ToggleHidden() => Apply(() =>
    {
        AemAuthoringSubmesh[] selected = SelectedIds().Select(FindCurrent).ToArray();
        bool hidden = selected.Any(value => !value.Hidden);
        foreach (AemAuthoringSubmesh value in selected)
        {
            project.SetHidden(value.StableId, hidden);
        }
    });

    private void ToggleLocked() => Apply(() =>
    {
        AemAuthoringSubmesh[] selected = SelectedIds().Select(FindCurrent).ToArray();
        bool locked = selected.Any(value => !value.Locked);
        foreach (AemAuthoringSubmesh value in selected)
        {
            project.SetLocked(value.StableId, locked);
        }
    });
    private void ApplyPivot() => Apply(() => project.SetPivot(SelectedSubmesh!.StableId, new Vector3(PivotX, PivotY, PivotZ)));
    private void CenterPivot() => Apply(() => project.CenterPivot(SelectedSubmesh!.StableId));
    private void RecalculateBounds() => Apply(() => project.RecalculateBounds(SelectedSubmesh!.StableId));
    private void NormalizeNormals() => Apply(() => project.NormalizeNormals(SelectedSubmesh!.StableId));
    private void ReverseWinding() => Apply(() => project.ReverseWinding(SelectedSubmesh!.StableId));
    private void RemoveDegenerates() => Apply(() => project.RemoveDegenerateTriangles(SelectedSubmesh!.StableId));
    private void WeldVertices() => Apply(() => project.WeldDuplicateVertices(SelectedSubmesh!.StableId));

    private void ApplyTransform()
    {
        Matrix4x4 transform = Matrix4x4.CreateScale(ScaleX, ScaleY, ScaleZ) *
            Matrix4x4.CreateFromYawPitchRoll(RotateY, RotateX, RotateZ) *
            Matrix4x4.CreateTranslation(TranslateX, TranslateY, TranslateZ);
        Apply(() => project.TransformGeometry(SelectedSubmesh!.StableId, transform));
    }

    private void MoveSelected(int delta)
    {
        int index = SelectedSubmesh!.Index;
        Apply(() => project.Move(SelectedSubmesh.StableId, index + delta));
    }

    private async Task AssignTextureAsync()
    {
        string? path = await dialogs.PickAssetFileAsync("Assign preview/export AEI texture", ".aei");
        if (path is not null)
        {
            Apply(() => project.AssignMaterial(SelectedSubmesh!.StableId, path));
        }
    }

    private void AddKey() => Apply(() => project.AddKey(
        SelectedSubmesh!.StableId,
        SelectedChannel,
        new AemAuthoringKey(KeyTime, KeyValue)));

    private void DeleteTrack() => Apply(() => project.ReplaceTrack(
        SelectedSubmesh!.StableId,
        SelectedTrack!.Channel,
        []));

    private void UpdateSelectedKey() => Apply(() => project.UpdateKey(
        SelectedSubmesh!.StableId,
        SelectedKey!.Channel,
        SelectedKey.Index,
        new AemAuthoringKey(KeyTime, KeyValue)));

    private void DeleteSelectedKey() => Apply(() => project.DeleteKey(
        SelectedSubmesh!.StableId,
        SelectedKey!.Channel,
        SelectedKey.Index));

    private void DuplicateSelectedKey() => Apply(() => project.DuplicateKey(
        SelectedSubmesh!.StableId,
        SelectedKey!.Channel,
        SelectedKey.Index));

    private async Task ImportAnimationAsync()
    {
        string? path = await dialogs.PickAssetFileAsync("Import transform animation from AEM", ".aem");
        if (path is null)
        {
            return;
        }

        try
        {
            AemFile source = await Task.Run(() => new AemParser().Parse(path, new AemParserOptions(ProfileCatalog.Pc1X)));
            if ((uint)AnimationSourceSubmeshIndex >= (uint)source.Submeshes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(AnimationSourceSubmeshIndex),
                    $"Source AEM has {source.Submeshes.Count} submeshes; choose index 0..{source.Submeshes.Count - 1}.");
            }
            Apply(() => project.ImportAnimationFromAem(
                source.Submeshes[AnimationSourceSubmeshIndex],
                SelectedSubmesh!.StableId,
                Channels,
                MergeImportedAnimation));
            output.Write(OutputLevel.Information, "AEM Authoring",
                $"Imported confirmed transform channels from source submesh {AnimationSourceSubmeshIndex}; unresolved channels were not discarded from the source file.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            ReportError(exception, "Choose a valid source submesh and import only confirmed transform channels.");
        }
    }

    private void Undo()
    {
        if (project.Undo())
        {
            RefreshRows();
            _ = RefreshPreviewAsync();
        }
    }

    private void Redo()
    {
        if (project.Redo())
        {
            RefreshRows();
            _ = RefreshPreviewAsync();
        }
    }

    private async Task ValidateAsync()
    {
        try
        {
            AemAuthoringResult result = await BuildAsync();
            PopulateValidation(result);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
        {
            Validation.Clear();
            Validation.Add(new AemAuthoringValidationRow("Error", "Writer", exception.Message));
            ReportError(exception, "Select the affected submesh and resolve representability errors before saving.");
        }
    }

    private async Task<AemAuthoringResult> BuildAsync()
    {
        IsBusy = true;
        try
        {
            return await Task.Run(() => project.Build());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshPreviewAsync()
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        CancellationToken token = previewCancellation.Token;
        if (project.Current.Submeshes.Count == 0)
        {
            PreviewDocument = null;
            Status = "Empty authoring project · import one or more submeshes";
            return;
        }

        try
        {
            AemAuthoringResult result = await Task.Run(() => project.Build(token), token);
            ScenePreviewResult preview = await Task.Run(() => new ScenePreviewRenderer().Render(
                result.Scene, new ScenePreviewOptions(640, 420, ShowNormals: false, Camera: new SceneCamera()), token), token);
            string virtualPath = Path.Combine(Path.GetTempPath(), "gof2-workshop-authoring", project.Current.Name + ".aem");
            IndexedAsset asset = new(
                virtualPath, "Authoring/" + project.Current.Name + ".aem", project.Current.Name + ".aem",
                AssetKind.Aem, AssetOwnership.Mod, result.Bytes.Length, DateTimeOffset.UtcNow,
                $"AEM v{(int)project.Current.Version} authoring preview", ((int)project.Current.Version).ToString(CultureInfo.InvariantCulture),
                AssetSupport.Supported, true, null);
            List<AemMaterialAssignment> materials = [];
            for (int index = 0; index < project.Current.Submeshes.Count; index++)
            {
                AemAuthoringSubmesh source = project.Current.Submeshes[index];
                IndexedAsset? texture = null;
                SceneTextureBinding? binding = null;
                if (source.MaterialAsset is { } material && File.Exists(material) && Path.GetExtension(material).Equals(".aei", StringComparison.OrdinalIgnoreCase))
                {
                    FileInfo info = new(material);
                    texture = new IndexedAsset(material, material, Path.GetFileName(material), AssetKind.Aei, AssetOwnership.Mod,
                        info.Length, info.LastWriteTimeUtc, "Authoring texture", null, AssetSupport.Supported, true, null);
                    binding = await AemEditorProvider.DecodeTextureAsync(texture, workspace, token);
                }

                AssetRelationshipResolution resolution = new(
                    asset, index,
                    texture is null ? AssetRelationshipSource.Unresolved : AssetRelationshipSource.WorkspaceOverride,
                    texture is null ? AssetRelationshipConfidence.None : AssetRelationshipConfidence.Confirmed,
                    texture,
                    texture is null ? [] : [new AssetRelationshipCandidate(texture, AssetRelationshipSource.WorkspaceOverride, AssetRelationshipConfidence.Confirmed, "Authoring material assignment.", 10_000)],
                    texture is null ? "No preview texture assigned." : "Workshop authoring preview/export assignment; game-effective storage is not implied.",
                    []);
                materials.Add(new AemMaterialAssignment(index, source.Name, resolution, binding));
            }

            PreviewDocument = new AemDocumentViewModel(
                asset, result.Reparsed, result.Scene, preview, workspace, dialogs, output, problems,
                relationships, workspaceService, materials);
            PopulateValidation(result);
            Status = $"Preview rebuilt · {result.Reparsed.Submeshes.Count} submeshes · writer reparse passed";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or IOException)
        {
            Status = "Preview validation failed: " + exception.Message;
            Validation.Clear();
            Validation.Add(new AemAuthoringValidationRow("Error", "Preview", exception.Message));
        }
    }

    private void PopulateValidation(AemAuthoringResult result)
    {
        Validation.Clear();
        Validation.Add(new AemAuthoringValidationRow("Passed", "Writer", $"{result.Bytes.Length:N0} bytes serialized and reparsed."));
        Validation.Add(new AemAuthoringValidationRow("Passed", "Geometry", $"{result.Reparsed.Submeshes.Count} submeshes; indices and finite positions validated."));
        Validation.Add(new AemAuthoringValidationRow("Passed", "Preview", $"Scene conversion produced {result.Scene.Primitives.Count} render primitives."));
        foreach (ModelImportDiagnostic diagnostic in result.Diagnostics)
        {
            Validation.Add(new AemAuthoringValidationRow(diagnostic.Severity.ToString(), diagnostic.Code, diagnostic.Message));
        }
    }

    private void Apply(Action operation)
    {
        try
        {
            string? stableId = SelectedSubmesh?.StableId;
            operation();
            RefreshRows(stableId);
            _ = RefreshPreviewAsync();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            ReportError(exception, "Review the selected submesh, lock state, value range, and target AEM limits.");
        }
    }

    private void RefreshRows(string? preferred = null)
    {
        string? selectedId = preferred ?? SelectedSubmesh?.StableId;
        Submeshes.Clear();
        foreach ((AemAuthoringSubmesh value, int index) in project.Current.Submeshes.Select((value, index) => (value, index)))
        {
            Submeshes.Add(new AemAuthoringSubmeshRow(
                value.StableId, value.Name, index, value.Geometry.Positions.Length,
                value.Geometry.Indices.Length / 3, Path.GetFileName(value.MaterialAsset) ?? "Unassigned", value.Hidden, value.Locked));
        }

        SelectedSubmesh = selectedId is null
            ? Submeshes.FirstOrDefault()
            : Submeshes.FirstOrDefault(value => value.StableId == selectedId) ?? Submeshes.FirstOrDefault();
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(AssetDetails));
        RaiseCommandStates();
    }

    private void RefreshTracks()
    {
        Tracks.Clear();
        foreach (AemAuthoringTrack track in CurrentSelected()?.AnimationTracks ?? [])
        {
            Tracks.Add(new AemAuthoringTrackRow(track.Channel, track.Keys.Count, $"{track.Channel} · {track.Keys.Count} keys"));
        }
        SelectedTrack = Tracks.FirstOrDefault();
    }

    private void RefreshKeys()
    {
        string? selectedIdentity = SelectedKey is null ? null : $"{SelectedKey.Channel}:{SelectedKey.Index}";
        Keys.Clear();
        if (SelectedTrack is not null)
        {
            AemAuthoringTrack? track = CurrentSelected()?.AnimationTracks.FirstOrDefault(value => value.Channel == SelectedTrack.Channel);
            foreach ((AemAuthoringKey key, int index) in (track?.Keys ?? []).Select((value, index) => (value, index)))
            {
                Keys.Add(new AemAuthoringKeyRow(SelectedTrack.Channel, index, key.Time, key.Value));
            }
        }
        SelectedKey = selectedIdentity is null
            ? Keys.FirstOrDefault()
            : Keys.FirstOrDefault(value => $"{value.Channel}:{value.Index}" == selectedIdentity) ?? Keys.FirstOrDefault();
    }

    private AemAuthoringSubmesh? CurrentSelected() => SelectedSubmesh is null
        ? null
        : project.Current.Submeshes.FirstOrDefault(value => value.StableId == SelectedSubmesh.StableId);

    private string[] SelectedIds() => selectedStableIds.Count == 0 && SelectedSubmesh is not null
        ? [SelectedSubmesh.StableId]
        : [.. selectedStableIds.Where(id => project.Current.Submeshes.Any(value => value.StableId == id))];

    private AemAuthoringSubmesh FindCurrent(string stableId) => project.Current.Submeshes.First(
        value => value.StableId == stableId);

    private bool HasUnlockedSelection()
    {
        string[] ids = SelectedIds();
        return ids.Length > 0 && ids.All(id => !FindCurrent(id).Locked);
    }

    private bool CanMove(int delta) => HasUnlockedSelection() && SelectedSubmesh is not null &&
        SelectedSubmesh.Index + delta >= 0 && SelectedSubmesh.Index + delta < project.Current.Submeshes.Count;

    private void RaiseCommandStates()
    {
        foreach (System.Windows.Input.ICommand command in new[]
        {
            DuplicateCommand, DeleteCommand, MoveUpCommand, MoveDownCommand, RenameCommand,
            ToggleHiddenCommand, ToggleLockedCommand, ApplyPivotCommand, CenterPivotCommand,
            RecalculateBoundsCommand, NormalizeNormalsCommand, ReverseWindingCommand,
            RemoveDegeneratesCommand, WeldVerticesCommand, ApplyTransformCommand,
            AddKeyCommand, DeleteTrackCommand, UpdateKeyCommand, DeleteKeyCommand,
            DuplicateKeyCommand, UndoCommand, RedoCommand,
        })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
        ((AsyncRelayCommand)ImportCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)AssignTextureCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ImportAnimationCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ValidateCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)SaveCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)StageCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ExportGltfCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ExportObjCommand).RaiseCanExecuteChanged();
    }

    private void ReportError(Exception exception, string action)
    {
        problems.Add(new ProblemEntry(ProblemSeverity.Error, project.Current.Name, null, "AEM Authoring",
            exception.Message, null, null, action));
        output.Write(OutputLevel.Error, "AEM Authoring", exception.Message);
    }

    protected override void DisposeCore()
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        PreviewDocument = null;
    }
}
