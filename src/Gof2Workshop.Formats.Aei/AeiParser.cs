using System.Text;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aei;

public sealed class AeiParser
{
    private static readonly byte[] Magic = "AEimage\0"u8.ToArray();

    public AeiFile Parse(
        string path,
        AeiParserOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Parse(stream, path, options, cancellationToken);
    }

    public AeiFile Parse(
        Stream stream,
        string? sourcePath = null,
        AeiParserOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= AeiParserOptions.Pc1X;
        ValidateOptions(options);

        ParseTrace? trace = options.ResearchDiagnostics ? new ParseTrace() : null;
        List<FormatDiagnostic> diagnostics = [];
        using BoundedBinaryReader reader = new(
            stream,
            sourcePath,
            BinaryEndianness.Little,
            trace,
            leaveOpen: true,
            maximumAllocation: options.MaximumPayloadBytes);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] magic = reader.ReadBytes(Magic.Length, "magic", "header");
            if (!magic.AsSpan().SequenceEqual(Magic))
            {
                string observed = Encoding.ASCII.GetString(magic).Replace("\0", "\\0", StringComparison.Ordinal);
                throw reader.Unsupported("magic", $"Expected 'AEimage\\0', observed '{observed}'.", 0);
            }

            byte rawFormat = reader.ReadByte("format", "header");
            AeiFormatDescriptor format = AeiFormatDescriptor.Identify(rawFormat);
            if (!format.IsRecognized)
            {
                throw reader.Unsupported(
                    "format",
                    $"AEI compression identifier 0x{rawFormat:X2} is not recognized.",
                    reader.Position - 1);
            }

            ushort width = reader.ReadUInt16("width", "header");
            ushort height = reader.ReadUInt16("height", "header");
            ValidateDimensions(reader, width, height);

            ushort regionCount = reader.ReadUInt16("regionCount", "header");
            if (regionCount > options.MaximumRegionCount)
            {
                throw reader.Corrupt(
                    "regionCount",
                    $"Region count {regionCount} exceeds configured limit {options.MaximumRegionCount}.");
            }

            List<AeiRegion> regions = new(regionCount);
            for (int index = 0; index < regionCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long offset = reader.Position;
                ushort x = reader.ReadUInt16($"regions[{index}].x", "regions");
                ushort y = reader.ReadUInt16($"regions[{index}].y", "regions");
                ushort regionWidth = reader.ReadUInt16($"regions[{index}].width", "regions");
                ushort regionHeight = reader.ReadUInt16($"regions[{index}].height", "regions");
                regions.Add(new AeiRegion(index, x, y, regionWidth, regionHeight, offset));

                if (regionWidth == 0 || regionHeight == 0)
                {
                    diagnostics.Add(new FormatDiagnostic(
                        DiagnosticSeverity.Warning,
                        "AEI_REGION_EMPTY",
                        $"Region {index} has a zero dimension.",
                        offset,
                        "regions"));
                }

                if ((long)x + regionWidth > width || (long)y + regionHeight > height)
                {
                    diagnostics.Add(new FormatDiagnostic(
                        DiagnosticSeverity.Warning,
                        "AEI_REGION_OUT_OF_BOUNDS",
                        $"Region {index} extends outside the {width}x{height} atlas.",
                        offset,
                        "regions"));
                }
            }

            int payloadLength = format.IsCompressed
                ? ReadCompressedPayloadLength(reader, options)
                : CheckedRawPayloadLength(reader, width, height, options);

            long payloadFileOffset = reader.Position;
            reader.EnsureAvailable((long)payloadLength + sizeof(ushort), "payload");
            byte[] rawHeader = reader.ReadRange(
                0,
                checked((int)payloadFileOffset),
                "rawHeader",
                "preserved");
            byte[] payload = reader.ReadBytes(payloadLength, "payload", "image");

            IReadOnlyList<AeiSurface> surfaces = BuildSurfaces(
                reader,
                format,
                width,
                height,
                payloadLength,
                diagnostics);

