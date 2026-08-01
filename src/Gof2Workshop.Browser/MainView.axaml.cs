using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.GameData;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.Browser;

public sealed record BrowserDataFieldRow(GameDataField Field, string Value)
{
    public string Name => Field.Name;

    public string Kind => Field.Kind.ToString();

    public string Confidence => Field.Confidence;

    public bool Editable => Field.Editable;
}

public sealed record BrowserAeiRegionRow(AeiRegion Region)
{
    public string Label => $"Region {Region.Index}: {Region.X}, {Region.Y} - {Region.Width} x {Region.Height}";
}

public sealed partial class MainView : UserControl, INotifyPropertyChanged, IDisposable
{
    private BrowserAssetItem? selectedAsset;
    private WriteableBitmap? preview;
    private string status = "Ready. Open files without creating a workspace.";
    private AssetPlatformProfile selectedProfile = ProfileCatalog.Pc1X;
    private string storageStatus = "No persistent assets stored.";
    private readonly BrowserWebGlSceneRenderer webGlRenderer = new();
    private BrowserRenderOptions renderOptions = new();
    private bool useSoftwareRenderer;
    private bool animationPlaying;
    private string rendererStatus = "WebGL 2 initializes when a model opens.";
    private bool smokeStarted;
    private BrowserDataFieldRow? selectedDataField;
    private string dataEditValue = string.Empty;
    private IReadOnlyList<BrowserDataFieldRow> selectedDataFields = [];
    private IReadOnlyList<BrowserAeiRegionRow> selectedAeiRegions = [];
    private BrowserAeiRegionRow? selectedAeiRegion;

