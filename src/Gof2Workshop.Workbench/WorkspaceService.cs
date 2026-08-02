using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Core;

namespace Gof2Workshop.Workbench;

public interface IWorkspaceService
{
    public Task<WorkspaceDefinition> CreateAsync(
        string directory,
        string name,
        string profileId,
        CancellationToken cancellationToken = default);

    public Task<WorkspaceLoadResult> LoadAsync(
        string workspaceFile,
        CancellationToken cancellationToken = default);

    public Task SaveAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default);

    public string ResolveModPath(WorkspaceDefinition workspace, string configuredPath);
}

public sealed record WorkspaceLoadResult(
    WorkspaceDefinition Workspace,
    IReadOnlyList<string> Warnings);

public sealed class WorkspaceService : IWorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<WorkspaceDefinition> CreateAsync(
        string directory,
        string name,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = ProfileCatalog.Resolve(profileId);

        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        foreach (string relative in new[]
        {
            Path.Combine("Assets", "Textures"),
            Path.Combine("Assets", "Models"),
            "Generated",
            ".work",
        })
        {
            Directory.CreateDirectory(Path.Combine(root, relative));
        }

        WorkspaceDefinition workspace = new()
        {
            Name = name.Trim(),
            ProfileId = profileId,
            ModRoot = ".",
            OutputRoot = "Generated",
            FilePath = Path.Combine(root, "project.gof2workspace"),
        };
        await SaveAsync(workspace, cancellationToken).ConfigureAwait(false);
        return workspace;
    }

    public async Task<WorkspaceLoadResult> LoadAsync(
        string workspaceFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFile);
        string fullPath = Path.GetFullPath(workspaceFile);
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        WorkspaceDefinition? workspace = await JsonSerializer.DeserializeAsync<WorkspaceDefinition>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            throw new InvalidDataException("The workspace file contains no workspace object.");
        }

        List<string> warnings = [];
        if (workspace.FormatVersion > WorkspaceDefinition.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"Workspace format {workspace.FormatVersion} is newer than supported format " +
                $"{WorkspaceDefinition.CurrentFormatVersion}.");
        }

        if (workspace.FormatVersion < WorkspaceDefinition.CurrentFormatVersion)
        {
            warnings.Add(
                $"Workspace format {workspace.FormatVersion} was migrated in memory to " +
                $"{WorkspaceDefinition.CurrentFormatVersion}.");
            workspace.FormatVersion = WorkspaceDefinition.CurrentFormatVersion;
        }

        workspace.FilePath = fullPath;
        if (!string.IsNullOrWhiteSpace(workspace.GameAssetRoot) &&
            !Path.IsPathRooted(workspace.GameAssetRoot))
        {
            string workspaceDirectory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("The workspace file has no parent directory.");
            workspace.GameAssetRoot = Path.GetFullPath(
                workspace.GameAssetRoot,
                workspaceDirectory);
        }

        workspace.Name = string.IsNullOrWhiteSpace(workspace.Name)
            ? Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "Untitled Mod"
            : workspace.Name;
        _ = ProfileCatalog.Resolve(workspace.ProfileId);
        workspace.ModRoot = string.IsNullOrWhiteSpace(workspace.ModRoot) ? "." : workspace.ModRoot;
        workspace.OutputRoot = string.IsNullOrWhiteSpace(workspace.OutputRoot)
            ? "Generated"
            : workspace.OutputRoot;
        workspace.OpenDocuments ??= [];
        workspace.RecentAssets ??= [];
        workspace.MaterialOverrides ??= new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        workspace.RelationshipDecisions ??= new Dictionary<string, string>(StringComparer.Ordinal);
        workspace.Layout ??= new WorkbenchLayoutState();
        workspace.AssetFilter ??= new AssetFilterState();
        workspace.Layout.Normalize();

        if (!Directory.Exists(ResolveModPath(workspace, workspace.ModRoot)))
        {
            warnings.Add("The mod workspace folder is missing.");
        }

        if (!string.IsNullOrWhiteSpace(workspace.GameAssetRoot) &&
            !Directory.Exists(Path.GetFullPath(workspace.GameAssetRoot)))
        {
            warnings.Add("The configured game asset folder is missing. Select a replacement folder.");
        }

        return new WorkspaceLoadResult(workspace, warnings);
    }

    public async Task SaveAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.FilePath))
        {
            throw new InvalidOperationException("The workspace has no file path.");
        }

        _ = ProfileCatalog.Resolve(workspace.ProfileId);
        workspace.FormatVersion = WorkspaceDefinition.CurrentFormatVersion;
        workspace.Layout.Normalize();
        string fullPath = Path.GetFullPath(workspace.FilePath);
        if (!string.IsNullOrWhiteSpace(workspace.GameAssetRoot) &&
            PathPolicy.IsWithin(fullPath, workspace.GameAssetRoot))
        {
            throw new InvalidOperationException(
                "The workspace configuration cannot be written beneath the original game asset root.");
        }

        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The workspace file has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = fullPath + ".tmp";
        await using (FileStream stream = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                workspace,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, fullPath, overwrite: true);
        workspace.FilePath = fullPath;
    }

    public string ResolveModPath(WorkspaceDefinition workspace, string configuredPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        string workspaceDirectory = Path.GetDirectoryName(
            workspace.FilePath ?? throw new InvalidOperationException("Workspace has no file path."))
            ?? throw new InvalidOperationException("Workspace file has no parent directory.");
        return Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(workspaceDirectory, configuredPath));
    }
}

public sealed record ApplicationStateLoadResult(
    ApplicationState State,
    string? Warning);

public sealed class ApplicationStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ApplicationStateService(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public async Task<ApplicationStateLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new ApplicationStateLoadResult(new ApplicationState(), null);
        }

        try
        {
            await using FileStream stream = File.OpenRead(FilePath);
            ApplicationState state = await JsonSerializer.DeserializeAsync<ApplicationState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? new ApplicationState();
            if (state.FormatVersion > ApplicationState.CurrentFormatVersion)
            {
                return new ApplicationStateLoadResult(
                    new ApplicationState(),
                    "Application state is from a newer version and was reset.");
            }

            state.FormatVersion = ApplicationState.CurrentFormatVersion;
            state.RecentWorkspaces ??= [];
            state.RecentStandaloneFiles ??= [];
            state.TutorialProgress ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            state.Window ??= new WindowPlacementState();
            state.Window.Normalize();
            return new ApplicationStateLoadResult(state, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new ApplicationStateLoadResult(
                new ApplicationState(),
                $"Application state could not be read and defaults were used: {exception.Message}");
        }
    }

    public async Task SaveAsync(
        ApplicationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.FormatVersion = ApplicationState.CurrentFormatVersion;
        state.Window.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporaryPath = FilePath + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
