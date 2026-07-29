using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;

namespace Gof2Workshop.Workbench;

public enum EditValidationState
{
    NotValidated,
    Valid,
    Warning,
    Invalid,
    Conflict,
}

public sealed record AeiReplaceRegionOperation(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    int RegionIndex,
    int Width,
    int Height,
    byte[] RgbaPixels,
    string DisplayName = "Replace atlas region");

public sealed record AeiRecoveryDocument(
    int FormatVersion,
    string SourceGameRelativePath,
    string OriginalSourceSha256,
    string ModRelativeOutputPath,
    int AppliedOperationCount,
    IReadOnlyList<AeiReplaceRegionOperation> Operations,
    DateTimeOffset SavedAtUtc);

public interface IUndoRedoService
{
    public bool CanUndo { get; }

    public bool CanRedo { get; }

    public string? UndoDescription { get; }

    public string? RedoDescription { get; }

    public void Undo();

    public void Redo();
}

public interface IEditSession : IUndoRedoService
{
    public string SourceGameRelativePath { get; }

    public string OriginalSourceSha256 { get; }

    public string ModRelativeOutputPath { get; }

    public bool IsDirty { get; }

    public EditValidationState ValidationState { get; }

    public event EventHandler? Changed;
}

/// <summary>
/// Operation-based AEI session. Parser snapshots and the original decoded image are never mutated.
/// </summary>
public sealed class AeiEditSession : IEditSession
{
    private readonly List<AeiReplaceRegionOperation> operations = [];
    private int appliedOperationCount;

    public AeiEditSession(
        string sourceGameRelativePath,
        string originalSourceSha256,
        string modRelativeOutputPath,
        AeiFile originalFile,
        RgbaImage originalAtlas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceGameRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalSourceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(modRelativeOutputPath);
        SourceGameRelativePath = sourceGameRelativePath;
        OriginalSourceSha256 = originalSourceSha256;
        ModRelativeOutputPath = modRelativeOutputPath;
        OriginalFile = originalFile ?? throw new ArgumentNullException(nameof(originalFile));
        OriginalAtlas = originalAtlas ?? throw new ArgumentNullException(nameof(originalAtlas));
        WorkingAtlas = Clone(originalAtlas);
    }

    public string SourceGameRelativePath { get; }

    public string OriginalSourceSha256 { get; }

    public string ModRelativeOutputPath { get; }

    public AeiFile OriginalFile { get; }

    public RgbaImage OriginalAtlas { get; }

    public RgbaImage WorkingAtlas { get; private set; }

    public IReadOnlyList<AeiReplaceRegionOperation> Operations => operations;

    public int AppliedOperationCount => appliedOperationCount;

    public bool IsDirty => appliedOperationCount != 0;

    public bool CanUndo => appliedOperationCount > 0;

    public bool CanRedo => appliedOperationCount < operations.Count;

    public string? UndoDescription => CanUndo
        ? operations[appliedOperationCount - 1].DisplayName
        : null;

    public string? RedoDescription => CanRedo
        ? operations[appliedOperationCount].DisplayName
        : null;

    public EditValidationState ValidationState { get; private set; }

    public AeiEncodingResult? LastValidation { get; private set; }

    public event EventHandler? Changed;

