using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Gof2Workshop.Core;

namespace Gof2Workshop.App.Views;

public static class AvaloniaBitmapFactory
{
    public static WriteableBitmap Create(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        WriteableBitmap bitmap = new(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        byte[] source = image.ReadOnlyPixelBytes.ToArray();
        using ILockedFramebuffer framebuffer = bitmap.Lock();
        int sourceStride = checked(image.Width * 4);
        for (int row = 0; row < image.Height; row++)
        {
            Marshal.Copy(
                source,
                row * sourceStride,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                sourceStride);
        }

        return bitmap;
    }

    public static RgbaImage LoadRgba(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using Bitmap source = new(path);
        using WriteableBitmap converted = new(
            source.PixelSize,
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        using ILockedFramebuffer framebuffer = converted.Lock();
        source.CopyPixels(framebuffer);
        int width = source.PixelSize.Width;
        int height = source.PixelSize.Height;
        RgbaImage image = new(width, height);
        byte[] row = new byte[checked(width * 4)];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(
                IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                row,
                0,
                row.Length);
            row.CopyTo(image.PixelBytes.Slice(y * row.Length, row.Length));
        }

        return image;
    }
}
