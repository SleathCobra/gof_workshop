using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;

namespace Gof2Workshop.Export;

public sealed record AeiExportResult(
    bool Decoded,
    string MetadataPath,
    string? AtlasPath,
    string? OverlayPath,
    IReadOnlyList<string> RegionPaths,
    string DecodeStatus);

public sealed class AeiExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AeiTextureDecoder decoder;

    public AeiExportService(AeiTextureDecoder? decoder = null)
    {
        this.decoder = decoder ?? new AeiTextureDecoder();
    }

    public AeiExportResult Export(
        AeiFile file,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        bool canDecode = decoder.CanDecode(file.Format.Format);
        string metadataPath = Path.Combine(outputDirectory, "metadata.json");
        WriteMetadata(file, metadataPath, canDecode);
        if (!canDecode)
        {
            return new AeiExportResult(
                false,
                metadataPath,
                null,
                null,
                [],
                $"Recognized {file.Format.DisplayName}, but no decoder is implemented.");
        }

        RgbaImage atlas = decoder.DecodeAtlas(file, cancellationToken);
        string atlasPath = Path.Combine(outputDirectory, "atlas.png");
        PngWriter.Write(atlas, atlasPath, cancellationToken);

        RgbaImage overlay = new(atlas.Width, atlas.Height, atlas.ReadOnlyPixelBytes);
        DrawRegionOverlay(file, overlay);
        string overlayPath = Path.Combine(outputDirectory, "atlas-regions.png");
        PngWriter.Write(overlay, overlayPath, cancellationToken);

        string regionsDirectory = Path.Combine(outputDirectory, "regions");
        Directory.CreateDirectory(regionsDirectory);
        List<string> regionPaths = [];
        foreach (AeiRegion region in file.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.Width == 0
                || region.Height == 0
                || (long)region.X + region.Width > atlas.Width
                || (long)region.Y + region.Height > atlas.Height)
            {
                continue;
            }

            RgbaImage regionImage = atlas.Crop(
                region.X,
                region.Y,
                region.Width,
                region.Height);
            string regionPath = Path.Combine(regionsDirectory, $"region_{region.Index:D4}.png");
            PngWriter.Write(regionImage, regionPath, cancellationToken);
            regionPaths.Add(regionPath);
        }

        return new AeiExportResult(
            true,
            metadataPath,
            atlasPath,
            overlayPath,
            regionPaths,
            "Decoded successfully.");
    }

    private static void WriteMetadata(AeiFile file, string path, bool canDecode)
    {
        var metadata = new
        {
            source = string.IsNullOrWhiteSpace(file.SourcePath)
                ? null
                : Path.GetFileName(file.SourcePath),
            profile = file.ProfileId,
            signature = "AEimage\\0",
            rawFormatId = $"0x{file.Format.RawId:X2}",
            format = file.Format.DisplayName,
            file.Format.HasMipmaps,
            file.Format.IsCompressed,
            width = file.Width,
            height = file.Height,
            surfaceCount = file.Surfaces.Count,
            arrayElementCount = file.ArrayElementCount,
            cubeFaceCount = file.FaceCount,
            mipLevelCount = file.MipLevelCount,
            regionCount = file.Regions.Count,
            symbolMapCount = file.SymbolMaps.Count,
            payloadLength = file.Payload.Length,
            file.CompressionQuality,
            unknownTrailingByteCount = file.UnknownTrailingData.Length,
            decodeStatus = canDecode
                ? "Supported"
                : $"Recognized but not decodable: {file.Format.DisplayName}",
            surfaces = file.Surfaces,
            regions = file.Regions.Select(region => new
            {
                region.Index,
                region.X,
                region.Y,
                region.Width,
                region.Height,
            }),
            symbolMaps = file.SymbolMaps.Select(map => new
            {
                map.Index,
                symbols = map.Symbols.Select(symbol => new
                {
                    character = symbol.Character.ToString(),
                    codePoint = $"U+{(int)symbol.Character:X4}",
                    symbol.X,
                    symbol.Y,
                    symbol.Width,
                    symbol.Height,
                }),
            }),
            diagnostics = file.Diagnostics,
            trace = file.Trace?.Entries,
            traceTruncated = file.Trace?.IsTruncated,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static void DrawRegionOverlay(AeiFile file, RgbaImage overlay)
    {
        foreach (AeiRegion region in file.Regions)
        {
            if (region.Width == 0 || region.Height == 0)
            {
                continue;
            }

            Rgba32 color = region.Index % 2 == 0
                ? new Rgba32(255, 48, 48, 220)
                : new Rgba32(48, 220, 255, 220);
            RasterDrawing.DrawRectangle(
                overlay,
                region.X,
                region.Y,
                region.Width,
                region.Height,
                color,
                thickness: 2);

            string label = region.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int labelWidth = (label.Length * 6) + 3;
            RasterDrawing.FillRectangle(
                overlay,
                region.X,
                region.Y,
                labelWidth,
                10,
                new Rgba32(0, 0, 0, 180));
            RasterDrawing.DrawText(
                overlay,
                region.X + 2,
                region.Y + 1,
                label,
                Rgba32.White);
        }
    }
}
