namespace Gof2Workshop.Binary;

public enum FormatFailureKind
{
    Corrupt,
    Unsupported,
}

public sealed class FormatParseException : IOException
{
    public FormatParseException(
        FormatFailureKind failureKind,
        string? filePath,
        long offset,
        string field,
        string reason,
        Exception? innerException = null)
        : base(CreateMessage(failureKind, filePath, offset, field, reason), innerException)
    {
        FailureKind = failureKind;
        FilePath = filePath;
        Offset = offset;
        Field = field;
        Reason = reason;
    }

    public FormatFailureKind FailureKind { get; }

    public string? FilePath { get; }

    public long Offset { get; }

    public string Field { get; }

    public string Reason { get; }

    private static string CreateMessage(
        FormatFailureKind failureKind,
        string? filePath,
        long offset,
        string field,
        string reason)
    {
        string source = string.IsNullOrWhiteSpace(filePath) ? "<stream>" : filePath;
        return $"{failureKind} format at 0x{offset:X} in '{source}', field '{field}': {reason}";
    }
}
