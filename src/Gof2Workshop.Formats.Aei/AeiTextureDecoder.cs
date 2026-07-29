using System.Buffers.Binary;
using AssetRipper.TextureDecoder.Atc;
using AssetRipper.TextureDecoder.Etc;
using AssetRipper.TextureDecoder.Pvrtc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aei;

public sealed class AeiTextureDecoder
{
    public bool CanDecode(AeiCompressionFormat format)
    {
        return format is AeiCompressionFormat.UncompressedUi
            or AeiCompressionFormat.Uncompressed
            or AeiCompressionFormat.UncompressedCubeMapPc
            or AeiCompressionFormat.UncompressedCubeMap
            or AeiCompressionFormat.Dxt1
            or AeiCompressionFormat.Dxt3
            or AeiCompressionFormat.Dxt5
            or AeiCompressionFormat.Pvrtc2Rgba
            or AeiCompressionFormat.Pvrtc4Rgba
            or AeiCompressionFormat.Atc
            or AeiCompressionFormat.Etc1
            or AeiCompressionFormat.Etc2;
    }

    public RgbaImage DecodeAtlas(AeiFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!CanDecode(file.Format.Format))
        {
            throw new FormatParseException(
                FormatFailureKind.Unsupported,
                file.SourcePath,
                file.PayloadFileOffset,
                "format",
                $"Compression {file.Format.DisplayName} is recognized but no decoder is available.");
        }

        if (!file.Format.IsCompressed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RgbaImage(file.Width, file.Height, file.Payload);
        }