    public MainView()
    {
        InitializeComponent();
        DataContext = this;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        try
        {
            string? savedProfile = BrowserLocalStorage.GetSetting("profile");
            if (!string.IsNullOrWhiteSpace(savedProfile))
            {
                selectedProfile = ProfileCatalog.Resolve(savedProfile);
            }

            UpdateStorageStatus();
        }
        catch (Exception)
        {
            storageStatus = "Browser local settings are unavailable; the session remains in memory.";
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public BrowserAssetSession Session { get; } = new();

    public IReadOnlyList<AssetPlatformProfile> Profiles { get; } = ProfileCatalog.All;

    public AssetPlatformProfile SelectedProfile
    {
        get => selectedProfile;
        set
        {
            selectedProfile = value;
            Notify();
            try
            {
                BrowserLocalStorage.SetSetting("profile", value.Id);
                UpdateStorageStatus();
            }
            catch (Exception)
            {
                StorageStatus = "Profile changed for this tab; local settings storage is unavailable.";
            }
        }
    }

    public string StorageStatus
    {
        get => storageStatus;
        private set
        {
            storageStatus = value;
            Notify();
        }
    }

    public BrowserAssetItem? SelectedAsset
    {
        get => selectedAsset;
        set
        {
            if (ReferenceEquals(selectedAsset, value))
            {
                return;
            }

            selectedAsset = value;
            Notify();
            RefreshDataFields();
            RefreshAeiRegions();
            ShowSelectedAsset();
        }
    }

    public WriteableBitmap? Preview
    {
        get => preview;
        private set
        {
            WriteableBitmap? previous = preview;
            preview = value;
            Notify();
            Notify(nameof(CanExportPng));
            previous?.Dispose();
        }
    }

    public string Status
    {
        get => status;
        private set
        {
            status = value;
            Notify();
        }
    }

    public string RendererStatus
    {
        get => rendererStatus;
        private set
        {
            rendererStatus = value;
            Notify();
        }
    }

    public bool IsEmpty => Session.Assets.Count == 0;

    public bool CanExportPng => selectedAsset?.EffectiveTexture is not null || selectedAsset?.Scene is not null;

    public bool HasScene => selectedAsset?.Scene is not null;

    public bool HasAem => selectedAsset?.Aem is not null;

    public bool CanPlayAnimation => selectedAsset?.Scene?.Animations.Count > 0;

    public bool HasGameData => selectedAsset?.GameData is not null;

    public bool HasEditableAei => selectedAsset?.AeiEditSession is not null;

    public bool CanUndoAeiEdit => selectedAsset?.AeiEditSession?.CanUndo == true;

    public bool CanRedoAeiEdit => selectedAsset?.AeiEditSession?.CanRedo == true;

    public IReadOnlyList<BrowserAeiRegionRow> SelectedAeiRegions
    {
        get => selectedAeiRegions;
        private set
        {
            selectedAeiRegions = value;
            Notify();
        }
    }

    public BrowserAeiRegionRow? SelectedAeiRegion
    {
        get => selectedAeiRegion;
        set
        {
            selectedAeiRegion = value;
            Notify();
            Notify(nameof(CanImportAeiRegion));
        }
    }

    public bool CanImportAeiRegion => HasEditableAei && selectedAeiRegion is not null;

    public IReadOnlyList<BrowserDataFieldRow> SelectedDataFields
    {
        get => selectedDataFields;
        private set
        {
            selectedDataFields = value;
            Notify();
        }
    }

    public BrowserDataFieldRow? SelectedDataField
    {
        get => selectedDataField;
        set
        {
            selectedDataField = value;
            DataEditValue = value?.Value ?? string.Empty;
            Notify();
            Notify(nameof(CanEditSelectedDataField));
        }
    }

    public string DataEditValue
    {
        get => dataEditValue;
        set
        {
            dataEditValue = value;
            Notify();
        }
    }

    public bool CanEditSelectedDataField => selectedDataField?.Editable == true;

    public bool CanUndoDataEdit => selectedAsset?.GameDataSession?.CanUndo == true;

    public bool CanRedoDataEdit => selectedAsset?.GameDataSession?.CanRedo == true;

    private async void OnOpenFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Quick Inspect local assets",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Workshop assets")
                        {
                            Patterns = ["*.aei", "*.aem", "*.png", "*.gltf", "*.glb", "*.obj", "*.mtl", "*.bin"],
                        },
                        FilePickerFileTypes.All,
                    ],
                });
            await AddFilesAsync(files);
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private async void OnLoadDemo(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        await LoadPublicDemoAsync(clearFirst: true);
    }

    private async void OnSaveBrowserWorkspace(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            string json = BrowserWorkspaceStorage.Serialize(Session, SelectedProfile.Id);
            await BrowserWorkspaceStorage.SaveAsync(BrowserWorkspaceStorage.LastWorkspaceKey, json);
            await BrowserWorkspaceStorage.SaveAsync(BrowserWorkspaceStorage.RecoveryKey, json);
            await UpdatePersistentStorageStatusAsync();
            Status = $"Saved {Session.Assets.Count} asset(s) to this browser origin. No network request was made.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private async void OnRestoreBrowserWorkspace(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            string? json = await BrowserWorkspaceStorage.LoadAsync(BrowserWorkspaceStorage.LastWorkspaceKey)
                ?? await BrowserWorkspaceStorage.LoadAsync(BrowserWorkspaceStorage.RecoveryKey);
            if (string.IsNullOrEmpty(json))
            {
                Status = "No browser-local workspace or recovery snapshot exists.";
                return;
            }

            await RestoreArchiveAsync(BrowserWorkspaceStorage.Deserialize(json));
            Status = $"Restored {Session.Assets.Count} asset(s) from browser-local IndexedDB.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private async void OnExportBrowserWorkspace(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            string json = BrowserWorkspaceStorage.Serialize(Session, SelectedProfile.Id);
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IStorageFile? destination = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Download browser workspace archive",
                    SuggestedFileName = "gof2-browser-workspace.json",
                    DefaultExtension = "json",
                    FileTypeChoices = [new FilePickerFileType("Workshop browser archive") { Patterns = ["*.json"] }],
                });
            if (destination is null)
            {
                return;
            }

            await using Stream output = await destination.OpenWriteAsync();
            await using StreamWriter writer = new(output, System.Text.Encoding.UTF8, leaveOpen: true);
            await writer.WriteAsync(json);
            await writer.FlushAsync();
            Status = $"Exported {destination.Name} through a user-authorized browser download.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private async void OnImportBrowserWorkspace(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Import browser workspace archive",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Workshop browser archive") { Patterns = ["*.json"] }],
                });
            IStorageFile? file = files.Count == 0 ? null : files[0];
            if (file is null)
            {
                return;
            }

            await using Stream input = await file.OpenReadAsync();
            using StreamReader reader = new(input, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string json = await reader.ReadToEndAsync();
            BrowserWorkspaceArchive archive = BrowserWorkspaceStorage.Deserialize(json);
            await RestoreArchiveAsync(archive);
            await BrowserWorkspaceStorage.SaveAsync(BrowserWorkspaceStorage.LastWorkspaceKey, json);
            Status = $"Imported {Session.Assets.Count} asset(s); a browser-local copy is now available for restoration.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = eventArgs.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs eventArgs)
    {
        try
        {
            IReadOnlyList<IStorageItem>? items = eventArgs.DataTransfer.TryGetFiles();
            if (items is null)
            {
                return;
            }

            await AddFilesAsync(items.OfType<IStorageFile>().ToArray());
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private async Task AddFilesAsync(IReadOnlyList<IStorageFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        int successes = 0;
        List<string> failures = [];
        foreach (IStorageFile file in files)
        {
            try
            {
                Status = $"Reading {file.Name} locally...";
                await using Stream stream = await file.OpenReadAsync();
                BrowserAssetItem item = await Session.AddAsync(file.Name, stream, SelectedProfile);
                SelectedAsset ??= item;
                successes++;
            }
            catch (Exception exception)
            {
                failures.Add($"{file.Name}: {FriendlyError(exception)}");
            }
        }

        try
        {
            IReadOnlyList<BrowserAssetItem> authored = Session.AuthorImportedModels(SelectedProfile);
            if (authored.Count > 0)
            {
                SelectedAsset = authored[^1];
                successes += authored.Count;
            }
        }
        catch (Exception exception)
        {
            failures.Add($"Model authoring: {FriendlyError(exception)}");
        }

        Notify(nameof(IsEmpty));
        ShowSelectedAsset();
        Status = failures.Count == 0
            ? $"Loaded {successes} file(s) locally. No upload or server processing occurred."
            : $"Loaded {successes}; {failures.Count} failed. {string.Join(" | ", failures.Take(3))}";
    }

    private async void OnExportPng(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (selectedAsset is null)
        {
            return;
        }

        try
        {
            Gof2Workshop.Core.RgbaImage? image = selectedAsset.EffectiveTexture;
            BrowserAssetItem? texture = selectedAsset.Scene is null
                ? null
                : Session.ResolveTexture(selectedAsset);
            image ??= selectedAsset.Scene is null
                ? null
                : BrowserAssetSession.RenderScene(selectedAsset, texture?.EffectiveTexture);
            if (image is null)
            {
                return;
            }

            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IStorageFile? destination = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Download PNG preview",
                    SuggestedFileName = Path.GetFileNameWithoutExtension(selectedAsset.Name) + ".png",
                    DefaultExtension = "png",
                    FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
                });
            if (destination is null)
            {
                return;
            }

            await using Stream output = await destination.OpenWriteAsync();
            PngWriter.Write(image, output);
            await output.FlushAsync();
            Status = $"Exported {destination.Name} through a browser-authorized download.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private void OnClear(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        webGlRenderer.Hide();
        SelectedAsset = null;
        Preview = null;
        Session.Clear();
        Notify(nameof(IsEmpty));
        Status = "In-memory collection cleared.";
    }

    private async void OnExportBin(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (selectedAsset?.GameDataSession is not { } session)
        {
            return;
        }

        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IStorageFile? destination = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Download validated BIN copy",
                    SuggestedFileName = selectedAsset.Name,
                    DefaultExtension = "bin",
                    FileTypeChoices = [new FilePickerFileType("GOF structured BIN") { Patterns = ["*.bin"] }],
                });
            if (destination is null)
            {
                return;
            }

            byte[] output = session.Write();
            GameDataDocument reparsed = new GameDataFormatRegistry().Parse(selectedAsset.Name, output);
            await using Stream stream = await destination.OpenWriteAsync();
            await stream.WriteAsync(output);
            await stream.FlushAsync();
            Status = $"Exported {destination.Name}; reparsed {reparsed.Records.Count} record(s). Original browser input was not modified.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private async void OnExportAem(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (selectedAsset?.Aem is null)
        {
            return;
        }

        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IStorageFile? destination = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Download AEM copy",
                    SuggestedFileName = selectedAsset.Name,
                    DefaultExtension = "aem",
                    FileTypeChoices = [new FilePickerFileType("Abyss Engine mesh") { Patterns = ["*.aem"] }],
                });
            if (destination is null)
            {
                return;
            }

            await using Stream output = await destination.OpenWriteAsync();
            await output.WriteAsync(selectedAsset.Bytes);
            await output.FlushAsync();
            Status = selectedAsset.IsGenerated
                ? $"Downloaded authored {destination.Name}; writer/reparse validation passed."
                : $"Downloaded a read-only copy of {destination.Name}; the source selection was not modified.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private void OnApplyDataEdit(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            if (selectedAsset?.GameDataSession is not { } session || selectedDataField is null)
            {
                return;
            }

            session.Replace(selectedDataField.Field.Id, DataEditValue);
            byte[] candidate = session.Write();
            _ = new GameDataFormatRegistry().Parse(selectedAsset.Name, candidate);
            RefreshDataFields(selectedDataField.Field.Id);
            _ = SaveRecoveryAsync();
            Status = $"Applied '{selectedDataField.Name}' in the derived BIN snapshot; writer reparse succeeded.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private void OnUndoDataEdit(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        selectedAsset?.GameDataSession?.Undo();
        RefreshDataFields(selectedDataField?.Field.Id);
        Status = "Undid the most recent BIN edit operation.";
    }

    private void OnRedoDataEdit(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        selectedAsset?.GameDataSession?.Redo();
        RefreshDataFields(selectedDataField?.Field.Id);
        Status = "Redid the BIN edit operation.";
    }

    private async void OnImportAeiRegion(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (selectedAsset?.AeiEditSession is not { } session || selectedAeiRegion is null)
        {
            return;
        }

        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = $"Import {selectedAeiRegion.Region.Width} x {selectedAeiRegion.Region.Height} region PNG",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
                });
            if (files.Count == 0)
            {
                return;
            }

            await using Stream input = await files[0].OpenReadAsync();
            RgbaImage replacement = BrowserBitmapFactory.LoadRgba(input);
            session.ReplaceRegion(selectedAeiRegion.Region.Index, replacement);
            Preview = BrowserBitmapFactory.Create(session.WorkingAtlas);
            NotifyAeiEditState();
            await SaveRecoveryAsync();
            Status = $"Replaced region {selectedAeiRegion.Region.Index} in the derived atlas; the original AEI remains unchanged.";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private void OnUndoAeiEdit(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        selectedAsset?.AeiEditSession?.Undo();
        RefreshAeiWorkingPreview("Undid the AEI region operation.");
    }

    private void OnRedoAeiEdit(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        selectedAsset?.AeiEditSession?.Redo();
        RefreshAeiWorkingPreview("Redid the AEI region operation.");
    }

    private async void OnExportEditedAei(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (selectedAsset?.AeiEditSession is not { } session)
        {
            return;
        }

        try
        {
            AeiEncodingResult result = session.Validate();
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            IStorageFile? destination = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Download validated edited AEI",
                    SuggestedFileName = selectedAsset.Name,
                    DefaultExtension = "aei",
                    FileTypeChoices = [new FilePickerFileType("Abyss Engine image") { Patterns = ["*.aei"] }],
                });
            if (destination is null)
            {
                return;
            }

            await using Stream output = await destination.OpenWriteAsync();
            new AeiWriter().Write(session.OriginalFile, output, result.Payload);
            await output.FlushAsync();
            Status = $"Exported validated {destination.Name}; reparse/decode passed (maximum channel error {result.MaximumChannelError}).";
        }
        catch (Exception exception)
        {
            Status = FriendlyError(exception);
        }
    }

    private void OnFrameAll(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        webGlRenderer.FrameAll();

    private void OnFrameSelected(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        webGlRenderer.FrameSelected();

    private void OnToggleAnimation(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        animationPlaying = !animationPlaying;
        webGlRenderer.SetAnimation(animationPlaying, 0);
        RendererStatus = animationPlaying
            ? "WebGL animation playback active."
            : "WebGL animation paused at the current time.";
    }

    private void OnToggleLit(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        renderOptions = renderOptions with { Lit = !renderOptions.Lit };
        ApplyRenderOptions();
    }

    private void OnToggleWire(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        renderOptions = renderOptions with { Wireframe = !renderOptions.Wireframe };
        ApplyRenderOptions();
    }

    private void OnToggleBounds(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        renderOptions = renderOptions with { Bounds = !renderOptions.Bounds };
        ApplyRenderOptions();
    }

    private void OnToggleProjection(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        renderOptions = renderOptions with { Orthographic = !renderOptions.Orthographic };
        ApplyRenderOptions();
    }

    private void OnToggleRenderer(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        useSoftwareRenderer = !useSoftwareRenderer;
        ShowSelectedAsset();
    }

    private void ApplyRenderOptions()
    {
        webGlRenderer.ApplyOptions(renderOptions);
        RendererStatus = webGlRenderer.Diagnostics;
    }

    private async void OnClearLocalData(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            await BrowserWorkspaceStorage.ClearAllAsync();
            StorageStatus = "Workshop settings, workspaces, recovery snapshots, and cached metadata were cleared.";
        }
        catch (Exception exception)
        {
            StorageStatus = $"Could not clear browser settings: {exception.Message}";
        }
    }

    private void ShowSelectedAsset()
    {
        try
        {
            Notify(nameof(CanExportPng));
            Notify(nameof(HasScene));
            Notify(nameof(HasAem));
            Notify(nameof(CanPlayAnimation));
            Notify(nameof(HasGameData));
            Notify(nameof(HasEditableAei));
            animationPlaying = false;
            webGlRenderer.SetAnimation(false, 0);

            Gof2Workshop.Core.RgbaImage? image = selectedAsset?.EffectiveTexture;
            BrowserAssetItem? texture = selectedAsset?.Scene is null || selectedAsset is null
                ? null
                : Session.ResolveTexture(selectedAsset);
            if (selectedAsset?.Scene is not null && !useSoftwareRenderer)
            {
                Preview = null;
                RendererStatus = webGlRenderer.Show(selectedAsset, texture?.EffectiveTexture, renderOptions);
            }
            else
            {
                webGlRenderer.Hide();
                image ??= selectedAsset?.Scene is null || selectedAsset is null
                    ? null
                    : BrowserAssetSession.RenderScene(selectedAsset, texture?.EffectiveTexture);
                RendererStatus = selectedAsset?.Scene is null
                    ? "Texture preview uses the decoded Workshop RGBA surface."
                    : "Bounded software fallback active.";
            }

            Preview = image is null ? null : BrowserBitmapFactory.Create(image);
            Status = selectedAsset is null
                ? "Ready."
                : texture is null
                    ? selectedAsset.Summary
                    : $"{selectedAsset.Summary}; textured locally with {texture.Name} (filename relationship).";
        }
        catch (Exception exception)
        {
            webGlRenderer.Hide();
            Preview = null;
            RendererStatus = "WebGL failed; controlled software fallback activated.";
            try
            {
                if (selectedAsset?.Scene is not null)
                {
                    RgbaImage fallback = BrowserAssetSession.RenderScene(
                        selectedAsset,
                        Session.ResolveTexture(selectedAsset)?.EffectiveTexture);
                    Preview = BrowserBitmapFactory.Create(fallback);
                }
            }
            catch (Exception fallbackException)
            {
                Status = $"{FriendlyError(exception)} Software fallback also failed: {FriendlyError(fallbackException)}";
                return;
            }

            Status = FriendlyError(exception);
        }
    }

    private async void OnAttachedToVisualTree(
        object? sender,
        Avalonia.VisualTreeAttachmentEventArgs eventArgs)
    {
        if (smokeStarted)
        {
            return;
        }

        smokeStarted = true;
        try
        {
            string? smoke = BrowserWebGlSceneRenderer.QueryParameter("smoke");
            if (string.Equals(smoke, "1", StringComparison.Ordinal))
            {
                await LoadPublicDemoAsync(clearFirst: true);
                await Task.Delay(750);
                BrowserWebGlInterop.SetSmokeStatus(
                    HasScene && RendererStatus.StartsWith("WebGL", StringComparison.Ordinal)
                        ? "pass"
                        : "fail");
                RendererStatus = webGlRenderer.Diagnostics;
            }
            else if (string.Equals(smoke, "bin", StringComparison.Ordinal))
            {
                await LoadPublicDemoAsync(clearFirst: true);
                BrowserAssetItem data = Session.Assets.First(asset => asset.GameData is not null);
                SelectedAsset = data;
                GameDataField name = data.GameData!.Records[0].Fields[0];
                data.GameDataSession!.Replace(name.Id, "Zyla");
                byte[] output = data.GameDataSession.Write();
                GameDataDocument reparsed = new GameDataFormatRegistry().Parse(data.Name, output);
                RefreshDataFields(name.Id);
                Status = $"Browser BIN smoke: edited {reparsed.Records[0].Fields[0].Value}, reparsed, original retained.";
                BrowserWebGlInterop.SetSmokeStatus(
                    reparsed.Records[0].Fields[0].Value == "Zyla" ? "pass" : "fail");
            }
            else if (string.Equals(smoke, "storage", StringComparison.Ordinal))
            {
                await BrowserWorkspaceStorage.ClearAllAsync();
                await LoadPublicDemoAsync(clearFirst: true);
                BrowserAssetItem data = Session.Assets.First(asset => asset.GameDataSession is not null);
                GameDataField name = data.GameData!.Records[0].Fields[0];
                data.GameDataSession!.Replace(name.Id, "Zyla");
                string json = BrowserWorkspaceStorage.Serialize(Session, SelectedProfile.Id);
                await BrowserWorkspaceStorage.SaveAsync(BrowserWorkspaceStorage.LastWorkspaceKey, json);
                Session.Clear();
                await RestoreArchiveAsync(BrowserWorkspaceStorage.Deserialize(
                    await BrowserWorkspaceStorage.LoadAsync(BrowserWorkspaceStorage.LastWorkspaceKey)
                        ?? throw new InvalidDataException("IndexedDB did not return the saved workspace.")));
                BrowserAssetItem restored = Session.Assets.First(asset => asset.GameDataSession is not null);
                string value = restored.GameDataSession!.AppliedOperations.Single().NewValue;
                Status = $"Browser storage smoke: restored {Session.Assets.Count} assets and recovered BIN edit {value}.";
                BrowserWebGlInterop.SetSmokeStatus(
                    Session.Assets.Count == 3 && value == "Zyla" ? "pass" : "fail");
            }
            else if (string.Equals(smoke, "aei-edit", StringComparison.Ordinal))
            {
                await LoadPublicDemoAsync(clearFirst: true);
                BrowserAssetItem texture = Session.Assets.First(asset => asset.AeiEditSession is not null);
                SelectedAsset = texture;
                Assembly assembly = typeof(MainView).Assembly;
                await using Stream png = assembly.GetManifestResourceStream(
                    "Gof2Workshop.Browser.Samples.synthetic-atlas-preview.png")
                    ?? throw new InvalidOperationException("The public replacement PNG is missing.");
                RgbaImage replacement = BrowserBitmapFactory.LoadRgba(png);
                texture.AeiEditSession!.ReplaceRegion(0, replacement);
                AeiEncodingResult validation = texture.AeiEditSession.Validate();
                Preview = BrowserBitmapFactory.Create(texture.AeiEditSession.WorkingAtlas);
                await SaveRecoveryAsync();
                NotifyAeiEditState();
                Status = $"Browser AEI smoke: decoded PNG, replaced region, encoded {texture.Aei!.Format.DisplayName}, reparsed and decoded; max error {validation.MaximumChannelError}.";
                BrowserWebGlInterop.SetSmokeStatus(
                    texture.AeiEditSession.Operations.Count == 1 && validation.ReparsedFile.Width == 16
                        ? "pass"
                        : "fail");
            }
            else if (string.Equals(smoke, "aem-author", StringComparison.Ordinal))
            {
                SelectedAsset = null;
                Session.Clear();
                Assembly assembly = typeof(MainView).Assembly;
                foreach ((string resource, string name) in new[]
                {
                    ("Gof2Workshop.Browser.Samples.synthetic_cube_import.gltf", "synthetic_cube_import.gltf"),
                    ("Gof2Workshop.Browser.Samples.synthetic_cube_import.bin", "synthetic_cube_import.bin"),
                })
                {
                    await using Stream input = assembly.GetManifestResourceStream(resource)
                        ?? throw new InvalidOperationException($"The public browser sample {resource} is missing.");
                    _ = await Session.AddAsync(name, input, SelectedProfile);
                }

                BrowserAssetItem authored = Session.AuthorImportedModels(SelectedProfile).Single();
                SelectedAsset = authored;
                await Task.Delay(500);
                Status = $"Browser AEM authoring smoke: imported glTF sidecar collection, wrote v4, reparsed and rendered {authored.Scene!.Primitives.Count} submesh(es).";
                await SaveRecoveryAsync();
                BrowserWebGlInterop.SetSmokeStatus(
                    authored.IsGenerated && authored.Aem?.Version == AemVersion.V4 ? "pass" : "fail");
            }
        }
        catch (Exception exception)
        {
            BrowserWebGlInterop.SetSmokeStatus("fail");
            Status = FriendlyError(exception);
        }
    }

    private void OnDetachedFromVisualTree(
        object? sender,
        Avalonia.VisualTreeAttachmentEventArgs eventArgs)
    {
        Dispose();
    }

    public void Dispose()
    {
        webGlRenderer.Dispose();
        Preview = null;
    }

    private async Task LoadPublicDemoAsync(bool clearFirst)
    {
        if (clearFirst)
        {
            SelectedAsset = null;
            Preview = null;
            Session.Clear();
        }

        Assembly assembly = typeof(MainView).Assembly;
        BrowserAssetItem? model = null;
        foreach ((string resource, string name) in new[]
        {
            ("Gof2Workshop.Browser.Samples.synthetic_animated.aei", "synthetic_animated.aei"),
            ("Gof2Workshop.Browser.Samples.synthetic_animated.aem", "synthetic_animated.aem"),
        })
        {
            await using Stream input = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The public browser sample {resource} is missing.");
            BrowserAssetItem item = await Session.AddAsync(name, input, SelectedProfile);
            if (item.Scene is not null)
            {
                model = item;
            }
        }

        byte[] syntheticNames =
        [
            0, 0, 0, 2,
            0, 4, (byte)'A', (byte)'y', (byte)'l', (byte)'a',
            0, 4, (byte)'B', (byte)'o', (byte)'r', (byte)'o',
        ];
        await using (MemoryStream names = new(syntheticNames, writable: false))
        {
            _ = await Session.AddAsync("names_synthetic_0.bin", names, SelectedProfile);
        }

        Notify(nameof(IsEmpty));
        SelectedAsset = model ?? Session.Assets.FirstOrDefault();
        Status = "Loaded the CC0 browser demo locally: animated AEM plus matching AEI texture.";
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        FormatParseException parse => $"{parse.FailureKind}: {parse.Reason} (field {parse.Field}, offset 0x{parse.Offset:X})",
        InvalidDataException => exception.Message,
        _ => $"Could not inspect this file: {exception.Message}",
    };

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void UpdateStorageStatus()
    {
        int bytes = BrowserLocalStorage.GetWorkshopStorageBytes();
        StorageStatus = $"Local settings: {bytes:N0} bytes. Asset bytes remain in memory only.";
    }

    private async Task UpdatePersistentStorageStatusAsync()
    {
        string json = await BrowserWorkspaceStorage.GetStorageEstimateAsync();
        BrowserStorageEstimate? estimate = System.Text.Json.JsonSerializer.Deserialize(
            json,
            BrowserWorkspaceJsonContext.Default.BrowserStorageEstimate);
        StorageStatus = estimate is null
            ? "Browser storage estimate is unavailable."
            : $"Browser origin storage: {estimate.Usage / 1048576d:F1} MiB used of {estimate.Quota / 1048576d:F1} MiB quota.";
    }

    private async Task RestoreArchiveAsync(BrowserWorkspaceArchive archive)
    {
        webGlRenderer.Hide();
        SelectedAsset = null;
        Preview = null;
        Session.Clear();
        SelectedProfile = ProfileCatalog.Resolve(archive.Profile);
        foreach (BrowserArchiveAsset asset in archive.Assets)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(asset.BytesBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"Workspace asset '{asset.Name}' has invalid base64 data.", exception);
            }

            await using MemoryStream input = new(bytes, writable: false);
            BrowserAssetItem restored = await Session.AddAsync(asset.Name, input, SelectedProfile);
            if (asset.AeiRecovery is not null)
            {
                restored.AeiEditSession?.Replay(asset.AeiRecovery);
            }

            if (asset.GameDataOperations is { Count: > 0 } && restored.GameDataSession is not null)
            {
                string hash = BrowserWorkspaceStorage.SourceHash(restored.Bytes);
                restored.GameDataSession.Replay(
                    new GameDataRecoveryDocument(1, hash, asset.GameDataOperations),
                    hash);
            }
        }

        Notify(nameof(IsEmpty));
        SelectedAsset = Session.Assets.FirstOrDefault();
        await UpdatePersistentStorageStatusAsync();
    }

    private async Task SaveRecoveryAsync()
    {
        try
        {
            string json = BrowserWorkspaceStorage.Serialize(Session, SelectedProfile.Id);
            await BrowserWorkspaceStorage.SaveAsync(BrowserWorkspaceStorage.RecoveryKey, json);
            await UpdatePersistentStorageStatusAsync();
        }
        catch (Exception exception)
        {
            StorageStatus = $"The edit is in memory, but browser recovery could not be updated: {exception.Message}";
        }
    }

    private void RefreshDataFields(string? preserveFieldId = null)
    {
        if (selectedAsset?.GameData is not { } document || selectedAsset.GameDataSession is not { } session)
        {
            SelectedDataFields = [];
            SelectedDataField = null;
            Notify(nameof(HasGameData));
            Notify(nameof(CanUndoDataEdit));
            Notify(nameof(CanRedoDataEdit));
            return;
        }

        Dictionary<string, string> current = session.AppliedOperations
            .GroupBy(operation => operation.FieldId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().NewValue, StringComparer.Ordinal);
        SelectedDataFields = document.Records
            .SelectMany(record => record.Fields)
            .Take(20_000)
            .Select(dataField => new BrowserDataFieldRow(
                dataField,
                TrimFieldValue(current.GetValueOrDefault(dataField.Id, dataField.Value))))
            .ToArray();
        SelectedDataField = SelectedDataFields.FirstOrDefault(row =>
            string.Equals(row.Field.Id, preserveFieldId, StringComparison.Ordinal));
        Notify(nameof(HasGameData));
        Notify(nameof(CanUndoDataEdit));
        Notify(nameof(CanRedoDataEdit));
    }

    private void RefreshAeiRegions()
    {
        SelectedAeiRegions = selectedAsset?.Aei?.Regions
            .Select(region => new BrowserAeiRegionRow(region))
            .ToArray() ?? [];
        SelectedAeiRegion = SelectedAeiRegions.Count == 0 ? null : SelectedAeiRegions[0];
        NotifyAeiEditState();
    }

    private void RefreshAeiWorkingPreview(string statusText)
    {
        if (selectedAsset?.AeiEditSession is { } session)
        {
            Preview = BrowserBitmapFactory.Create(session.WorkingAtlas);
            _ = SaveRecoveryAsync();
        }

        NotifyAeiEditState();
        Status = statusText;
    }

    private void NotifyAeiEditState()
    {
        Notify(nameof(HasEditableAei));
        Notify(nameof(CanImportAeiRegion));
        Notify(nameof(CanUndoAeiEdit));
        Notify(nameof(CanRedoAeiEdit));
    }

    private static string TrimFieldValue(string value) => value.Length <= 512
        ? value
        : value[..512] + $"... ({value.Length:N0} characters)";
}
