using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Gof2Workshop.Core;

namespace Gof2Workshop.Binary;

public enum BinaryEndianness
{
    Little,
    Big,
}

public sealed class BoundedBinaryReader : IDisposable
{
    private delegate T SpanFactory<T>(ReadOnlySpan<float> values);

    public const int DefaultMaximumAllocation = 512 * 1024 * 1024;

    private readonly Stream stream;
    private readonly bool leaveOpen;

    public BoundedBinaryReader(
        Stream stream,
        string? sourcePath = null,
        BinaryEndianness endianness = BinaryEndianness.Little,
        ParseTrace? trace = null,
        bool leaveOpen = false,
        int maximumAllocation = DefaultMaximumAllocation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The binary stream must be readable and seekable.", nameof(stream));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAllocation);

        this.stream = stream;
        this.leaveOpen = leaveOpen;
        SourcePath = sourcePath;
        Endianness = endianness;
        Trace = trace;
        MaximumAllocation = maximumAllocation;
    }

    public string? SourcePath { get; }

    public BinaryEndianness Endianness { get; }

    public ParseTrace? Trace { get; }

    public int MaximumAllocation { get; }

    public long Position => stream.Position;

    public long Length => stream.Length;

    public long Remaining => Length - Position;

    public byte ReadByte(string field, string section)
    {
        long offset = Position;
        EnsureAvailable(1, field);
        int value = stream.ReadByte();
        if (value < 0)
        {
            throw Corrupt(field, "Unexpected end of stream.", offset);
        }

        Trace?.Record(section, field, offset, 1, value);
        return (byte)value;
    }

    public sbyte ReadSByte(string field, string section)
    {
        byte value = ReadByte(field, section);
        return unchecked((sbyte)value);
    }

