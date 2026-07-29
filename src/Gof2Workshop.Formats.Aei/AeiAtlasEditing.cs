using Gof2Workshop.Core;

namespace Gof2Workshop.Formats.Aei;

public sealed record AeiRegionOverlap(int FirstRegionIndex, int SecondRegionIndex);

public sealed record AeiPixelDifference(
    long ChangedPixels,
    long ChangedAlphaPixels,
    long AbsoluteChannelError,
    byte MaximumChannelError);

public static class AeiAtlasEditing
{
    public static RgbaImage ReplaceRegion(
        RgbaImage original,
        AeiRegion region,
        RgbaImage replacement)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Width != region.Width || replacement.Height != region.Height)
        {
            throw new InvalidDataException(
                $"Replacement is {replacement.Width}x{replacement.Height}; " +
                $"region {region.Index} requires {region.Width}x{region.Height}.");
        }

        if (region.X + region.Width > original.Width || region.Y + region.Height > original.Height)
        {
            throw new InvalidDataException($"Region {region.Index} lies outside the atlas.");
        }

        RgbaImage working = new(original.Width, original.Height, original.ReadOnlyPixelBytes);
        for (int y = 0; y < region.Height; y++)
        {
            replacement.ReadOnlyPixelBytes.Slice(y * replacement.Width * 4, replacement.Width * 4)
                .CopyTo(working.PixelBytes.Slice(
                    ((region.Y + y) * working.Width + region.X) * 4,
                    replacement.Width * 4));
        }

        return working;
    }

    public static IReadOnlyList<AeiRegionOverlap> FindOverlaps(IReadOnlyList<AeiRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        List<AeiRegionOverlap> overlaps = [];
        for (int first = 0; first < regions.Count; first++)
        {
            for (int second = first + 1; second < regions.Count; second++)
            {
                if (Intersects(regions[first], regions[second]))
                {
                    overlaps.Add(new AeiRegionOverlap(regions[first].Index, regions[second].Index));
                }
            }
        }

        return overlaps;
    }

    public static AeiPixelDifference Compare(RgbaImage original, RgbaImage working)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(working);
        if (original.Width != working.Width || original.Height != working.Height)
        {
            throw new InvalidDataException("Images must have identical dimensions for comparison.");
        }

        long changedPixels = 0;
        long changedAlpha = 0;
        long absoluteError = 0;
        byte maximumError = 0;
        ReadOnlySpan<byte> before = original.ReadOnlyPixelBytes;
        ReadOnlySpan<byte> after = working.ReadOnlyPixelBytes;
        for (int pixel = 0; pixel < original.Width * original.Height; pixel++)
        {
            bool changed = false;
            int offset = pixel * 4;
            for (int channel = 0; channel < 4; channel++)
            {
                int difference = Math.Abs(before[offset + channel] - after[offset + channel]);
                changed |= difference != 0;
                absoluteError += difference;
                maximumError = Math.Max(maximumError, (byte)difference);
            }

            changedPixels += changed ? 1 : 0;
            changedAlpha += before[offset + 3] != after[offset + 3] ? 1 : 0;
        }

        return new AeiPixelDifference(changedPixels, changedAlpha, absoluteError, maximumError);
    }

    private static bool Intersects(AeiRegion first, AeiRegion second)
    {
        return first.X < second.X + second.Width
            && first.X + first.Width > second.X
            && first.Y < second.Y + second.Height
            && first.Y + first.Height > second.Y;
    }
}
