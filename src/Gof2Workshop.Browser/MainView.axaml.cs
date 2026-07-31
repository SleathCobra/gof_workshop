using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;

namespace Gof2Workshop.Browser;

public sealed partial class MainView : UserControl, INotifyPropertyChanged
{
    private BrowserAssetItem? selectedAsset;
    private WriteableBitmap? preview;
    private string status = "Ready. Open files without creating a workspace.";
    private AssetPlatformProfile selectedProfile = ProfileCatalog.Pc1X;
    private string storageStatus = "No persistent assets stored.";

    public MainView()
    {
        InitializeComponent();
        DataContext = this;
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

    public bool IsEmpty => Session.Assets.Count == 0;

    public bool CanExportPng => selectedAsset?.Texture is not null || selectedAsset?.Scene is not null;

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
            Gof2Workshop.Core.RgbaImage? image = selectedAsset.Texture;
            BrowserAssetItem? texture = selectedAsset.Scene is null
                ? null
                : Session.ResolveTexture(selectedAsset);
            image ??= selectedAsset.Scene is null
                ? null
                : BrowserAssetSession.RenderScene(selectedAsset, texture?.Texture);
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
        SelectedAsset = null;
        Preview = null;
        Session.Clear();
        Notify(nameof(IsEmpty));
        Status = "In-memory collection cleared.";
    }

    private void OnClearLocalData(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        try
        {
            BrowserLocalStorage.ClearWorkshopData();
            StorageStatus = "Workshop local settings cleared. Selected assets were never persisted.";
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
            Gof2Workshop.Core.RgbaImage? image = selectedAsset?.Texture;
            BrowserAssetItem? texture = selectedAsset?.Scene is null || selectedAsset is null
                ? null
                : Session.ResolveTexture(selectedAsset);
            image ??= selectedAsset?.Scene is null || selectedAsset is null
                ? null
                : BrowserAssetSession.RenderScene(selectedAsset, texture?.Texture);
            Preview = image is null ? null : BrowserBitmapFactory.Create(image);
            Status = selectedAsset is null
                ? "Ready."
                : texture is null
                    ? selectedAsset.Summary
                    : $"{selectedAsset.Summary}; textured locally with {texture.Name} (filename relationship).";
        }
        catch (Exception exception)
        {
            Preview = null;
            Status = FriendlyError(exception);
        }
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
}
