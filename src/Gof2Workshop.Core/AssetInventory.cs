namespace Gof2Workshop.Core;

public enum AssetKind
{
    Aei,
    Aem,
    Language,
}

public sealed record AssetInventoryEntry(
    string RelativePath,
    string FileName,
    AssetKind Kind,
    long Size,
    string Classification,
    string? Version,
    string? Error);

public sealed record AssetInventory(
    string Profile,
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<AssetInventoryEntry> Assets,
    IReadOnlyDictionary<string, int> DuplicateFileNames);
