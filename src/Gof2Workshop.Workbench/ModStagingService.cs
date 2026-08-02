using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.GameData;

namespace Gof2Workshop.Workbench;

public enum ModAssetOperationKind
{
    AddOriginal,
    Replace,
    Add,
}

public sealed record ModAssetOperation(
    Guid Id,
    ModAssetOperationKind Kind,
    AssetKind AssetKind,
    string GameRelativePath,
    string ModRelativePath,
    string OriginalSha256,
    string StagedSha256,
    DateTimeOffset CreatedAtUtc);

public sealed record ModStagingResult(
    string StagedPath,
    ModAssetOperation Operation);

public interface IModStagingService
{
    public Task<ModStagingResult> AddOriginalAsync(
        WorkspaceDefinition workspace,
        IndexedAsset source,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    public Task<ModStagingResult> StageReplacementAsync(
        WorkspaceDefinition workspace,
        IndexedAsset source,
        string replacementPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    public Task<ModStagingResult> StageGeneratedReplacementAsync(
        WorkspaceDefinition workspace,
        string gameRelativePath,
        AssetKind kind,
        string replacementPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    public Task<ModStagingResult> StageNewAssetAsync(
        WorkspaceDefinition workspace,
        string gameRelativePath,
        AssetKind kind,
        string authoredPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);
}

public sealed class ModStagingService : IModStagingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IWorkspaceService workspaceService;

    public ModStagingService(IWorkspaceService? workspaceService = null)
    {
        this.workspaceService = workspaceService ?? new WorkspaceService();
    }

    public Task<ModStagingResult> AddOriginalAsync(
        WorkspaceDefinition workspace,
        IndexedAsset source,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        return StageAsync(
            workspace,
            source,
            source.FullPath,
            ModAssetOperationKind.AddOriginal,
            overwrite,
            cancellationToken);
    }

    public Task<ModStagingResult> StageReplacementAsync(
        WorkspaceDefinition workspace,
        IndexedAsset source,
        string replacementPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        return StageAsync(
            workspace,
            source,
            replacementPath,
            ModAssetOperationKind.Replace,
            overwrite,
            cancellationToken);
    }

    public Task<ModStagingResult> StageGeneratedReplacementAsync(
        WorkspaceDefinition workspace,
        string gameRelativePath,
        AssetKind kind,
        string replacementPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRelativePath);
        string gameRoot = Path.GetFullPath(
            workspace.GameAssetRoot
            ?? throw new InvalidOperationException("The workspace has no game asset root."));
        string normalized = NormalizeRelativeAssetPath(gameRelativePath);
        string original = Path.GetFullPath(Path.Combine(gameRoot, normalized));
        if (!PathPolicy.IsWithin(original, gameRoot) || !File.Exists(original))
        {
            throw new FileNotFoundException(
                "The original structured asset is missing or outside the immutable game root.", original);
        }

        FileInfo info = new(original);
        IndexedAsset source = new(
            original,
            normalized.Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetFileName(original),
            kind,
            AssetOwnership.Game,
            info.Length,
            info.LastWriteTimeUtc,
            "Structured replacement source",
            null,
            AssetSupport.Supported,
            true,
            null);
        return StageAsync(
            workspace,
            source,
            replacementPath,
            ModAssetOperationKind.Replace,
            overwrite,
            cancellationToken);
    }

    public async Task<ModStagingResult> StageNewAssetAsync(
        WorkspaceDefinition workspace,
        string gameRelativePath,
        AssetKind kind,
        string authoredPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoredPath);
        string input = Path.GetFullPath(authoredPath);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("The authored asset does not exist.", input);
        }

