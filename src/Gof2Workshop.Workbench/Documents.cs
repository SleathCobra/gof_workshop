using System.Collections.ObjectModel;

namespace Gof2Workshop.Workbench;

public interface IDocument : IDisposable
{
    public string Id { get; }

    public string Title { get; }

    public string Kind { get; }

    public string? SourcePath { get; }

    public bool IsReadOnly { get; }
}

public sealed record EditorOpenContext(
    IndexedAsset Asset,
    WorkspaceDefinition Workspace,
    CancellationToken CancellationToken);

public interface IDocumentEditorProvider
{
    public string Name { get; }

    public int Priority { get; }

    public bool CanOpen(IndexedAsset asset);

    public Task<IDocument> OpenAsync(EditorOpenContext context);
}

public sealed class DocumentEditorRegistry
{
    private readonly List<IDocumentEditorProvider> providers = [];

    public void Register(IDocumentEditorProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        providers.Add(provider);
        providers.Sort((left, right) => right.Priority.CompareTo(left.Priority));
    }

    public IDocumentEditorProvider? Resolve(IndexedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return providers.FirstOrDefault(provider => provider.CanOpen(asset));
    }
}

public sealed class DocumentManager : IDisposable
{
    private readonly DocumentEditorRegistry registry;
    private readonly object gate = new();
    private readonly List<IDocument> documents = [];
    private readonly Dictionary<string, Task<IDocument>> pendingOpens =
        new(StringComparer.OrdinalIgnoreCase);
    private IDocument? activeDocument;
    private bool disposed;

    public DocumentManager(DocumentEditorRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public event EventHandler? Changed;

    public IReadOnlyList<IDocument> Documents
    {
        get
        {
            lock (gate)
            {
                return new ReadOnlyCollection<IDocument>(documents.ToArray());
            }
        }
    }

    public IDocument? ActiveDocument
    {
        get
        {
            lock (gate)
            {
                return activeDocument;
            }
        }

        set
        {
            bool changed;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (value is not null && !documents.Contains(value))
                {
                    throw new ArgumentException("The active document must be open.", nameof(value));
                }

                changed = !ReferenceEquals(activeDocument, value);
                activeDocument = value;
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IDocument Add(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IDocument? existing;
        bool added;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            existing = documents.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Id,
                    document.Id,
                    StringComparison.OrdinalIgnoreCase));
            added = existing is null;
            if (added)
            {
                documents.Add(document);
                activeDocument = document;
            }
            else
            {
                activeDocument = existing;
            }
        }

        if (existing is not null)
        {
            document.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added ? document : existing!;
    }

    public Task<IDocument> OpenAsync(
        IndexedAsset asset,
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(workspace);
        string id = NormalizeDocumentId(asset.FullPath);
        IDocument? existing;
        Task<IDocument>? pending;
        TaskCompletionSource<IDocument>? completion = null;
        bool activeChanged = false;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            existing = documents.FirstOrDefault(
                document => string.Equals(document.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                activeChanged = !ReferenceEquals(activeDocument, existing);
                activeDocument = existing;
                pending = null;
            }
            else if (pendingOpens.TryGetValue(id, out pending))
            {
                // A double-click or another navigation surface is already opening this asset.
            }
            else
            {
                completion = new TaskCompletionSource<IDocument>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending = completion.Task;
                pendingOpens.Add(id, pending);
            }
        }

        if (existing is not null)
        {
            if (activeChanged)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return Task.FromResult(existing);
        }

        if (completion is not null)
        {
            _ = CompleteOpenAsync(
                id,
                asset,
                workspace,
                completion,
                cancellationToken);
        }

        return pending!;
    }

    private async Task CompleteOpenAsync(
        string id,
        IndexedAsset asset,
        WorkspaceDefinition workspace,
        TaskCompletionSource<IDocument> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            IDocumentEditorProvider provider = registry.Resolve(asset)
                ?? throw new NotSupportedException(
                    $"No editor provider can open '{asset.FileName}'.");
            IDocument opened = await provider.OpenAsync(
                new EditorOpenContext(asset, workspace, cancellationToken)).ConfigureAwait(false);
            IDocument result;
            bool disposeOpened;
            bool managerDisposed;
            lock (gate)
            {
                pendingOpens.Remove(id);
                managerDisposed = disposed;
                IDocument? existing = managerDisposed
                    ? null
                    : documents.FirstOrDefault(
                        document => string.Equals(
                            document.Id,
                            id,
                            StringComparison.OrdinalIgnoreCase));
                disposeOpened = managerDisposed || existing is not null;
                if (managerDisposed)
                {
                    result = opened;
                }
                else if (existing is not null)
                {
                    result = existing;
                    activeDocument = existing;
                }
                else
                {
                    result = opened;
                    documents.Add(opened);
                    activeDocument = opened;
                }
            }

            if (disposeOpened)
            {
                opened.Dispose();
            }

            if (managerDisposed)
            {
                completion.TrySetException(
                    new ObjectDisposedException(nameof(DocumentManager)));
                return;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            lock (gate)
            {
                pendingOpens.Remove(id);
            }

            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                pendingOpens.Remove(id);
            }

            completion.TrySetException(exception);
        }
    }

