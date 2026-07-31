namespace Gof2Workshop.Core;

public enum AssetEndianness
{
    Little,
    Big,
}

public enum AssetProduct
{
    GalaxyOnFire2,
    GalaxyOnFire3D,
}

public enum AssetTargetPlatform
{
    Windows,
    Android,
    IOS,
    MacOS,
}

public enum ProfileSupportLevel
{
    Unsupported,
    ResearchReadOnly,
    CorpusValidatedRead,
    CorpusValidatedReadWrite,
}

public enum ProfileValidationConfidence
{
    Hypothesis,
    Synthetic,
    PartialCorpus,
    FullCorpus,
}

public sealed record AssetProfileDetails(
    AssetProduct Product,
    AssetTargetPlatform Platform,
    IReadOnlyList<string> ExpectedAssetRoots,
    IReadOnlyList<string> DatabaseCandidates,
    IReadOnlyList<string> MissionCandidates,
    string NumericEncodings,
    string MipmapConvention,
    string CubeMapConvention,
    string SurfaceConvention,
    string UvOrientation,
    string NamingConvention,
    ProfileSupportLevel AeiReadSupport,
    ProfileSupportLevel AeiWriteSupport,
    ProfileSupportLevel AemReadSupport,
    ProfileSupportLevel AemWriteSupport,
    ProfileValidationConfidence Confidence,
    IReadOnlyList<string> KnownLimitations);

public sealed record AssetPlatformProfile(
    string Id,
    string DisplayName,
    AssetEndianness Endianness,
    IReadOnlySet<int> SupportedAemVersions,
    IReadOnlySet<byte> ExpectedAeiFormats,
    string SourceCoordinateConvention,
    string Notes,
    AssetProfileDetails Details);

public static class ProfileCatalog
{
    public static AssetPlatformProfile Pc1X { get; } = new(
        "gof2-pc-1x",
        "Galaxy on Fire 2 — PC 1.x",
        AssetEndianness.Little,
        new HashSet<int> { 1, 2, 3, 4, 5 },
        new HashSet<byte> { 0x01, 0x03, 0x0D, 0x10, 0x12, 0x20, 0x22, 0x24, 0x26, 0x81 },
        "Source XYZ preserved; handedness and up-axis remain under validation.",
        "The complete local PC AEI/AEM corpus is read-validated; representable files round-trip.",
        Gof2Details(
            AssetTargetPlatform.Windows,
            ["assets", "textures", "meshes"],
            "IEEE-754 geometry for v4/v5; historical fixed-point encodings for v1-v3.",
            "Mip bit 0x02; smallest level is 1x1 with codec block minimums.",
            "0x81 is a vertical six-face raw strip; face order is unresolved.",
            "Single atlas or vertical cube strip; no confirmed arrays.",
            "Raw UV preserved; viewer/glTF uses V-flip.",
            ProfileSupportLevel.CorpusValidatedReadWrite,
            ProfileSupportLevel.CorpusValidatedReadWrite,
            ProfileValidationConfidence.FullCorpus));

    public static AssetPlatformProfile Android { get; } = new(
        "gof2-android",
        "Galaxy on Fire 2 — Android",
        AssetEndianness.Little,
        new HashSet<int> { 1, 2, 3, 4, 5 },
        new HashSet<byte> { 0x01, 0x03, 0x0D, 0x10, 0x12, 0x24, 0x40, 0x42, 0xC2 },
        "Source XYZ preserved; Android transform and UV conventions require visual validation.",
        "Real Android AEI/AEM corpus is available; write support remains disabled until reconstruction is audited.",
        Gof2Details(
            AssetTargetPlatform.Android,
            ["assets", "textures", "meshes"],
            "Mixed historical AEM fixed-point and v4/v5 IEEE-754 geometry.",
            "ETC1 0x42 and PVRTC 0x12 use complete mip chains.",
            "0xC2 is observed as a vertical six-face raw strip; face order is unresolved.",
            "Single atlas or vertical cube strip; array ordering is unresolved.",
            "Raw UV preserved; platform texture orientation is under corpus comparison.",
            ProfileSupportLevel.CorpusValidatedRead,
            ProfileSupportLevel.Unsupported,
            ProfileValidationConfidence.PartialCorpus));

    public static AssetPlatformProfile IOS { get; } = new(
        "gof2-ios",
        "Galaxy on Fire 2 — iOS",
        AssetEndianness.Little,
        new HashSet<int> { 1, 2, 3, 4, 5 },
        new HashSet<byte> { 0x01, 0x03, 0x0D, 0x10, 0x12, 0x24, 0x81 },
        "Source XYZ preserved; iOS transform and UV conventions require visual validation.",
        "PVRTC dominates the real iOS corpus; write support remains research-only.",
        Gof2Details(
            AssetTargetPlatform.IOS,
            ["assets", "textures", "meshes"],
            "Mixed historical AEM fixed-point and v4/v5 IEEE-754 geometry.",
            "PVRTC 0x12 uses a complete chain with PVRTC minimum encoded dimensions.",
            "0x81 vertical raw cube strips occur; face order is unresolved.",
            "Single atlas or vertical cube strip; array ordering is unresolved.",
            "Raw UV preserved; platform texture orientation is under corpus comparison.",
            ProfileSupportLevel.CorpusValidatedRead,
            ProfileSupportLevel.Unsupported,
            ProfileValidationConfidence.PartialCorpus));