    public ushort ReadUInt16(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[2];
        long offset = ReadExact(bytes, field);
        ushort value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public short ReadInt16(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[2];
        long offset = ReadExact(bytes, field);
        short value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadInt16BigEndian(bytes);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public uint ReadUInt32(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[4];
        long offset = ReadExact(bytes, field);
        uint value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public int ReadInt32(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[4];
        long offset = ReadExact(bytes, field);
        int value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadInt32BigEndian(bytes);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public ulong ReadUInt64(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[8];
        long offset = ReadExact(bytes, field);
        ulong value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt64BigEndian(bytes);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public long ReadInt64(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[8];
        long offset = ReadExact(bytes, field);
        long value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadInt64BigEndian(bytes);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public float ReadSingle(string field, string section)
    {
        int bits = ReadInt32Bits(field, section, traceValue: false);
        float value = BitConverter.Int32BitsToSingle(bits);
        Trace?.Record(section, field, Position - 4, 4, value);
        return value;
    }

    public double ReadDouble(string field, string section)
    {
        Span<byte> bytes = stackalloc byte[8];
        long offset = ReadExact(bytes, field);
        long bits = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadInt64BigEndian(bytes);
        double value = BitConverter.Int64BitsToDouble(bits);
        Trace?.Record(section, field, offset, bytes.Length, value);
        return value;
    }

    public byte[] ReadBytes(int count, string field, string section)
    {
        ValidateAllocation(count, field);
        byte[] bytes = new byte[count];
        long offset = ReadExact(bytes, field);
        Trace?.Record(section, field, offset, bytes.Length, $"byte[{bytes.Length}]");
        return bytes;
    }

    public ushort[] ReadUInt16Array(
        int count,
        string field,
        string section,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int byteCount = checked(count * sizeof(ushort));
        byte[] bytes = ReadBytesUntraced(byteCount, field);
        ushort[] values = new ushort[count];
        ReadOnlySpan<byte> source = bytes;

        for (int index = 0; index < count; index++)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> item = source.Slice(index * sizeof(ushort), sizeof(ushort));
            values[index] = Endianness == BinaryEndianness.Little
                ? BinaryPrimitives.ReadUInt16LittleEndian(item)
                : BinaryPrimitives.ReadUInt16BigEndian(item);
        }

        Trace?.Record(section, field, Position - byteCount, byteCount, $"u16[{count}]");
        return values;
    }

    public Vector2[] ReadVector2Array(
        int count,
        string field,
        string section,
        CancellationToken cancellationToken = default)
    {
        return ReadFloatVectorArray(
            count,
            2,
            field,
            section,
            static values => new Vector2(values[0], values[1]),
            cancellationToken);
    }

    public Vector3[] ReadVector3Array(
        int count,
        string field,
        string section,
        CancellationToken cancellationToken = default)
    {
        return ReadFloatVectorArray(
            count,
            3,
            field,
            section,
            static values => new Vector3(values[0], values[1], values[2]),
            cancellationToken);
    }

    public Vector4[] ReadVector4Array(
        int count,
        string field,
        string section,
        CancellationToken cancellationToken = default)
    {
        return ReadFloatVectorArray(
            count,
            4,
            field,
            section,
            static values => new Vector4(values[0], values[1], values[2], values[3]),
            cancellationToken);
    }

    public Vector3 ReadVector3(string field, string section)
    {
        long offset = Position;
        Vector3 value = new(
            ReadSingleUntraced($"{field}.x"),
            ReadSingleUntraced($"{field}.y"),
            ReadSingleUntraced($"{field}.z"));
        Trace?.Record(section, field, offset, 12, value);
        return value;
    }

    public Vector4 ReadVector4(string field, string section)
    {
        long offset = Position;
        Vector4 value = new(
            ReadSingleUntraced($"{field}.x"),
            ReadSingleUntraced($"{field}.y"),
            ReadSingleUntraced($"{field}.z"),
            ReadSingleUntraced($"{field}.w"));
        Trace?.Record(section, field, offset, 16, value);
        return value;
    }

    public string ReadNullTerminatedString(
        Encoding encoding,
        int maximumBytes,
        string field,
        string section)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ValidateAllocation(maximumBytes, field);

        long offset = Position;
        List<byte> bytes = new(Math.Min(maximumBytes, 256));
        for (int index = 0; index < maximumBytes; index++)
        {
            byte value = ReadByteUntraced(field);
            if (value == 0)
            {
                string result = encoding.GetString(CollectionsMarshal.AsSpan(bytes));
                Trace?.Record(section, field, offset, Position - offset, result);
                return result;
            }

            bytes.Add(value);
        }

        throw Corrupt(field, $"Null terminator not found within {maximumBytes} bytes.", offset);
    }

    public string ReadLengthPrefixedUtf8(
        int maximumBytes,
        string field,
        string section)
    {
        ushort byteCount = ReadUInt16($"{field}.length", section);
        if (byteCount > maximumBytes)
        {
            throw Corrupt(field, $"String length {byteCount} exceeds limit {maximumBytes}.");
        }

        byte[] bytes = ReadBytes(byteCount, field, section);
        return Encoding.UTF8.GetString(bytes);
    }

    public void SeekAbsolute(long offset, string field)
    {
        if (offset < 0 || offset > Length)
        {
            throw Corrupt(field, $"Offset 0x{offset:X} is outside the stream range 0..0x{Length:X}.");
        }

        stream.Position = offset;
    }

    public int Align(int alignment, string field, string section)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        long remainder = Position % alignment;
        if (remainder == 0)
        {
            return 0;
        }

        int padding = checked((int)(alignment - remainder));
        _ = ReadBytes(padding, field, section);
        return padding;
    }

    public byte[] ReadRange(long offset, int length, string field, string section)
    {
        long originalPosition = Position;
        try
        {
            SeekAbsolute(offset, field);
            return ReadBytes(length, field, section);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    public void EnsureAvailable(long count, string field)
    {
        if (count < 0)
        {
            throw Corrupt(field, "A negative byte count was requested.");
        }

        if (count > Remaining)
        {
            throw Corrupt(
                field,
                $"Need {count} bytes but only {Remaining} remain (stream length 0x{Length:X}).");
        }
    }

    public FormatParseException Corrupt(string field, string reason, long? offset = null)
    {
        return new FormatParseException(
            FormatFailureKind.Corrupt,
            SourcePath,
            offset ?? Position,
            field,
            reason);
    }

    public FormatParseException Unsupported(string field, string reason, long? offset = null)
    {
        return new FormatParseException(
            FormatFailureKind.Unsupported,
            SourcePath,
            offset ?? Position,
            field,
            reason);
    }

    public void Dispose()
    {
        if (!leaveOpen)
        {
            stream.Dispose();
        }
    }

    public static double FixedPointToDouble(int rawValue, int fractionalBits)
    {
        if ((uint)fractionalBits > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(fractionalBits));
        }

        return rawValue / (double)(1L << fractionalBits);
    }

    private T[] ReadFloatVectorArray<T>(
        int count,
        int componentCount,
        string field,
        string section,
        SpanFactory<T> factory,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int valueCount = checked(count * componentCount);
        int byteCount = checked(valueCount * sizeof(float));
        byte[] bytes = ReadBytesUntraced(byteCount, field);
        T[] result = new T[count];
        Span<float> components = stackalloc float[4];

        for (int index = 0; index < count; index++)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (int component = 0; component < componentCount; component++)
            {
                int sourceOffset = ((index * componentCount) + component) * sizeof(float);
                ReadOnlySpan<byte> item = bytes.AsSpan(sourceOffset, sizeof(float));
                int bits = Endianness == BinaryEndianness.Little
                    ? BinaryPrimitives.ReadInt32LittleEndian(item)
                    : BinaryPrimitives.ReadInt32BigEndian(item);
                components[component] = BitConverter.Int32BitsToSingle(bits);
            }

            result[index] = factory(components[..componentCount]);
        }

        Trace?.Record(section, field, Position - byteCount, byteCount, $"f32x{componentCount}[{count}]");
        return result;
    }

    private int ReadInt32Bits(string field, string section, bool traceValue)
    {
        Span<byte> bytes = stackalloc byte[4];
        long offset = ReadExact(bytes, field);
        int value = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (traceValue)
        {
            Trace?.Record(section, field, offset, bytes.Length, value);
        }

        return value;
    }

    private float ReadSingleUntraced(string field)
    {
        Span<byte> bytes = stackalloc byte[4];
        _ = ReadExact(bytes, field);
        int bits = Endianness == BinaryEndianness.Little
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadInt32BigEndian(bytes);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private byte ReadByteUntraced(string field)
    {
        EnsureAvailable(1, field);
        int value = stream.ReadByte();
        if (value < 0)
        {
            throw Corrupt(field, "Unexpected end of stream.");
        }

        return (byte)value;
    }

    private byte[] ReadBytesUntraced(int count, string field)
    {
        ValidateAllocation(count, field);
        byte[] bytes = new byte[count];
        _ = ReadExact(bytes, field);
        return bytes;
    }

    private long ReadExact(Span<byte> destination, string field)
    {
        EnsureAvailable(destination.Length, field);
        long offset = Position;
        stream.ReadExactly(destination);
        return offset;
    }

    private void ValidateAllocation(int count, string field)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > MaximumAllocation)
        {
            throw Corrupt(field, $"Allocation of {count} bytes exceeds limit {MaximumAllocation}.");
        }
    }
}