        return DecodeSurface(file, arrayElement: 0, face: 0, mipLevel: 0, cancellationToken);
    }

    public RgbaImage DecodeSurface(
        AeiFile file,
        int arrayElement,
        int face,
        int mipLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        AeiSurface? surface = file.Surfaces.FirstOrDefault(
            candidate => candidate.ArrayElement == arrayElement
                && candidate.Face == face
                && candidate.MipLevel == mipLevel);
        if (surface is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mipLevel),
                $"Surface array={arrayElement}, face={face}, mip={mipLevel} does not exist.");
        }

        if (!file.Format.IsCompressed)
        {
            if (surface.PayloadOffset == 0
                && surface.PayloadLength == file.Payload.Length
                && surface.Width == file.Width
                && surface.Height == file.Height)
            {
                return new RgbaImage(surface.Width, surface.Height, file.Payload);
            }

            RgbaImage rawSurface = new(surface.Width, surface.Height);
            file.Payload.AsSpan(surface.PayloadOffset, surface.PayloadLength)
                .CopyTo(rawSurface.PixelBytes);
            return rawSurface;
        }

        ReadOnlySpan<byte> source = file.Payload.AsSpan(surface.PayloadOffset, surface.PayloadLength);
        return file.Format.Format switch
        {
            AeiCompressionFormat.Dxt1 => DecodeBc(source, surface.Width, surface.Height, BcMode.Bc1, cancellationToken),
            AeiCompressionFormat.Dxt3 => DecodeBc(source, surface.Width, surface.Height, BcMode.Bc2, cancellationToken),
            AeiCompressionFormat.Dxt5 => DecodeBc(source, surface.Width, surface.Height, BcMode.Bc3, cancellationToken),
            AeiCompressionFormat.Pvrtc2Rgba => DecodeMobile(
                source,
                surface.Width,
                surface.Height,
                static (input, width, height, output) =>
                    PvrtcDecoder.DecompressPVRTC<ColorRGBA<byte>, byte>(
                        input,
                        width,
                        height,
                        do2bitMode: true,
                        output),
                cancellationToken),
            AeiCompressionFormat.Pvrtc4Rgba => DecodeMobile(
                source,
                surface.Width,
                surface.Height,
                static (input, width, height, output) =>
                    PvrtcDecoder.DecompressPVRTC<ColorRGBA<byte>, byte>(
                        input,
                        width,
                        height,
                        do2bitMode: false,
                        output),
                cancellationToken),
            AeiCompressionFormat.Atc => DecodeMobile(
                source,
                surface.Width,
                surface.Height,
                static (input, width, height, output) =>
                    AtcDecoder.DecompressAtcRgba8<ColorRGBA<byte>, byte>(
                        input,
                        width,
                        height,
                        output),
                cancellationToken),
            AeiCompressionFormat.Etc1 => DecodeMobile(
                source,
                surface.Width,
                surface.Height,
                static (input, width, height, output) =>
                    EtcDecoder.DecompressETC<ColorRGBA<byte>, byte>(
                        input,
                        width,
                        height,
                        output),
                cancellationToken),
            AeiCompressionFormat.Etc2 => DecodeMobile(
                source,
                surface.Width,
                surface.Height,
                static (input, width, height, output) =>
                    EtcDecoder.DecompressETC2A8<ColorRGBA<byte>, byte>(
                        input,
                        width,
                        height,
                        output),
                cancellationToken),
            _ => throw new FormatParseException(
                FormatFailureKind.Unsupported,
                file.SourcePath,
                file.PayloadFileOffset + surface.PayloadOffset,
                "format",
                $"Compression {file.Format.DisplayName} is recognized but no decoder is available."),
        };
    }

    private static RgbaImage DecodeMobile(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        MobileDecoder decoder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RgbaImage image = new(width, height);
        try
        {
            int bytesRead = decoder(source, width, height, image.PixelBytes);
            if (bytesRead > source.Length)
            {
                throw new InvalidDataException(
                    $"Texture decoder consumed {bytesRead} bytes from a {source.Length}-byte surface.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Compressed surface payload is invalid for a {width}x{height} texture.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    private static RgbaImage DecodeBc(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        BcMode mode,
        CancellationToken cancellationToken)
    {
        int blockBytes = mode == BcMode.Bc1 ? 8 : 16;
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);
        int expectedLength = checked(blocksWide * blocksHigh * blockBytes);
        if (source.Length < expectedLength)
        {
            throw new InvalidDataException(
                $"{mode} surface requires {expectedLength} bytes, got {source.Length}.");
        }

        RgbaImage image = new(width, height);
        Span<Rgba32> colors = stackalloc Rgba32[4];
        Span<byte> alphas = stackalloc byte[8];
        int sourceOffset = 0;

        for (int blockY = 0; blockY < blocksHigh; blockY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int blockX = 0; blockX < blocksWide; blockX++)
            {
                ReadOnlySpan<byte> block = source.Slice(sourceOffset, blockBytes);
                sourceOffset += blockBytes;

                ulong alphaIndices = 0;
                if (mode == BcMode.Bc2)
                {
                    BuildBc2Alpha(block[..8], alphas);
                    block = block[8..];
                }
                else if (mode == BcMode.Bc3)
                {
                    BuildBc3Alpha(block[..8], alphas, out alphaIndices);
                    block = block[8..];
                }

                ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
                ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
                uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                BuildColorPalette(color0, color1, colors, forceOpaquePalette: mode != BcMode.Bc1);

                for (int pixel = 0; pixel < 16; pixel++)
                {
                    int x = (blockX * 4) + (pixel & 3);
                    int y = (blockY * 4) + (pixel >> 2);
                    if (x >= width || y >= height)
                    {
                        continue;
                    }

                    int colorIndex = (int)((colorIndices >> (pixel * 2)) & 0x3);
                    Rgba32 color = colors[colorIndex];
                    byte alpha = mode switch
                    {
                        BcMode.Bc1 => color.A,
                        BcMode.Bc2 => alphas[pixel],
                        BcMode.Bc3 => alphas[(int)((alphaIndices >> (pixel * 3)) & 0x7)],
                        _ => byte.MaxValue,
                    };
                    image.SetPixel(x, y, color with { A = alpha });
                }
            }
        }

        return image;
    }

    private static void BuildColorPalette(
        ushort endpoint0,
        ushort endpoint1,
        Span<Rgba32> palette,
        bool forceOpaquePalette)
    {
        palette[0] = DecodeRgb565(endpoint0);
        palette[1] = DecodeRgb565(endpoint1);

        if (endpoint0 > endpoint1 || forceOpaquePalette)
        {
            palette[2] = Interpolate(palette[0], palette[1], 2, 1, 3);
            palette[3] = Interpolate(palette[0], palette[1], 1, 2, 3);
        }
        else
        {
            palette[2] = Interpolate(palette[0], palette[1], 1, 1, 2);
            palette[3] = Rgba32.Transparent;
        }
    }

    private static Rgba32 DecodeRgb565(ushort value)
    {
        int red = (value >> 11) & 0x1F;
        int green = (value >> 5) & 0x3F;
        int blue = value & 0x1F;
        return new Rgba32(
            (byte)((red * 255 + 15) / 31),
            (byte)((green * 255 + 31) / 63),
            (byte)((blue * 255 + 15) / 31),
            byte.MaxValue);
    }

    private static Rgba32 Interpolate(
        Rgba32 first,
        Rgba32 second,
        int firstWeight,
        int secondWeight,
        int divisor)
    {
        return new Rgba32(
            (byte)(((first.R * firstWeight) + (second.R * secondWeight)) / divisor),
            (byte)(((first.G * firstWeight) + (second.G * secondWeight)) / divisor),
            (byte)(((first.B * firstWeight) + (second.B * secondWeight)) / divisor),
            byte.MaxValue);
    }

    private static void BuildBc2Alpha(ReadOnlySpan<byte> bytes, Span<byte> alphaByPixel)
    {
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        for (int pixel = 0; pixel < 16; pixel++)
        {
            int alpha4 = (int)((bits >> (pixel * 4)) & 0xF);
            alphaByPixel[pixel] = (byte)((alpha4 << 4) | alpha4);
        }
    }

    private static void BuildBc3Alpha(
        ReadOnlySpan<byte> bytes,
        Span<byte> alphaPalette,
        out ulong alphaIndices)
    {
        byte alpha0 = bytes[0];
        byte alpha1 = bytes[1];
        alphaPalette[0] = alpha0;
        alphaPalette[1] = alpha1;

        if (alpha0 > alpha1)
        {
            for (int index = 1; index <= 6; index++)
            {
                alphaPalette[index + 1] = (byte)(
                    (((7 - index) * alpha0) + (index * alpha1)) / 7);
            }
        }
        else
        {
            for (int index = 1; index <= 4; index++)
            {
                alphaPalette[index + 1] = (byte)(
                    (((5 - index) * alpha0) + (index * alpha1)) / 5);
            }

            alphaPalette[6] = 0;
            alphaPalette[7] = byte.MaxValue;
        }

        alphaIndices = 0;
        for (int index = 0; index < 6; index++)
        {
            alphaIndices |= (ulong)bytes[index + 2] << (index * 8);
        }
    }

    private enum BcMode
    {
        Bc1,
        Bc2,
        Bc3,
    }

    private delegate int MobileDecoder(
        ReadOnlySpan<byte> input,
        int width,
        int height,
        Span<byte> output);
}
