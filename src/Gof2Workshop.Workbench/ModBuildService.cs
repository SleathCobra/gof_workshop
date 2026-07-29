using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gof2Workshop.Workbench;

public sealed record ModManifestAsset(
    string Target,
    string SourceHash,
    string ModFile,
    string Type,
    string BuiltHash);

public sealed record ModManifest(
    int FormatVersion,
    string ModId,
    string Name,
    string Author,
    string Version,
    string TargetProfile,
    IReadOnlyList<ModManifestAsset> Assets);

public enum ModValidationSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ModValidationIssue(
    ModValidationSeverity Severity,
    string? Target,
    string Message);

public sealed record ModValidationResult(
    IReadOnlyList<ModManifestAsset> Assets,
    IReadOnlyList<ModValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != ModValidationSeverity.Error);
}

public sealed record ModBuildReport(
    int FormatVersion,
    string ModId,
    string Version,
    string TargetProfile,
    IReadOnlyList<ModManifestAsset> Assets,
    IReadOnlyList<ModValidationIssue> Issues,
    string ContentSha256);

public sealed record ModBuildResult(
    string OutputDirectory,
    string ManifestPath,
    string ReportPath,
    ModBuildReport Report);

public interface IModBuildService
{
    public Task<ModValidationResult> ValidateAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default);

    public Task<ModBuildResult> BuildAsync(
        WorkspaceDefinition workspace,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts the private operation audit into a versioned distributable manifest. A source asset
/// whose bytes have changed is a hard conflict; an unchanged AddOriginal operation is omitted.
/// </summary>
public sealed class ModBuildService : IModBuildService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WorkspaceService workspaceService;

    public ModBuildService(WorkspaceService? workspaceService = null)
    {
        this.workspaceService = workspaceService ?? new WorkspaceService();
    }

    public async Task<ModValidationResult> ValidateAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        List<ModValidationIssue> issues = [];
        List<ModManifestAsset> assets = [];
        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        string? gameRoot = string.IsNullOrWhiteSpace(workspace.GameAssetRoot)
            ? null
            : Path.GetFullPath(workspace.GameAssetRoot);
        if (gameRoot is null || !Directory.Exists(gameRoot))
        {
            issues.Add(new(
                ModValidationSeverity.Error,
                null,
                "The original game asset root is missing; source hashes cannot be verified."));
            return new ModValidationResult(assets, issues);
        }

        IReadOnlyList<ModAssetOperation> operations = await LoadOperationsAsync(
            modRoot,
            cancellationToken).ConfigureAwait(false);
        foreach (ModAssetOperation operation in operations
                     .GroupBy(item => NormalizeRelative(item.GameRelativePath), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(item => item.CreatedAtUtc).First())
                     .OrderBy(item => NormalizeRelative(item.GameRelativePath), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = NormalizeRelative(operation.GameRelativePath);
            string sourcePath = SafeCombine(gameRoot, target);
            string modRelative = NormalizeRelative(operation.ModRelativePath);
            string stagedPath = SafeCombine(modRoot, modRelative);
            if (!File.Exists(sourcePath))
            {
                issues.Add(new(ModValidationSeverity.Error, target, "Original source asset is missing."));
                continue;
            }

            if (!File.Exists(stagedPath))
            {
                issues.Add(new(ModValidationSeverity.Error, target, "Staged mod file is missing."));
                continue;
            }

            string currentSourceHash = await HashFileAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    currentSourceHash,
                    operation.OriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    ModValidationSeverity.Error,
                    target,
                    "Original source hash changed after staging. Restage or resolve the conflict."));
                continue;
            }

            string currentStagedHash = await HashFileAsync(stagedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    currentStagedHash,
                    operation.StagedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    ModValidationSeverity.Error,
                    target,
                    "Staged file changed after its validation record was written."));
                continue;
            }

            if (string.Equals(currentSourceHash, currentStagedHash, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    ModValidationSeverity.Warning,
                    target,
                    "Staged bytes are identical to the original and were omitted from the build."));
                continue;
            }

            assets.Add(new ModManifestAsset(
                target,
                currentSourceHash,
                modRelative,
                "replace",
                currentStagedHash));
        }

        if (operations.Count == 0)
        {
            issues.Add(new(ModValidationSeverity.Warning, null, "No staged asset operations exist."));
        }

        return new ModValidationResult(assets, issues);
    }

    public async Task<ModBuildResult> BuildAsync(
        WorkspaceDefinition workspace,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ModValidationResult validation = await ValidateAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Mod validation failed. Resolve errors before building.");
        }

        string modRoot = workspaceService.ResolveModPath(workspace, workspace.ModRoot);
        string defaultOutput = Path.Combine(
            workspaceService.ResolveModPath(workspace, workspace.OutputRoot),
            "Build");
        string output = PathPolicy.ValidateExportDestination(
            outputDirectory ?? defaultOutput,
            workspace.GameAssetRoot);
        if (PathPolicy.IsWithin(output, modRoot) is false && outputDirectory is null)
        {
            throw new InvalidOperationException("Default build output escaped the mod workspace.");
        }

        string parent = Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException("Build output has no parent directory.");
        Directory.CreateDirectory(parent);
        string temporary = output + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (ModManifestAsset asset in validation.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = SafeCombine(modRoot, asset.ModFile);
                string destination = SafeCombine(
                    temporary,
                    Path.Combine("Assets", asset.Target.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }

            ModManifest manifest = new(
                FormatVersion: 1,
                workspace.ModId,
                workspace.Name,
                workspace.Author,
                workspace.ModVersion,
                workspace.ProfileId,
                validation.Assets);
            string manifestPath = Path.Combine(temporary, "mod.gof2manifest.json");
            await WriteJsonAtomicContentAsync(manifestPath, manifest, cancellationToken)
                .ConfigureAwait(false);
            string contentHash = await HashTreeAsync(temporary, cancellationToken)
                .ConfigureAwait(false);
            ModBuildReport report = new(
                FormatVersion: 1,
                workspace.ModId,
                workspace.ModVersion,
                workspace.ProfileId,
                validation.Assets,
                validation.Issues,
                contentHash);
            string reportPath = Path.Combine(temporary, "build-report.json");
            await WriteJsonAtomicContentAsync(reportPath, report, cancellationToken)
                .ConfigureAwait(false);

            if (Directory.Exists(output))
            {
                string resolvedOutput = Path.GetFullPath(output);
                string resolvedParent = Path.GetFullPath(parent);
                if (!PathPolicy.IsWithin(resolvedOutput, resolvedParent)
                    || string.Equals(resolvedOutput, resolvedParent, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Refusing to clean an unsafe build path.");
                }

                Directory.Delete(resolvedOutput, recursive: true);
            }

            Directory.Move(temporary, output);
            return new ModBuildResult(
                output,
                Path.Combine(output, "mod.gof2manifest.json"),
                Path.Combine(output, "build-report.json"),
                report);
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            throw;
        }
    }

    private static async Task<IReadOnlyList<ModAssetOperation>> LoadOperationsAsync(
        string modRoot,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(modRoot, ".work", "asset-operations.json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ModAssetOperation>>(
            input,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? [];
    }

    private static string NormalizeRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe manifest path '{path}'.");
        }

        return normalized;
    }

    private static string SafeCombine(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(
            Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathPolicy.IsWithin(fullPath, fullRoot)
            || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relative}' escapes its declared root.");
        }

        return fullPath;
    }

    private static async Task WriteJsonAtomicContentAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await using (FileStream output = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: false);
    }

    private static async Task<string> HashTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        using IncrementalHash aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            aggregate.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            await using FileStream input = File.OpenRead(path);
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                aggregate.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexStringLower(aggregate.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