            ushort symbolMapCount = reader.ReadUInt16("symbolMapCount", "symbols");
            List<AeiSymbolMap> symbolMaps = new(symbolMapCount);
            for (int mapIndex = 0; mapIndex < symbolMapCount; mapIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ushort symbolCount = reader.ReadUInt16(
                    $"symbolMaps[{mapIndex}].symbolCount",
                    "symbols");
                if (symbolCount > options.MaximumSymbolCount)
                {
                    throw reader.Corrupt(
                        $"symbolMaps[{mapIndex}].symbolCount",
                        $"Symbol count {symbolCount} exceeds configured limit {options.MaximumSymbolCount}.");
                }

                char[] characters = new char[symbolCount];
                for (int symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
                {
                    characters[symbolIndex] = (char)reader.ReadUInt16(
                        $"symbolMaps[{mapIndex}].characters[{symbolIndex}]",
                        "symbols");
                }

                List<AeiSymbol> symbols = new(symbolCount);
                for (int symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
                {
                    long offset = reader.Position;
                    ushort x = reader.ReadUInt16(
                        $"symbolMaps[{mapIndex}].symbols[{symbolIndex}].x",
                        "symbols");
                    ushort y = reader.ReadUInt16(
                        $"symbolMaps[{mapIndex}].symbols[{symbolIndex}].y",
                        "symbols");
                    ushort symbolWidth = reader.ReadUInt16(
                        $"symbolMaps[{mapIndex}].symbols[{symbolIndex}].width",
                        "symbols");
                    ushort symbolHeight = reader.ReadUInt16(
                        $"symbolMaps[{mapIndex}].symbols[{symbolIndex}].height",
                        "symbols");
                    symbols.Add(new AeiSymbol(
                        characters[symbolIndex],
                        x,
                        y,
                        symbolWidth,
                        symbolHeight,
                        offset));
                }

                symbolMaps.Add(new AeiSymbolMap(mapIndex, symbols));
            }

            byte? quality = null;
            byte[] unknownTrailingData = [];
            if (reader.Remaining == 1)
            {
                byte candidate = reader.ReadByte("compressionQuality", "footer");
                if (candidate is >= 1 and <= 3)
                {
                    quality = candidate;
                }
                else
                {
                    unknownTrailingData = [candidate];
                    diagnostics.Add(new FormatDiagnostic(
                        DiagnosticSeverity.Warning,
                        "AEI_UNKNOWN_FOOTER",
                        $"Unrecognized one-byte footer value 0x{candidate:X2}.",
                        reader.Position - 1,
                        "footer"));
                }
            }
            else if (reader.Remaining > 0)
            {
                if (reader.Remaining > options.MaximumPayloadBytes)
                {
                    throw reader.Corrupt(
                        "unknownTrailingData",
                        $"Trailing byte count {reader.Remaining} exceeds the allocation limit.");
                }

                unknownTrailingData = reader.ReadBytes(
                    checked((int)reader.Remaining),
                    "unknownTrailingData",
                    "footer");
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEI_UNKNOWN_TRAILING_DATA",
                    $"Preserved {unknownTrailingData.Length} uninterpreted trailing bytes.",
                    reader.Position - unknownTrailingData.Length,
                    "footer"));
            }

