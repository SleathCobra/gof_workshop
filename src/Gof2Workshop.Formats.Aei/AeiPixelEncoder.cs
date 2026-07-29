using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aei;

public enum AeiEncodingQuality
{
    Fast,
    Balanced,
    Best,
}

public sealed record AeiEncodingOptions(
    AeiEncodingQuality Quality = AeiEncodingQuality.Balanced,
    bool PreserveMipCount = true);

public sealed record AeiEncodingResult(
    byte[] Payload,
    AeiFile ReparsedFile,
    RgbaImage DecodedAtlas,
    long AbsolutePixelError,
    byte MaximumChannelError);

public interface IAeiPixelEncoder
{
    public bool CanEncode(AeiCompressionFormat format);

    public byte[] EncodeSurface(
        RgbaImage image,
        AeiCompressionFormat format,
        AeiEncodingOptions? options = null,
        CancellationToken cancellationToken = default);

    public byte[] RebuildPayload(
        AeiFile source,
        RgbaImage workingAtlas,
        AeiEncodingOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Encodes Workshop-owned RGBA pixels. Container reconstruction remains the responsibility of
/// <see cref="AeiWriter"/>, keeping codec work independent from preserved AEI metadata.
/// </summary>
public sealed class AeiPixelEncoder : IAeiPixelEncoder
{
    public bool CanEncode(AeiCompressionFormat format)
    {
        return format is AeiCompressionFormat.UncompressedUi
            or AeiCompressionFormat.Uncompressed
            or AeiCompressionFormat.UncompressedCubeMapPc
            or AeiCompressionFormat.UncompressedCubeMap
            or AeiCompressionFormat.Dxt1
            or AeiCompressionFormat.Dxt3
            or AeiCompressionFormat.Dxt5;
    }

    public byte[] EncodeSurface(
        RgbaImage image,
        AeiCompressionFormat format,
        AeiEncodingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new AeiEncodingOptions();

        if (format is AeiCompressionFormat.UncompressedUi
            or AeiCompressionFormat.Uncompressed
            or AeiCompressionFormat.UncompressedCubeMapPc
            or AeiCompressionFormat.UncompressedCubeMap)
        {
            return image.ReadOnlyPixelBytes.ToArray();
        }

        CompressionFormat bcFormat = format switch
        {
            AeiCompressionFormat.Dxt1 => CompressionFormat.Bc1,
            AeiCompressionFormat.Dxt3 => CompressionFormat.Bc2,
            AeiCompressionFormat.Dxt5 => CompressionFormat.Bc3,
            _ => throw new NotSupportedException(
                $"{format} encoding is unavailable. The source codec will not be changed."),
        };

        BcEncoder encoder = new(bcFormat);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = options.Quality switch
        {
            AeiEncodingQuality.Fast => CompressionQuality.Fast,
            AeiEncodingQuality.Best => CompressionQuality.BestQuality,
            _ => CompressionQuality.Balanced,
        };
        encoder.Options.IsParallel = true;
        byte[][] levels = encoder.EncodeToRawBytes(
            image.ReadOnlyPixelBytes,
            image.Width,
            image.Height,
            PixelFormat.Rgba32);
        cancellationToken.ThrowIfCancellationRequested();
        if (levels.Length != 1)
        {
            throw new InvalidDataException(
                $"BC encoder returned {levels.Length} levels when mip generation was disabled.");
        }

        return levels[0];
    }

    public byte[] RebuildPayload(
        AeiFile source,
        RgbaImage workingAtlas,
        AeiEncodingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workingAtlas);
        options ??= new AeiEncodingOptions();
        if (!CanEncode(source.Format.Format))
        {
            throw new NotSupportedException(
                $"{source.Format.DisplayName} is recognized but cannot be encoded.");
        }

        if (workingAtlas.Width != source.Width || workingAtlas.Height != source.Height)
        {
            throw new InvalidDataException(
                $"Working atlas is {workingAtlas.Width}x{workingAtlas.Height}; " +
                $"the preserved container requires {source.Width}x{source.Height}.");
        }

        byte[] payload = source.Payload.ToArray();
        AeiSurface[] editableSurfaces = source.Surfaces
            .Where(surface => surface.ArrayElement == 0 && surface.Face == 0)
            .OrderBy(surface => surface.MipLevel)
            .ToArray();
        if (editableSurfaces.Length == 0)
        {
            throw new InvalidDataException("The AEI has no editable primary surface.");
        }

        RgbaImage levelImage = workingAtlas;
        for (int index = 0; index < editableSurfaces.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AeiSurface surface = editableSurfaces[index];
            if (levelImage.Width != surface.Width || levelImage.Height != surface.Height)
            {
                levelImage = ResizeBox(workingAtlas, surface.Width, surface.Height, cancellationToken);
            }

            byte[] encoded = EncodeSurface(levelImage, source.Format.Format, options, cancellationToken);
            if (encoded.Length != surface.PayloadLength)
            {
                throw new InvalidDataException(
                    $"Encoded mip {surface.MipLevel} has {encoded.Length} bytes; " +
                    $"the preserved surface requires {surface.PayloadLength}.");
            }

            encoded.CopyTo(payload, surface.PayloadOffset);
        }

        return payload;
    }

    internal static RgbaImage ResizeBox(
        RgbaImage source,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Mip dimensions must be positive.");
        }

        RgbaImage result = new(width, height);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceY0 = y * source.Height / height;
            int sourceY1 = Math.Max(sourceY0 + 1, (y + 1) * source.Height / height);
            for (int x = 0; x < width; x++)
            {
                int sourceX0 = x * source.Width / width;
                int sourceX1 = Math.Max(sourceX0 + 1, (x + 1) * source.Width / width);
                long red = 0;
                long green = 0;
                long blue = 0;
                long alpha = 0;
                int count = 0;
                for (int sourceY = sourceY0; sourceY < sourceY1; sourceY++)
                {
                    for (int sourceX = sourceX0; sourceX < sourceX1; sourceX++)
                    {
                        Rgba32 pixel = source.GetPixel(sourceX, sourceY);
                        red += pixel.R;
                        green += pixel.G;
                        blue += pixel.B;
                        alpha += pixel.A;
                        count++;
                    }
                }

                result.SetPixel(
                    x,
                    y,
                    new Rgba32(
                        (byte)(red / count),
                        (byte)(green / count),
                        (byte)(blue / count),
                        (byte)(alpha / count)));
            }
        }

