using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Gof2Workshop.App.Presentation;

public interface IUserDialogService
{
    public Task<string?> PickFolderAsync(string title, string? suggestedStartPath = null);

    public Task<string?> PickWorkspaceFileAsync();

    public Task<string?> SaveFileAsync(
        string title,
        string suggestedName,
        string extension,
        string? suggestedStartPath = null);

    public Task<string?> PickAssetFileAsync(string title, string extension);

    public void RevealInExplorer(string path);
}

public sealed class UserDialogService : IUserDialogService
{
    public Window? Owner { get; set; }

    public async Task<string?> PickFolderAsync(
        string title,
        string? suggestedStartPath = null)
    {
        Window owner = Owner ?? throw new InvalidOperationException("Dialog owner is not attached.");
        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(suggestedStartPath) &&
            Directory.Exists(suggestedStartPath))
        {
            suggested = await owner.StorageProvider.TryGetFolderFromPathAsync(suggestedStartPath);
        }

        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = suggested,
            });
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickWorkspaceFileAsync()
    {
        Window owner = Owner ?? throw new InvalidOperationException("Dialog owner is not attached.");
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open Galaxy on Fire 2 Workspace",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("GOF2 Workspace")
                    {
                        Patterns = ["*.gof2workspace"],
                    },
                ],
            });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> SaveFileAsync(
        string title,
        string suggestedName,
        string extension,
        string? suggestedStartPath = null)
    {
        Window owner = Owner ?? throw new InvalidOperationException("Dialog owner is not attached.");
        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(suggestedStartPath) &&
            Directory.Exists(suggestedStartPath))
        {
            suggested = await owner.StorageProvider.TryGetFolderFromPathAsync(suggestedStartPath);
        }

        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = extension.TrimStart('.'),
                SuggestedStartLocation = suggested,
            });
        return file?.Path.LocalPath;
    }

    public async Task<string?> PickAssetFileAsync(string title, string extension)
    {
        Window owner = Owner ?? throw new InvalidOperationException("Dialog owner is not attached.");
        string normalized = extension.StartsWith('.') ? extension : "." + extension;
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType($"{normalized.TrimStart('.').ToUpperInvariant()} asset")
                    {
                        Patterns = ["*" + normalized],
                    },
                ],
            });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public void RevealInExplorer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string argument = File.Exists(fullPath) ? $"/select,\"{fullPath}\"" : $"\"{fullPath}\"";
        _ = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            });
    }
}
