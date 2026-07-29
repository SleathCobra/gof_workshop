using System.Text;

namespace Gof2Workshop.Formats.Aei;

/// <summary>
/// Writes an AEI snapshot without changing its container metadata. Pixel replacement is supplied
/// as an explicit payload so callers cannot accidentally mutate the immutable parser result.
/// </summary>
public sealed class AeiWriter
{
    private static readonly byte[] Magic = "AEimage\0"u8.ToArray();

    public void Write(
        AeiFile file,
        string path,
        ReadOnlyMemory<byte>? replacementPayload = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = new(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        Write(file, stream, replacementPayload);
    }

    public void Write(
        AeiFile file,
        Stream output,
        ReadOnlyMemory<byte>? replacementPayload = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        }

        ReadOnlyMemory<byte> payload = replacementPayload ?? file.Payload;
        ValidatePayload(file, payload.Length);
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(file.Format.RawId);
        writer.Write(file.Width);
        writer.Write(file.Height);
        writer.Write(checked((ushort)file.Regions.Count));
        foreach (AeiRegion region in file.Regions)
        {
            writer.Write(region.X);
            writer.Write(region.Y);
            writer.Write(region.Width);
            writer.Write(region.Height);
        }

        if (file.Format.IsCompressed)
        {
            writer.Write(checked((uint)payload.Length));
        }

        writer.Write(payload.Span);
        writer.Write(checked((ushort)file.SymbolMaps.Count));
        foreach (AeiSymbolMap map in file.SymbolMaps)
        {
            writer.Write(checked((ushort)map.Symbols.Count));
            foreach (AeiSymbol symbol in map.Symbols)
            {
                writer.Write((ushort)symbol.Character);
            }

            foreach (AeiSymbol symbol in map.Symbols)
            {
                writer.Write(symbol.X);
                writer.Write(symbol.Y);
                writer.Write(symbol.Width);
                writer.Write(symbol.Height);
            }
        }

        if (file.CompressionQuality is byte quality)
        {
            writer.Write(quality);
        }

        writer.Write(file.UnknownTrailingData);
    }

    private static void ValidatePayload(AeiFile file, int length)
    {
        if (length != file.Payload.Length)
        {
            throw new InvalidDataException(
                $"Replacement payload length {length} differs from the preserved " +
                $"{file.Payload.Length}-byte surface layout. Re-encoding is required.");
        }

        if (!file.Format.IsCompressed)
        {
            int required = checked(file.Width * file.Height * 4);
            if (length != required)
            {
                throw new InvalidDataException(
                    $"Raw AEI payload must contain {required} RGBA bytes, got {length}.");
            }
        }
    }
}
