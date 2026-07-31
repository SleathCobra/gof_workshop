using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Gof2Workshop.Binary;

namespace Gof2Workshop.GameData;

public sealed record LanguageEntry(int Index, string Value, long OriginalOffset);

public sealed record LanguageTable(
    IReadOnlyList<LanguageEntry> Entries,
    string? SourcePath = null)
{
    public string? LanguageName => Entries.Count > 1 && Entries[0].Value == "Language"
        ? Entries[1].Value
        : null;
}

public sealed class LanguageTableParser
{
    public const int MaximumEntries = 100_000;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public LanguageTable Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream input = File.OpenRead(path);
        return Parse(input, path);
    }

    public LanguageTable Parse(Stream input, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The language stream must be readable.", nameof(input));
        }

        List<LanguageEntry> entries = [];
        Span<byte> lengthBytes = stackalloc byte[2];
        long offset = input.CanSeek ? input.Position : 0;
        while (true)
        {
            int first = input.ReadByte();
            if (first < 0)
            {
                break;
            }

            int second = input.ReadByte();
            if (second < 0)
            {
                throw Corrupt(sourcePath, offset, "entry length", "The final length prefix is truncated.");
            }

            lengthBytes[0] = (byte)first;
            lengthBytes[1] = (byte)second;
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (entries.Count >= MaximumEntries)
            {
                throw Corrupt(
                    sourcePath,
                    offset,
                    "entry count",
                    $"More than the safety limit of {MaximumEntries:N0} entries was observed.");
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
            ReadExactly(input, bytes, sourcePath, offset + 2, entries.Count);
            string value;
            try
            {
                value = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new FormatParseException(
                    FormatFailureKind.Corrupt,
                    sourcePath,
                    offset + 2,
                    $"entry[{entries.Count}] UTF-8",
                    "The entry is not valid UTF-8.",
                    exception);
            }

            entries.Add(new LanguageEntry(entries.Count, value, offset));
            offset = checked(offset + 2L + length);
        }

        return new LanguageTable(new ReadOnlyCollection<LanguageEntry>(entries), sourcePath);
    }

    private static void ReadExactly(
        Stream input,
        Span<byte> destination,
        string? sourcePath,
        long offset,
        int index)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int current = input.Read(destination[read..]);
            if (current == 0)
            {
                throw Corrupt(
                    sourcePath,
                    offset + read,
                    $"entry[{index}] payload",
                    $"Expected {destination.Length:N0} bytes, but only {read:N0} remain.");
            }

            read += current;
        }
    }

    private static FormatParseException Corrupt(
        string? path,
        long offset,
        string field,
        string reason) => new(FormatFailureKind.Corrupt, path, offset, field, reason);
}

public sealed class LanguageTableWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public void Write(LanguageTable table, Stream output)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The language output stream must be writable.", nameof(output));
        }

        Span<byte> lengthBytes = stackalloc byte[2];
        for (int index = 0; index < table.Entries.Count; index++)
        {
            LanguageEntry entry = table.Entries[index];
            int byteCount = StrictUtf8.GetByteCount(entry.Value);
            if (byteCount > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"Language entry {index:N0} is {byteCount:N0} UTF-8 bytes; the format limit is 65,535.");
            }

            BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)byteCount);
            output.Write(lengthBytes);
            byte[] bytes = StrictUtf8.GetBytes(entry.Value);
            output.Write(bytes);
        }
    }

    public byte[] Write(LanguageTable table)
    {
        using MemoryStream output = new();
        Write(table, output);
        return output.ToArray();
    }
}

public sealed record ReplaceLanguageEntryOperation(
    int EntryIndex,
    string Before,
    string After,
    DateTimeOffset CreatedAtUtc);

public sealed record LanguageRecoveryDocument(
    int FormatVersion,
    string SourceHash,
    IReadOnlyList<ReplaceLanguageEntryOperation> Operations);

public sealed class LanguageEditSession
{
    public const int RecoveryFormatVersion = 1;

    private static readonly JsonSerializerOptions RecoveryJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly LanguageTable original;
    private readonly List<ReplaceLanguageEntryOperation> operations = [];
    private readonly Stack<ReplaceLanguageEntryOperation> redo = [];

    public LanguageEditSession(LanguageTable original)
    {
        this.original = original ?? throw new ArgumentNullException(nameof(original));
    }

    public IReadOnlyList<ReplaceLanguageEntryOperation> Operations => operations;

    public bool CanUndo => operations.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public bool IsDirty => operations.Count > 0;

    public LanguageTable Working => Derive();

    public void Replace(int index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        LanguageTable working = Derive();
        if ((uint)index >= (uint)working.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        string before = working.Entries[index].Value;
        if (string.Equals(before, value, StringComparison.Ordinal))
        {
            return;
        }

        operations.Add(new ReplaceLanguageEntryOperation(index, before, value, DateTimeOffset.UtcNow));
        redo.Clear();
    }

    public bool Undo()
    {
        if (operations.Count == 0)
        {
            return false;
        }

        ReplaceLanguageEntryOperation operation = operations[^1];
        operations.RemoveAt(operations.Count - 1);
        redo.Push(operation);
        return true;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out ReplaceLanguageEntryOperation? operation))
        {
            return false;
        }

        operations.Add(operation);
        return true;
    }

    public LanguageRecoveryDocument CaptureRecovery(string sourceHash) => new(
        RecoveryFormatVersion,
        sourceHash,
        operations.ToArray());

    public void Replay(LanguageRecoveryDocument recovery, string expectedSourceHash)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.FormatVersion != RecoveryFormatVersion)
        {
            throw new NotSupportedException(
                $"Language recovery version {recovery.FormatVersion} is unsupported.");
        }

        if (!string.Equals(recovery.SourceHash, expectedSourceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The language source hash changed; recovery operations were not replayed.");
        }

        operations.Clear();
        redo.Clear();
        foreach (ReplaceLanguageEntryOperation operation in recovery.Operations)
        {
            LanguageTable working = Derive();
            if ((uint)operation.EntryIndex >= (uint)working.Entries.Count ||
                !string.Equals(
                    working.Entries[operation.EntryIndex].Value,
                    operation.Before,
                    StringComparison.Ordinal))
            {
                operations.Clear();
                throw new InvalidDataException(
                    $"Recovery operation for entry {operation.EntryIndex:N0} does not match its expected prior value.");
            }

            operations.Add(operation);
        }
    }

    public string SerializeRecovery(string sourceHash) => JsonSerializer.Serialize(
        CaptureRecovery(sourceHash),
        RecoveryJsonOptions);

    private LanguageTable Derive()
    {
        LanguageEntry[] entries = original.Entries.ToArray();
        foreach (ReplaceLanguageEntryOperation operation in operations)
        {
            LanguageEntry entry = entries[operation.EntryIndex];
            entries[operation.EntryIndex] = entry with { Value = operation.After };
        }

        return new LanguageTable(entries, original.SourcePath);
    }
}
