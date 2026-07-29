using Gof2Workshop.Core;

namespace Gof2Workshop.Export;

public static class RasterDrawing
{
    private static readonly Dictionary<char, byte[]> Glyphs =
        new Dictionary<char, byte[]>
        {
            ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
            ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
            ['2'] = [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
            ['3'] = [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
            ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
            ['5'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110],
            ['6'] = [0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
            ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
            ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
            ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110],
            ['-'] = [0, 0, 0, 0b11111, 0, 0, 0],
        };

    public static void DrawRectangle(
        RgbaImage image,
        int x,
        int y,
        int width,
        int height,
        Rgba32 color,
        int thickness = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (width <= 0 || height <= 0 || thickness <= 0)
        {
            return;
        }

        for (int inset = 0; inset < thickness; inset++)
        {
            DrawLine(image, x + inset, y + inset, x + width - 1 - inset, y + inset, color);
            DrawLine(
                image,
                x + inset,
                y + height - 1 - inset,
                x + width - 1 - inset,
                y + height - 1 - inset,
                color);
            DrawLine(image, x + inset, y + inset, x + inset, y + height - 1 - inset, color);
            DrawLine(
                image,
                x + width - 1 - inset,
                y + inset,
                x + width - 1 - inset,
                y + height - 1 - inset,
                color);
        }
    }

    public static void FillRectangle(
        RgbaImage image,
        int x,
        int y,
        int width,
        int height,
        Rgba32 color)
    {
        ArgumentNullException.ThrowIfNull(image);
        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(image.Width, x + width);
        int bottom = Math.Min(image.Height, y + height);
        for (int row = top; row < bottom; row++)
        {
            for (int column = left; column < right; column++)
            {
                BlendPixel(image, column, row, color);
            }
        }
    }

    public static void DrawLine(
        RgbaImage image,
        int x0,
        int y0,
        int x1,
        int y1,
        Rgba32 color)
    {
        ArgumentNullException.ThrowIfNull(image);
        int deltaX = Math.Abs(x1 - x0);
        int stepX = x0 < x1 ? 1 : -1;
        int deltaY = -Math.Abs(y1 - y0);
        int stepY = y0 < y1 ? 1 : -1;
        int error = deltaX + deltaY;

        while (true)
        {
            if ((uint)x0 < (uint)image.Width && (uint)y0 < (uint)image.Height)
            {
                BlendPixel(image, x0, y0, color);
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int doubledError = 2 * error;
            if (doubledError >= deltaY)
            {
                error += deltaY;
                x0 += stepX;
            }

            if (doubledError <= deltaX)
            {
                error += deltaX;
                y0 += stepY;
            }
        }
    }

    public static void DrawCircle(
        RgbaImage image,
        int centerX,
        int centerY,
        int radius,
        Rgba32 color)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (radius <= 0)
        {
            return;
        }

        int x = radius;
        int y = 0;
        int error = 1 - x;
        while (x >= y)
        {
            PlotCircleOctants(image, centerX, centerY, x, y, color);
            y++;
            if (error < 0)
            {
                error += (2 * y) + 1;
            }
            else
            {
                x--;
                error += (2 * (y - x)) + 1;
            }
        }
    }

    public static void DrawText(
        RgbaImage image,
        int x,
        int y,
        string text,
        Rgba32 color,
        int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        int cursor = x;
        foreach (char character in text)
        {
            if (Glyphs.TryGetValue(character, out byte[]? rows))
            {
                for (int row = 0; row < rows.Length; row++)
                {
                    for (int column = 0; column < 5; column++)
                    {
                        if ((rows[row] & (1 << (4 - column))) != 0)
                        {
                            FillRectangle(
                                image,
                                cursor + (column * scale),
                                y + (row * scale),
                                scale,
                                scale,
                                color);
                        }
                    }
                }
            }

            cursor += 6 * scale;
        }
    }

    public static void BlendPixel(RgbaImage image, int x, int y, Rgba32 source)
    {
        if (source.A == byte.MaxValue)
        {
            image.SetPixel(x, y, source);
            return;
        }

        Rgba32 destination = image.GetPixel(x, y);
        int inverseAlpha = byte.MaxValue - source.A;
        image.SetPixel(
            x,
            y,
            new Rgba32(
                (byte)(((source.R * source.A) + (destination.R * inverseAlpha)) / 255),
                (byte)(((source.G * source.A) + (destination.G * inverseAlpha)) / 255),
                (byte)(((source.B * source.A) + (destination.B * inverseAlpha)) / 255),
                byte.MaxValue));
    }

    private static void PlotCircleOctants(
        RgbaImage image,
        int centerX,
        int centerY,
        int x,
        int y,
        Rgba32 color)
    {
        (int X, int Y)[] points =
        [
            (centerX + x, centerY + y),
            (centerX + y, centerY + x),
            (centerX - y, centerY + x),
            (centerX - x, centerY + y),
            (centerX - x, centerY - y),
            (centerX - y, centerY - x),
            (centerX + y, centerY - x),
            (centerX + x, centerY - y),
        ];

        foreach ((int pointX, int pointY) in points)
        {
            if ((uint)pointX < (uint)image.Width && (uint)pointY < (uint)image.Height)
            {
                BlendPixel(image, pointX, pointY, color);
            }
        }
    }
}