    public bool Close(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        bool closed;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int index = documents.IndexOf(document);
            closed = index >= 0;
            if (!closed)
            {
                return false;
            }

            documents.RemoveAt(index);
            if (ReferenceEquals(activeDocument, document))
            {
                activeDocument = documents.Count == 0
                    ? null
                    : documents[Math.Min(index, documents.Count - 1)];
            }
        }

        document.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
        return closed;
    }

    public void CloseAll()
    {
        IDocument[] closing;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            closing = documents.ToArray();
            documents.Clear();
            activeDocument = null;
        }

        foreach (IDocument document in closing)
        {
            document.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int CloseOthers(IDocument keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        IDocument[] closing;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!documents.Contains(keep))
            {
                return 0;
            }

            closing = documents.Where(document => !ReferenceEquals(document, keep)).ToArray();
            documents.RemoveAll(document => !ReferenceEquals(document, keep));
            activeDocument = keep;
        }

        DisposeClosedDocuments(closing);
        return closing.Length;
    }

    public int CloseToRight(IDocument keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        IDocument[] closing;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int index = documents.IndexOf(keep);
            if (index < 0 || index == documents.Count - 1)
            {
                return 0;
            }

            closing = documents.Skip(index + 1).ToArray();
            documents.RemoveRange(index + 1, closing.Length);
            if (closing.Contains(activeDocument))
            {
                activeDocument = keep;
            }
        }

        DisposeClosedDocuments(closing);
        return closing.Length;
    }

    public IReadOnlyList<WorkspaceDocumentState> CaptureState(string? gameRoot)
    {
        IDocument[] snapshot;
        lock (gate)
        {
            snapshot = documents.ToArray();
        }

        return snapshot
            .Where(document => document.SourcePath is not null)
            .Select(document => new WorkspaceDocumentState(
                ToPersistedPath(document.SourcePath!, gameRoot),
                document.Kind))
            .ToArray();
    }

    public WorkspaceDocumentState? CaptureActiveState(string? gameRoot)
    {
        IDocument? active = ActiveDocument;
        return active?.SourcePath is null
            ? null
            : new WorkspaceDocumentState(
                ToPersistedPath(active.SourcePath, gameRoot),
                active.Kind);
    }

    public async Task RestoreAsync(
        IEnumerable<WorkspaceDocumentState> states,
        IEnumerable<IndexedAsset> availableAssets,
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        Dictionary<string, IndexedAsset> assets = availableAssets.ToDictionary(
            asset => NormalizeDocumentId(asset.FullPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceDocumentState state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate = Path.IsPathRooted(state.AssetPath)
                ? state.AssetPath
                : Path.Combine(workspace.GameAssetRoot ?? string.Empty, state.AssetPath);
            if (assets.TryGetValue(NormalizeDocumentId(candidate), out IndexedAsset? asset))
            {
                await OpenAsync(asset, workspace, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        IDocument[] closing;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            closing = documents.ToArray();
            documents.Clear();
            activeDocument = null;
            disposed = true;
        }

        foreach (IDocument document in closing)
        {
            document.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public static string NormalizeDocumentId(string path) => Path.GetFullPath(path);

    private void DisposeClosedDocuments(IDocument[] closing)
    {
        foreach (IDocument document in closing)
        {
            document.Dispose();
        }

        if (closing.Length > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string ToPersistedPath(string path, string? gameRoot)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(gameRoot) &&
            PathPolicy.IsWithin(fullPath, gameRoot))
        {
            return Path.GetRelativePath(Path.GetFullPath(gameRoot), fullPath);
        }

        return fullPath;
    }
}