    public static AssetPlatformProfile MacOS { get; } = new(
        "gof2-macos",
        "Galaxy on Fire 2 — macOS",
        AssetEndianness.Little,
        new HashSet<int> { 1, 2, 3, 4, 5 },
        new HashSet<byte> { 0x01, 0x03, 0x0D, 0x10, 0x12, 0x20, 0x22, 0x24, 0x26, 0x81, 0xA6 },
        "Source XYZ preserved; desktop coordinate convention is not silently normalized.",
        "The real macOS corpus combines desktop BC textures, PVRTC, and raw cube strips.",
        Gof2Details(
            AssetTargetPlatform.MacOS,
            ["assets", "textures", "meshes"],
            "Mixed historical AEM fixed-point and v4/v5 IEEE-754 geometry.",
            "Desktop BC/PVRTC mip rules; 0xA6 is an observed non-mipped raw cube strip.",
            "0x81 and 0xA6 vertical six-face raw strips occur; face order is unresolved.",
            "Single atlas or vertical cube strip; array ordering is unresolved.",
            "Raw UV preserved; viewer/glTF uses V-flip pending platform visual comparison.",
            ProfileSupportLevel.CorpusValidatedRead,
            ProfileSupportLevel.Unsupported,
            ProfileValidationConfidence.PartialCorpus));

    public static AssetPlatformProfile Gof3DIosResearch { get; } = new(
        "gof3d-ios-research",
        "Galaxy on Fire 3D — iOS Research",
        AssetEndianness.Little,
        new HashSet<int> { 1 },
        new HashSet<byte> { 0x01 },
        "Separate GOF3D/Abyss Engine convention; never inherited by GOF2 profiles.",
        "The supplied GOF3D corpus uses a distinct legacy AEM v1 layout and is intentionally read-only research.",
        new AssetProfileDetails(
            AssetProduct.GalaxyOnFire3D,
            AssetTargetPlatform.IOS,
            ["textures", "models", "txt"],
            ["txt/items.txt", "txt/ships.txt", "txt/systems.txt", "txt/stations.txt"],
            [],
            "Legacy fixed-point AEM layout is structurally distinct and unresolved.",
            "No mipmapped AEI observed in the supplied subset.",
            "No cube-map ordering confirmed.",
            "Nine raw RGBA atlases observed.",
            "Unvalidated.",
            "Legacy text tables and numbered model/texture resources.",
            ProfileSupportLevel.ResearchReadOnly,
            ProfileSupportLevel.Unsupported,
            ProfileSupportLevel.ResearchReadOnly,
            ProfileSupportLevel.Unsupported,
            ProfileValidationConfidence.PartialCorpus,
            ["Legacy AEM v1 record boundaries differ from GOF2 v1.", "Mission semantics are unknown."]));

    public static IReadOnlyList<AssetPlatformProfile> All { get; } =
        [Pc1X, Android, IOS, MacOS, Gof3DIosResearch];

    public static AssetPlatformProfile Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string canonical = id.ToLowerInvariant() switch
        {
            "pc-1x" => Pc1X.Id,
            "android" => Android.Id,
            "ios" => IOS.Id,
            "macos" => MacOS.Id,
            "ios2" or "gof3d-ios" => Gof3DIosResearch.Id,
            _ => id,
        };
        AssetPlatformProfile? match = All.FirstOrDefault(
            profile => string.Equals(profile.Id, canonical, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new ArgumentException(
            $"Unknown profile '{id}'. Available profiles: {string.Join(", ", All.Select(profile => profile.Id))}.",
            nameof(id));
    }

    private static AssetProfileDetails Gof2Details(
        AssetTargetPlatform platform,
        IReadOnlyList<string> roots,
        string numericEncodings,
        string mipmaps,
        string cubeMaps,
        string surfaces,
        string uv,
        ProfileSupportLevel readSupport,
        ProfileSupportLevel writeSupport,
        ProfileValidationConfidence confidence)
    {
        return new AssetProfileDetails(
            AssetProduct.GalaxyOnFire2,
            platform,
            roots,
            ["data", "db", "txt", "assets"],
            ["missions", "mission", "quests", "objectives", "agents", "dialog"],
            numericEncodings,
            mipmaps,
            cubeMaps,
            surfaces,
            uv,
            "External resources primarily use folder/stem/suffix conventions; game-effective material storage is unresolved.",
            readSupport,
            writeSupport,
            readSupport,
            writeSupport,
            confidence,
            ["Cube face order is unresolved.", "Auxiliary AEM float4 semantics are unresolved.", "Game-effective material mapping is unresolved."]);
    }
}
