using System.Collections.ObjectModel;
using Gof2Workshop.Core;

namespace Gof2Workshop.Workbench;

public enum ProblemSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ProblemEntry(
    ProblemSeverity Severity,
    string AssetName,
    string? AssetPath,
    string? Format,
    string Message,
    long? Offset,
    string? Field,
    string? SuggestedAction)
{
    public static ProblemEntry Warning(
        IndexedAsset asset,
        string message,
        string? suggestedAction = null)
    {
        return new ProblemEntry(
            ProblemSeverity.Warning,
            asset.FileName,
            asset.FullPath,
            asset.Classification,
            message,
            null,
            null,
            suggestedAction);
    }

    public static ProblemEntry Error(
        IndexedAsset asset,
        string message,
        string? suggestedAction = null)
    {
        return new ProblemEntry(
            ProblemSeverity.Error,
            asset.FileName,
            asset.FullPath,
            asset.Classification,
            message,
            null,
            null,
            suggestedAction);
    }

    public static ProblemEntry FromDiagnostic(
        string assetName,
        string? assetPath,
        string? format,
        FormatDiagnostic diagnostic)
    {
        ProblemSeverity severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Info => ProblemSeverity.Information,
            DiagnosticSeverity.Warning => ProblemSeverity.Warning,
            DiagnosticSeverity.Error => ProblemSeverity.Error,
            _ => ProblemSeverity.Information,
        };
        return new ProblemEntry(
            severity,
            assetName,
            assetPath,
            format,
            diagnostic.Message,
            diagnostic.Offset,
            diagnostic.Section,
            null);
    }
}

public interface IProblemService
{
    public event EventHandler? Changed;

    public IReadOnlyList<ProblemEntry> Entries { get; }

    public void Add(ProblemEntry entry);

    public void AddRange(IEnumerable<ProblemEntry> entries);

    public void Clear();
}

public sealed class ProblemService : IProblemService
{
    private readonly object sync = new();
    private readonly List<ProblemEntry> entries = [];

    public event EventHandler? Changed;

    public IReadOnlyList<ProblemEntry> Entries
    {
        get
        {
            lock (sync)
            {
                return new ReadOnlyCollection<ProblemEntry>(entries.ToArray());
            }
        }
    }

    public void Add(ProblemEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (sync)
        {
            entries.Add(entry);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddRange(IEnumerable<ProblemEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (sync)
        {
            this.entries.AddRange(entries);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public enum OutputLevel
{
    Information,
    Warning,
    Error,
}

public sealed record OutputEntry(
    DateTimeOffset Timestamp,
    OutputLevel Level,
    string Category,
    string Message)
{
    public string DisplayText => $"[{Timestamp:HH:mm:ss}] {Level.ToString()[0]} {Category}: {Message}";
}

public interface IOutputService
{
    public event EventHandler? Changed;

    public IReadOnlyList<OutputEntry> Entries { get; }

    public void Write(OutputLevel level, string category, string message);
}

public sealed class OutputService : IOutputService
{
    private const int MaximumEntries = 10_000;
    private readonly object sync = new();
    private readonly List<OutputEntry> entries = [];

    public event EventHandler? Changed;

    public IReadOnlyList<OutputEntry> Entries
    {
        get
        {
            lock (sync)
            {
                return new ReadOnlyCollection<OutputEntry>(entries.ToArray());
            }
        }
    }

    public void Write(OutputLevel level, string category, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (sync)
        {
            entries.Add(new OutputEntry(DateTimeOffset.Now, level, category, message));
            if (entries.Count > MaximumEntries)
            {
                entries.RemoveRange(0, entries.Count - MaximumEntries);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