        string gameRoot = Path.GetFullPath(
            workspace.GameAssetRoot
            ?? throw new InvalidOperationException("The workspace has no game asset root."));
        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        if (PathPolicy.IsWithin(modRoot, gameRoot))
        {
            throw new InvalidOperationException("The mod workspace cannot be located beneath the game asset root.");
        }
        string target = NormalizeRelativeAssetPath(gameRelativePath).Replace('\\', '/');
        string requiredExtension = RequiredExtension(kind);
        if (!Path.GetExtension(target).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"A {kind} target must use the {requiredExtension} extension.");
        }
        string original = Path.GetFullPath(Path.Combine(gameRoot, target));
        if (PathPolicy.IsWithin(original, gameRoot) && File.Exists(original))
        {
            throw new InvalidOperationException("The target already exists in the game root; use validated replacement staging instead.");
        }

        await ValidateFormatAsync(input, kind, workspace.ProfileId, cancellationToken).ConfigureAwait(false);
        string category = Category(kind);
        string categoryRoot = Path.GetFullPath(Path.Combine(modRoot, "Assets", category));
        string destination = Path.GetFullPath(Path.Combine(categoryRoot, target.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathPolicy.IsWithin(destination, categoryRoot))
        {
            throw new InvalidOperationException("The authored asset path escapes its mod-owned category.");
        }
        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException($"A mod-owned asset already exists at '{Path.GetRelativePath(modRoot, destination)}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp";
        File.Copy(input, temporary, overwrite: true);
        File.Move(temporary, destination, overwrite);
        string stagedHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
        ModAssetOperation operation = new(
            Guid.NewGuid(), ModAssetOperationKind.Add, kind, target,
            Path.GetRelativePath(modRoot, destination), string.Empty, stagedHash, DateTimeOffset.UtcNow);
        await AppendManifestAsync(modRoot, operation, cancellationToken).ConfigureAwait(false);
        return new ModStagingResult(destination, operation);
    }

    private async Task<ModStagingResult> StageAsync(
        WorkspaceDefinition workspace,
        IndexedAsset source,
        string inputPath,
        ModAssetOperationKind operationKind,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Ownership != AssetOwnership.Game)
        {
            throw new InvalidOperationException("Only an indexed original game asset can be staged.");
        }

        string gameRoot = Path.GetFullPath(
            workspace.GameAssetRoot
            ?? throw new InvalidOperationException("The workspace has no game asset root."));
        string sourcePath = Path.GetFullPath(source.FullPath);
        if (!PathPolicy.IsWithin(sourcePath, gameRoot))
        {
            throw new InvalidOperationException("The indexed source is outside the selected game asset root.");
        }

        string fullInput = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInput))
        {
            throw new FileNotFoundException("The replacement asset does not exist.", fullInput);
        }

        string requiredExtension = RequiredExtension(source.Kind);
        if (!Path.GetExtension(fullInput).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"A {source.Kind} replacement must use the {requiredExtension} extension.");
        }

        await ValidateFormatAsync(fullInput, source.Kind, workspace.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        if (PathPolicy.IsWithin(modRoot, gameRoot))
        {
            throw new InvalidOperationException("The mod workspace cannot be located beneath the game asset root.");
        }

        string category = Category(source.Kind);
        string safeRelative = NormalizeRelativeAssetPath(source.RelativePath);
        string destination = Path.GetFullPath(
            Path.Combine(modRoot, "Assets", category, safeRelative));
        string categoryRoot = Path.GetFullPath(Path.Combine(modRoot, "Assets", category));
        if (!PathPolicy.IsWithin(destination, categoryRoot))
        {
            throw new InvalidOperationException("The staged asset path escapes its mod-owned category.");
        }

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException(
                $"A mod-owned asset already exists at '{Path.GetRelativePath(modRoot, destination)}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporaryPath = destination + ".tmp";
        await using (FileStream sourceStream = new(
            fullInput,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream destinationStream = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous))
        {
            await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, destination, overwrite);
        ModAssetOperation operation = new(
            Guid.NewGuid(),
            operationKind,
            source.Kind,
            Path.GetRelativePath(gameRoot, sourcePath),
            Path.GetRelativePath(modRoot, destination),
            await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false),
            await HashFileAsync(destination, cancellationToken).ConfigureAwait(false),
            DateTimeOffset.UtcNow);
        await AppendManifestAsync(modRoot, operation, cancellationToken).ConfigureAwait(false);
        return new ModStagingResult(destination, operation);
    }

    private static async Task ValidateFormatAsync(
        string path,
        AssetKind kind,
        string profileId,
        CancellationToken cancellationToken)
    {
        AssetPlatformProfile profile = ProfileCatalog.Resolve(profileId);
        await Task.Run(
            () =>
            {
                if (kind == AssetKind.Aei)
                {
                    _ = new AeiParser().Parse(
                        path,
                        new AeiParserOptions(profile),
                        cancellationToken);
                }
                else if (kind == AssetKind.Aem)
                {
                    _ = new AemParser().Parse(
                        path,
                        new AemParserOptions(profile),
                        cancellationToken);
                }
                else if (kind == AssetKind.GameData)
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    GameDataDocument document = new GameDataFormatRegistry().Parse(Path.GetFileName(path), bytes);
                    if (!new GameDataEditSession(document).Write().AsSpan().SequenceEqual(bytes))
                    {
                        throw new InvalidDataException("Structured BIN failed unchanged reconstruction validation.");
                    }
                }
                else if (kind == AssetKind.Language)
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    using MemoryStream input = new(bytes, writable: false);
                    LanguageTable table = new LanguageTableParser().Parse(input, path);
                    _ = new LanguageTableWriter().Write(table);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeRelativeAssetPath(string relativePath)
    {
        string normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The indexed asset has an unsafe relative path.");
        }

        return normalized;
    }

    private static string Category(AssetKind kind) => kind switch
    {
        AssetKind.Aei => "Textures",
        AssetKind.Aem => "Models",
        AssetKind.GameData => "Data",
        AssetKind.Language => "Languages",
        _ => throw new NotSupportedException($"No mod asset category is defined for {kind}."),
    };

    private static string RequiredExtension(AssetKind kind) => kind switch
    {
        AssetKind.Aei => ".aei",
        AssetKind.Aem => ".aem",
        AssetKind.GameData => ".bin",
        AssetKind.Language => ".lang",
        _ => throw new NotSupportedException($"{kind} staging is not supported."),
    };

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task AppendManifestAsync(
        string modRoot,
        ModAssetOperation operation,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(modRoot, ".work", "asset-operations.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<ModAssetOperation> operations = [];
        if (File.Exists(path))
        {
            await using FileStream input = File.OpenRead(path);
            operations = await JsonSerializer.DeserializeAsync<List<ModAssetOperation>>(
                input,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? [];
        }

        operations.Add(operation);
        string temporaryPath = path + ".tmp";
        await using (FileStream output = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                output,
                operations,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
