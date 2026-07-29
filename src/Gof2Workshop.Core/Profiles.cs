namespace Gof2Workshop.Core;

public enum AssetEndianness
{
    Little,
    Big,
}

public sealed record AssetPlatformProfile(
    string Id,
    string DisplayName,
    AssetEndianness Endianness,
    IReadOnlySet<int> SupportedAemVersions,
    IReadOnlySet<byte> ExpectedAeiFormats,
    string SourceCoordinateConvention,
    string Notes);

public static class ProfileCatalog
{
    public static AssetPlatformProfile Pc1X { get; } = new(
        "pc-1x",
        "PC 1.x",
        AssetEndianness.Little,
        new HashSet<int> { 1, 2, 3, 4, 5 },
        new HashSet<byte> { 0x01, 0x03, 0x20, 0x22, 0x24, 0x26, 0x81 },
        "Source XYZ preserved; handedness and up-axis remain under validation.",
        "AEM v1-v5 and desktop/mobile AEI decoding are implemented.");

    public static AssetPlatformProfile Android { get; } = new(
        "android",
        "Android",
        AssetEndianness.Little,
        new HashSet<int> { 1, 2, 3, 4, 5 },
        new HashSet<byte> { 0x11, 0x13, 0x17, 0x40, 0x42 },
        "Not yet normalized.",
        "Mobile PVRTC, ATC, ETC1, and ETC2 decoding is available; platform variants remain explicit.");

    public static IReadOnlyList<AssetPlatformProfile> All { get; } = [Pc1X, Android];

    public static AssetPlatformProfile Resolve(string id)
    {
        AssetPlatformProfile? match = All.FirstOrDefault(
            profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new ArgumentException(
            $"Unknown profile '{id}'. Available profiles: {string.Join(", ", All.Select(profile => profile.Id))}.",
            nameof(id));
    }
}
