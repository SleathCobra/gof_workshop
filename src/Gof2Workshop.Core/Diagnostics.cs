using System.Collections.ObjectModel;
using System.Globalization;

namespace Gof2Workshop.Core;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record FormatDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    long? Offset = null,
    string? Section = null);

public sealed record ParseTraceEntry(
    string Section,
    string Field,
    long Offset,
    long Length,
    string InterpretedValue);

public sealed class ParseTrace
{
    public const int DefaultMaximumEntries = 100_000;

    private readonly List<ParseTraceEntry> entries = [];

    public ParseTrace(int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        MaximumEntries = maximumEntries;
    }

    public int MaximumEntries { get; }

    public bool IsTruncated { get; private set; }

    public ReadOnlyCollection<ParseTraceEntry> Entries => entries.AsReadOnly();

    public void Record(string section, string field, long offset, long length, object? value)
    {
        if (entries.Count >= MaximumEntries)
        {
            IsTruncated = true;
            return;
        }

        string text = value switch
        {
            null => "null",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        entries.Add(new ParseTraceEntry(section, field, offset, length, text));
    }
}
