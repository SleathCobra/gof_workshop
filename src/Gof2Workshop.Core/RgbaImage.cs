namespace Gof2Workshop.Core;

public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    public static Rgba32 Transparent { get; } = new(0, 0, 0, 0);

    public static Rgba32 Black { get; } = new(0, 0, 0, 255);

    public static Rgba32 White { get; } = new(255, 255, 255, 255);
}

public sealed class RgbaImage
{
    public const int MaximumDimension = 32_768;
    public const long MaximumPixelCount = 268_435_456;

    private readonly byte[] pixels;

    public RgbaImage(int width, int height)
        : this(width, height, new byte[CheckedByteLength(width, height)], takeOwnership: true)
    {
    }

    public RgbaImage(int width, int height, ReadOnlySpan<byte> rgbaBytes)
        : this(width, height, rgbaBytes.ToArray(), takeOwnership: true)
    {
    }

    private RgbaImage(int width, int height, byte[] rgbaBytes, bool takeOwnership)
    {
        int expectedLength = CheckedByteLength(width, height);
        if (rgbaBytes.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Expected {expectedLength} RGBA bytes for a {width}x{height} image, got {rgbaBytes.Length}.",
                nameof(rgbaBytes));
        }

        Width = width;
        Height = height;
        pixels = takeOwnership ? rgbaBytes : rgbaBytes.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public Span<byte> PixelBytes => pixels;

    public ReadOnlySpan<byte> ReadOnlyPixelBytes => pixels;

    public Rgba32 GetPixel(int x, int y)
    {
        int offset = GetPixelOffset(x, y);
        return new Rgba32(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    public void SetPixel(int x, int y, Rgba32 color)
    {
        int offset = GetPixelOffset(x, y);
        pixels[offset] = color.R;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.B;
        pixels[offset + 3] = color.A;
    }

    public void Clear(Rgba32 color)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.R;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.B;
            pixels[offset + 3] = color.A;
        }
    }

    public RgbaImage Crop(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if ((long)x + width > Width || (long)y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Crop rectangle exceeds image bounds.");
        }

        RgbaImage result = new(width, height);
        int sourceStride = checked(Width * 4);
        int destinationStride = checked(width * 4);

        for (int row = 0; row < height; row++)
        {
            pixels.AsSpan(((y + row) * sourceStride) + (x * 4), destinationStride)
                .CopyTo(result.pixels.AsSpan(row * destinationStride, destinationStride));
        }

        return result;
    }

    public static RgbaImage TakeOwnership(int width, int height, byte[] rgbaBytes)
    {
        ArgumentNullException.ThrowIfNull(rgbaBytes);
        return new RgbaImage(width, height, rgbaBytes, takeOwnership: true);
    }

    private static int CheckedByteLength(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width > MaximumDimension || height > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Image dimensions may not exceed {MaximumDimension}.");
        }

        long pixelCount = (long)width * height;
        if (pixelCount > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Image contains {pixelCount} pixels, exceeding the limit of {MaximumPixelCount}.");
        }

        return checked((int)(pixelCount * 4));
    }

    private int GetPixelOffset(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return checked(((y * Width) + x) * 4);
    }
}