    public AeiReplaceRegionOperation ReplaceRegion(int regionIndex, RgbaImage replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        AeiRegion region = OriginalFile.Regions.FirstOrDefault(candidate => candidate.Index == regionIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(regionIndex));
        if (replacement.Width != region.Width || replacement.Height != region.Height)
        {
            throw new InvalidDataException(
                $"Replacement is {replacement.Width}x{replacement.Height}; " +
                $"region {region.Index} requires {region.Width}x{region.Height}.");
        }

        if (CanRedo)
        {
            operations.RemoveRange(appliedOperationCount, operations.Count - appliedOperationCount);
        }

        AeiReplaceRegionOperation operation = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            region.Index,
            replacement.Width,
            replacement.Height,
            replacement.ReadOnlyPixelBytes.ToArray(),
            $"Replace region {region.Index}");
        operations.Add(operation);
        appliedOperationCount++;
        Rebuild();
        return operation;
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        appliedOperationCount--;
        Rebuild();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        appliedOperationCount++;
        Rebuild();
    }

    public void Revert()
    {
        operations.Clear();
        appliedOperationCount = 0;
        Rebuild();
    }

    public AeiEncodingResult Validate(
        AeiEncodingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            LastValidation = new AeiReconstructionService().ReconstructAndValidate(
                OriginalFile,
                WorkingAtlas,
                options,
                cancellationToken);
            ValidationState = EditValidationState.Valid;
            Changed?.Invoke(this, EventArgs.Empty);
            return LastValidation;
        }
        catch
        {
            ValidationState = EditValidationState.Invalid;
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
    }

    public AeiRecoveryDocument CreateRecoveryDocument()
    {
        return new AeiRecoveryDocument(
            FormatVersion: 1,
            SourceGameRelativePath,
            OriginalSourceSha256,
            ModRelativeOutputPath,
            appliedOperationCount,
            operations.ToArray(),
            DateTimeOffset.UtcNow);
    }

    public void Replay(AeiRecoveryDocument recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.FormatVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported AEI recovery format {recovery.FormatVersion}.");
        }

        if (!string.Equals(
                recovery.OriginalSourceSha256,
                OriginalSourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            ValidationState = EditValidationState.Conflict;
            throw new InvalidDataException(
                "Recovery source hash differs from the selected original asset.");
        }

        if (recovery.AppliedOperationCount < 0
            || recovery.AppliedOperationCount > recovery.Operations.Count)
        {
            throw new InvalidDataException("Recovery operation cursor is outside the operation log.");
        }

        operations.Clear();
        foreach (AeiReplaceRegionOperation operation in recovery.Operations)
        {
            ValidateOperation(operation);
            operations.Add(operation);
        }

        appliedOperationCount = recovery.AppliedOperationCount;
        Rebuild();
    }

    private void Rebuild()
    {
        RgbaImage current = Clone(OriginalAtlas);
        for (int index = 0; index < appliedOperationCount; index++)
        {
            AeiReplaceRegionOperation operation = operations[index];
            ValidateOperation(operation);
            AeiRegion region = OriginalFile.Regions.First(candidate => candidate.Index == operation.RegionIndex);
            current = AeiAtlasEditing.ReplaceRegion(
                current,
                region,
                new RgbaImage(operation.Width, operation.Height, operation.RgbaPixels));
        }

        WorkingAtlas = current;
        LastValidation = null;
        ValidationState = EditValidationState.NotValidated;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ValidateOperation(AeiReplaceRegionOperation operation)
    {
        AeiRegion region = OriginalFile.Regions.FirstOrDefault(
            candidate => candidate.Index == operation.RegionIndex)
            ?? throw new InvalidDataException(
                $"Recovery references missing region {operation.RegionIndex}.");
        if (operation.Width != region.Width || operation.Height != region.Height)
        {
            throw new InvalidDataException(
                $"Recovery region {region.Index} dimensions do not match the source.");
        }

        int required = checked(operation.Width * operation.Height * 4);
        if (operation.RgbaPixels.Length != required)
        {
            throw new InvalidDataException(
                $"Recovery region {region.Index} has {operation.RgbaPixels.Length} bytes; " +
                $"{required} are required.");
        }
    }

    private static RgbaImage Clone(RgbaImage image)
    {
        return new RgbaImage(image.Width, image.Height, image.ReadOnlyPixelBytes);
    }
}

public interface IRecoveryService
{
    public Task SaveAsync(
        string modRoot,
        AeiEditSession session,
        CancellationToken cancellationToken = default);

    public Task<AeiRecoveryDocument?> LoadAsync(
        string modRoot,
        string sourceGameRelativePath,
        CancellationToken cancellationToken = default);

    public Task DiscardAsync(
        string modRoot,
        string sourceGameRelativePath,
        CancellationToken cancellationToken = default);
}

public sealed class RecoveryService : IRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task SaveAsync(
        string modRoot,
        AeiEditSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        string path = GetPath(modRoot, session.SourceGameRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        await using (FileStream output = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                output,
                session.CreateRecoveryDocument(),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public async Task<AeiRecoveryDocument?> LoadAsync(
        string modRoot,
        string sourceGameRelativePath,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(modRoot, sourceGameRelativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AeiRecoveryDocument>(
            input,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Recovery document is empty.");
    }

    public Task DiscardAsync(
        string modRoot,
        string sourceGameRelativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(modRoot, sourceGameRelativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string GetPath(string modRoot, string sourceGameRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceGameRelativePath);
        string root = Path.GetFullPath(Path.Combine(modRoot, ".work", "recovery"));
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            sourceGameRelativePath.Replace('\\', '/').ToLowerInvariant()));
        string path = Path.GetFullPath(Path.Combine(root, $"{Convert.ToHexStringLower(hash)}.json"));
        if (!PathPolicy.IsWithin(path, root))
        {
            throw new InvalidOperationException("Recovery path escapes the workspace.");
        }

        return path;
    }
}
