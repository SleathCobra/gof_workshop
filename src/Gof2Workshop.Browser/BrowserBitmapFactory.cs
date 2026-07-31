using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Gof2Workshop.Core;

namespace Gof2Workshop.Browser;

internal static class BrowserBitmapFactory
{
    public static WriteableBitmap Create(RgbaImage image)
    {
        WriteableBitmap bitmap = new(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        byte[] source = image.ReadOnlyPixelBytes.ToArray();
        using ILockedFramebuffer framebuffer = bitmap.Lock();
        int stride = checked(image.Width * 4);
        for (int row = 0; row < image.Height; row++)
        {
            Marshal.Copy(
                source,
                row * stride,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                stride);
        }

        return bitmap;
    }
}