        return result;
    }
}

public sealed class AeiReconstructionService
{
    private readonly IAeiPixelEncoder encoder;

    public AeiReconstructionService(IAeiPixelEncoder? encoder = null)
    {
        this.encoder = encoder ?? new AeiPixelEncoder();
    }

    public AeiEncodingResult ReconstructAndValidate(
        AeiFile original,
        RgbaImage workingAtlas,
        AeiEncodingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(workingAtlas);
        byte[] payload = encoder.RebuildPayload(original, workingAtlas, options, cancellationToken);
        using MemoryStream output = new();
        new AeiWriter().Write(original, output, payload);
        output.Position = 0;
        AeiFile reparsed = new AeiParser().Parse(
            output,
            original.SourcePath is null ? "reconstructed.aei" : $"{original.SourcePath} (working)",
            new AeiParserOptions(ProfileCatalog.Resolve(original.ProfileId)),
            cancellationToken);
        RgbaImage decoded = new AeiTextureDecoder().DecodeAtlas(reparsed, cancellationToken);
        if (decoded.Width != workingAtlas.Width || decoded.Height != workingAtlas.Height)
        {
            throw new InvalidDataException("Reconstructed AEI dimensions changed during validation.");
        }

        long absoluteError = 0;
        byte maximumError = 0;
        ReadOnlySpan<byte> expected = workingAtlas.ReadOnlyPixelBytes;
        ReadOnlySpan<byte> actual = decoded.ReadOnlyPixelBytes;
        for (int index = 0; index < expected.Length; index++)
        {
            int difference = Math.Abs(expected[index] - actual[index]);
            absoluteError += difference;
            maximumError = Math.Max(maximumError, (byte)difference);
        }

        return new AeiEncodingResult(payload, reparsed, decoded, absoluteError, maximumError);
    }
}