            if (!options.Profile.ExpectedAeiFormats.Contains(rawFormat))
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEI_PROFILE_MISMATCH",
                    $"Format 0x{rawFormat:X2} is not expected by profile '{options.Profile.Id}'.",
                    8,
                    "header"));
            }

            return new AeiFile(
                sourcePath,
                options.Profile.Id,
                format,
                width,
                height,
                regions,
                symbolMaps,
                surfaces,
                payload,
                rawHeader,
                quality,
                unknownTrailingData,
                payloadFileOffset,
                diagnostics,
                trace);
        }
        catch (FormatParseException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new FormatParseException(
                FormatFailureKind.Corrupt,
                sourcePath,
                reader.Position,
                "size",
                "An integer overflow occurred while validating file-controlled sizes.",
                exception);
        }
    }

    private static int ReadCompressedPayloadLength(
        BoundedBinaryReader reader,
        AeiParserOptions options)
    {
        uint rawLength = reader.ReadUInt32("payloadLength", "image");
        if (rawLength > options.MaximumPayloadBytes)
        {
            throw reader.Corrupt(
                "payloadLength",
                $"Payload length {rawLength} exceeds configured limit {options.MaximumPayloadBytes}.");
        }

        return checked((int)rawLength);
    }

    private static int CheckedRawPayloadLength(
        BoundedBinaryReader reader,
        ushort width,
        ushort height,
        AeiParserOptions options)
    {
        long length = (long)width * height * 4;
        if (length > options.MaximumPayloadBytes)
        {
            throw reader.Corrupt(
                "payload",
                $"Raw payload length {length} exceeds configured limit {options.MaximumPayloadBytes}.");
        }

        return checked((int)length);
    }

    private static void ValidateDimensions(BoundedBinaryReader reader, ushort width, ushort height)
    {
        if (width == 0 || height == 0)
        {
            throw reader.Corrupt("dimensions", $"Invalid image dimensions {width}x{height}.");
        }

        if (width > RgbaImage.MaximumDimension || height > RgbaImage.MaximumDimension)
        {
            throw reader.Corrupt(
                "dimensions",
                $"Dimensions {width}x{height} exceed the supported limit {RgbaImage.MaximumDimension}.");
        }

        if ((long)width * height > RgbaImage.MaximumPixelCount)
        {
            throw reader.Corrupt(
                "dimensions",
                $"Pixel count {(long)width * height} exceeds the supported limit {RgbaImage.MaximumPixelCount}.");
        }
    }

    private static IReadOnlyList<AeiSurface> BuildSurfaces(
        BoundedBinaryReader reader,
        AeiFormatDescriptor format,
        int width,
        int height,
        int payloadLength,
        List<FormatDiagnostic> diagnostics)
    {
        if (!format.IsCompressed)
        {
            if (format.Format is AeiCompressionFormat.UncompressedCubeMapPc
                or AeiCompressionFormat.UncompressedCubeMap
                && height == width * 6)
            {
                int faceLength = checked(width * width * 4);
                return Enumerable.Range(0, 6)
                    .Select(face => new AeiSurface(0, face, 0, width, width, face * faceLength, faceLength))
                    .ToArray();
            }

            if (format.Format is AeiCompressionFormat.UncompressedCubeMapPc
                or AeiCompressionFormat.UncompressedCubeMap)
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEI_CUBEMAP_LAYOUT_UNKNOWN",
                    $"Cube-map dimensions {width}x{height} are not the observed vertical six-face layout.",
                    8,
                    "image"));
            }

            return [new AeiSurface(0, 0, 0, width, height, 0, payloadLength)];
        }

        List<AeiSurface> surfaces = [];
        int offset = 0;
        int mip = 0;
        int mipWidth = width;
        int mipHeight = height;

        while (true)
        {
            int? levelLength = GetLevelLength(format.Format, mipWidth, mipHeight);
            if (levelLength is null)
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEI_SURFACE_PARTITION_UNKNOWN",
                    $"Surface partitioning is not established for {format.DisplayName}.",
                    reader.Position - payloadLength,
                    "image"));
                return [new AeiSurface(0, 0, 0, width, height, 0, payloadLength)];
            }

            if ((long)offset + levelLength.Value > payloadLength)
            {
                throw reader.Corrupt(
                    "payloadLength",
                    $"Payload ends inside mip {mip}; need {levelLength.Value} bytes at payload offset {offset}, length is {payloadLength}.");
            }

            surfaces.Add(new AeiSurface(
                0,
                0,
                mip,
                mipWidth,
                mipHeight,
                offset,
                levelLength.Value));
            offset = checked(offset + levelLength.Value);

            if (!format.HasMipmaps || (mipWidth == 1 && mipHeight == 1))
            {
                break;
            }

            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
            mip++;
        }

        if (offset != payloadLength)
        {
            diagnostics.Add(new FormatDiagnostic(
                DiagnosticSeverity.Warning,
                "AEI_PAYLOAD_REMAINDER",
                $"Known surfaces consume {offset} of {payloadLength} payload bytes; {payloadLength - offset} bytes remain preserved.",
                reader.Position - payloadLength + offset,
                "image"));
        }

        return surfaces;
    }

    private static int? GetLevelLength(AeiCompressionFormat format, int width, int height)
    {
        return format switch
        {
            AeiCompressionFormat.Dxt1 or AeiCompressionFormat.Etc1 =>
                CheckedBlockLength(width, height, 8),
            AeiCompressionFormat.Dxt3 or AeiCompressionFormat.Dxt5 or AeiCompressionFormat.Etc2 =>
                CheckedBlockLength(width, height, 16),
            AeiCompressionFormat.Atc =>
                CheckedBlockLength(width, height, 16),
            AeiCompressionFormat.Pvrtc4Rgba =>
                checked(Math.Max(width, 8) * Math.Max(height, 8) / 2),
            AeiCompressionFormat.Pvrtc2Rgba =>
                checked(Math.Max(width, 16) * Math.Max(height, 8) / 4),
            _ => null,
        };
    }

    private static int CheckedBlockLength(int width, int height, int blockBytes)
    {
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);
        return checked(blocksWide * blocksHigh * blockBytes);
    }

    private static void ValidateOptions(AeiParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumRegionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumSymbolCount);
    }
}
