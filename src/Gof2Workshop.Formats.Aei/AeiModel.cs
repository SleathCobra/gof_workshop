using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aei;

public enum AeiCompressionFormat
{
    Unknown,
    UncompressedUi,
    Uncompressed,
    UncompressedCubeMapPc,
    UncompressedCubeMap,
    Pvrtc2Rgba,
    Pvrtc4Rgba,
    Atc,
    Dxt1,
    Dxt3,
    Dxt5,
    Etc1,
    Etc2,
}

public sealed record AeiFormatDescriptor(
    byte RawId,
    byte BaseId,
    AeiCompressionFormat Format,
    bool HasMipmaps,
    bool IsCompressed,
    string DisplayName)
{
    public bool IsRecognized => Format != AeiCompressionFormat.Unknown;

    public static AeiFormatDescriptor Identify(byte rawId)
    {
        if (TryGetKnown(rawId, out AeiFormatDescriptor? exact))
        {
            return exact!;
        }

        bool hasMipmaps = (rawId & 0x02) != 0;
        byte baseId = (byte)(rawId & 0xFD);
        if (hasMipmaps && TryGetKnown(baseId, out AeiFormatDescriptor? baseFormat))
        {
            return baseFormat! with
            {
                RawId = rawId,
                HasMipmaps = true,
                DisplayName = $"{baseFormat.DisplayName} + mipmaps",
            };
        }

        return new AeiFormatDescriptor(
            rawId,
            rawId,
            AeiCompressionFormat.Unknown,
            hasMipmaps,
            IsCompressed: true,
            $"Unknown (0x{rawId:X2})");
    }

    private static bool TryGetKnown(byte id, out AeiFormatDescriptor? descriptor)
    {
        descriptor = id switch
        {
            0x01 => Raw(id, AeiCompressionFormat.UncompressedUi, "Raw RGBA UI"),
            0x03 => Raw(id, AeiCompressionFormat.Uncompressed, "Raw RGBA"),
            0x81 => Raw(id, AeiCompressionFormat.UncompressedCubeMapPc, "Raw RGBA PC cube map"),
            0xC2 => Raw(id, AeiCompressionFormat.UncompressedCubeMap, "Raw RGBA cube map"),
            0x0D => Compressed(id, AeiCompressionFormat.Pvrtc2Rgba, "PVRTC 2bpp RGBA"),
            0x10 => Compressed(id, AeiCompressionFormat.Pvrtc4Rgba, "PVRTC 4bpp RGBA"),
            0x11 => Compressed(id, AeiCompressionFormat.Atc, "ATC"),
            0x17 => Compressed(id, AeiCompressionFormat.Etc2, "ETC2"),
            0x20 => Compressed(id, AeiCompressionFormat.Dxt1, "DXT1 / BC1"),
            0x21 => Compressed(id, AeiCompressionFormat.Dxt3, "DXT3 / BC2"),
            0x24 => Compressed(id, AeiCompressionFormat.Dxt5, "DXT5 / BC3"),
            0x40 => Compressed(id, AeiCompressionFormat.Etc1, "ETC1"),
            _ => null,
        };

        return descriptor is not null;
    }

    private static AeiFormatDescriptor Raw(
        byte id,
        AeiCompressionFormat format,
        string displayName)
    {
        return new AeiFormatDescriptor(id, id, format, false, false, displayName);
    }

    private static AeiFormatDescriptor Compressed(
        byte id,
        AeiCompressionFormat format,
        string displayName)
    {
        return new AeiFormatDescriptor(id, id, format, false, true, displayName);
    }
}

public sealed record AeiRegion(
    int Index,
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height,
    long OriginalOffset);

public sealed record AeiSymbol(
    char Character,
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height,
    long OriginalOffset);

public sealed record AeiSymbolMap(
    int Index,
    IReadOnlyList<AeiSymbol> Symbols);

public sealed record AeiSurface(
    int ArrayElement,
    int Face,
    int MipLevel,
    int Width,
    int Height,
    int PayloadOffset,
    int PayloadLength);

public sealed record AeiFile(
    string? SourcePath,
    string ProfileId,
    AeiFormatDescriptor Format,
    ushort Width,
    ushort Height,
    IReadOnlyList<AeiRegion> Regions,
    IReadOnlyList<AeiSymbolMap> SymbolMaps,
    IReadOnlyList<AeiSurface> Surfaces,
    byte[] Payload,
    byte[] RawHeader,
    byte? CompressionQuality,
    byte[] UnknownTrailingData,
    long PayloadFileOffset,
    IReadOnlyList<FormatDiagnostic> Diagnostics,
    ParseTrace? Trace)
{
    public int MipLevelCount => Surfaces.Count == 0
        ? 0
        : Surfaces.Max(surface => surface.MipLevel) + 1;

    public int FaceCount => Surfaces.Count == 0
        ? 0
        : Surfaces.Max(surface => surface.Face) + 1;

    public int ArrayElementCount => Surfaces.Count == 0
        ? 0
        : Surfaces.Max(surface => surface.ArrayElement) + 1;
}

public sealed record AeiParserOptions(
    AssetPlatformProfile Profile,
    bool ResearchDiagnostics = false,
    int MaximumPayloadBytes = 512 * 1024 * 1024,
    int MaximumRegionCount = 65_535,
    int MaximumSymbolCount = 65_535)
{
    public static AeiParserOptions Pc1X { get; } = new(ProfileCatalog.Pc1X);
}
