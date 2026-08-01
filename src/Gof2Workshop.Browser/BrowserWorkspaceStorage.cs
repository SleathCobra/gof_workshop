using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Gof2Workshop.GameData;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.Browser;

internal sealed record BrowserWorkspaceArchive(
    int FormatVersion,
    string Profile,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<BrowserArchiveAsset> Assets);

internal sealed record BrowserArchiveAsset(
    string Name,
    string BytesBase64,
    AeiRecoveryDocument? AeiRecovery = null,
    IReadOnlyList<GameDataEditOperation>? GameDataOperations = null);

internal sealed record BrowserStorageEstimate(long Usage, long Quota);

[JsonSerializable(typeof(BrowserWorkspaceArchive))]
[JsonSerializable(typeof(BrowserStorageEstimate))]
[JsonSerializable(typeof(AeiRecoveryDocument))]
[JsonSerializable(typeof(GameDataEditOperation))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BrowserWorkspaceJsonContext : JsonSerializerContext;

[SupportedOSPlatform("browser")]
internal static partial class BrowserWorkspaceStorage
{
    internal const string LastWorkspaceKey = "workspace:last";
    internal const string RecoveryKey = "recovery:last";
    internal const long MaximumPersistedAssetBytes = 64L * 1024 * 1024;

    internal static string Serialize(BrowserAssetSession session, string profile)
    {
        long size = session.Assets.Sum(asset => (long)asset.Bytes.Length);
        if (size > MaximumPersistedAssetBytes)
        {
            throw new InvalidOperationException(
                $"Browser workspace persistence is capped at {MaximumPersistedAssetBytes / 1048576} MiB; this collection is {size / 1048576d:F1} MiB.");
        }

        BrowserWorkspaceArchive archive = new(
            1,
            profile,
            DateTimeOffset.UtcNow,
            session.Assets.Select(asset => new BrowserArchiveAsset(
                asset.Name,
                Convert.ToBase64String(asset.Bytes),
                asset.AeiEditSession?.IsDirty == true
                    ? asset.AeiEditSession.CreateRecoveryDocument()
                    : null,
                asset.GameDataSession?.AppliedOperations.Count > 0
                    ? asset.GameDataSession.AppliedOperations
                    : null)).ToArray());
        return JsonSerializer.Serialize(archive, BrowserWorkspaceJsonContext.Default.BrowserWorkspaceArchive);
    }

    internal static BrowserWorkspaceArchive Deserialize(string json)
    {
        BrowserWorkspaceArchive archive = JsonSerializer.Deserialize(
            json,
            BrowserWorkspaceJsonContext.Default.BrowserWorkspaceArchive)
            ?? throw new InvalidDataException("The browser workspace archive is empty.");
        if (archive.FormatVersion != 1)
        {
            throw new InvalidDataException($"Unsupported browser workspace version {archive.FormatVersion}.");
        }

        long decodedEstimate = archive.Assets.Sum(asset => (long)asset.BytesBase64.Length * 3 / 4);
        if (decodedEstimate > MaximumPersistedAssetBytes)
        {
            throw new InvalidDataException("The browser workspace archive exceeds the 64 MiB persisted-asset limit.");
        }

        return archive;
    }

    internal static string SourceHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    [JSImport("saveWorkspace", "./workshopStorage.js")]
    internal static partial Task<string> SaveAsync(string key, string json);

    [JSImport("loadWorkspace", "./workshopStorage.js")]
    internal static partial Task<string?> LoadAsync(string key);

    [JSImport("removeWorkspace", "./workshopStorage.js")]
    internal static partial Task<string> RemoveAsync(string key);

    [JSImport("getStorageEstimate", "./workshopStorage.js")]
    internal static partial Task<string> GetStorageEstimateAsync();

    [JSImport("clearAllWorkshopData", "./workshopStorage.js")]
    internal static partial Task<string> ClearAllAsync();
}
