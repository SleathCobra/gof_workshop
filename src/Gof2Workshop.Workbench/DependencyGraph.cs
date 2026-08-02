using System.Collections.ObjectModel;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.GameData;

namespace Gof2Workshop.Workbench;

public enum DependencyNodeKind
{
    BinFile,
    BinRecord,
    LanguageEntry,
    Ship,
    Item,
    Equipment,
    Weapon,
    System,
    SystemConnection,
    Station,
    Agent,
    WantedTarget,
    NewsEntry,
    SaveStructure,
    MissionCandidate,
    DialogueEntry,
    AemModel,
    AemSubmesh,
    AeiTexture,
    AeiAtlasRegion,
    MaterialMapping,
    ModAsset,
    GeneratedAsset,
    WorkspaceOverride,
    NativeHandler,
    UnknownExternalReference,
}

public enum DependencyEdgeKind
{
    Uses,
    References,
    TexturedBy,
    LocalizedBy,
    LocatedIn,
    ConnectedTo,
    EquippedBy,
    AttachedTo,
    Rewards,
    Targets,
    TriggeredBy,
    HandledBy,
    Overrides,
    Replaces,
    GeneratedFrom,
    CandidateMatch,
    ConfirmedMapping,
    MissingReference,
}

public enum RelationshipEvidenceLevel
{
    ConfirmedEncodedReference,
    ConfirmedExternalMapping,
    ConfirmedRuntimeResearch,
    ConfirmedByUser,
    HighConfidenceHeuristic,
    LowConfidenceCandidate,
    Unresolved,
    Broken,
}

public enum DependencyValidationState
{
    Valid,
    Warning,
    Broken,
    Unresolved,
}

public readonly record struct DependencyNodeId(string Value)
{
    public override string ToString() => Value;

    public static DependencyNodeId Asset(string profile, string relativePath) =>
        new($"{profile}|asset|{Normalize(relativePath)}");

    public static DependencyNodeId Record(
        string profile,
        GameDataFamily family,
        string relativePath,
        string stableRecordId) =>
        new($"{profile}|bin|{family}|{Normalize(relativePath)}|{stableRecordId}");

    public static DependencyNodeId Missing(string profile, string family, string value) =>
        new($"{profile}|missing|{family}|{value}");

    public static DependencyNodeId Language(string profile, string key) =>
        new($"{profile}|language|{key}");

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();
}

public sealed record DependencyNode(
    DependencyNodeId Id,
    DependencyNodeKind Kind,
    string DisplayName,
    string ProfileId,
    string SourcePath,
    string? RecordId = null,
    string? Detail = null,
    bool IsModified = false);

public sealed record DependencyEdge(
    string Id,
    DependencyNodeId Source,
    DependencyNodeId Target,
    DependencyEdgeKind Kind,
    RelationshipEvidenceLevel EvidenceLevel,
    string Evidence,
    string ProfileId,
    string? OriginatingField,
    bool Writable,
    DependencyValidationState ValidationState);

public sealed record DependencyGraphSnapshot(
    IReadOnlyList<DependencyNode> Nodes,
    IReadOnlyList<DependencyEdge> Edges,
    DateTimeOffset CreatedAt);

public sealed record DependencyGraphIssue(
    DependencyValidationState Severity,
    DependencyNodeId Source,
    DependencyNodeId Target,
    string Message,
    string? OriginatingField);

public interface IDependencyGraph
{
    public event EventHandler? Changed;

    public DependencyGraphSnapshot Snapshot();

    public void ReplaceScope(string scope, IEnumerable<DependencyNode> nodes, IEnumerable<DependencyEdge> edges);

    public IReadOnlyList<DependencyEdge> GetUses(DependencyNodeId id);

    public IReadOnlyList<DependencyEdge> GetReferencedBy(DependencyNodeId id);

    public IReadOnlyList<DependencyNode> Expand(DependencyNodeId root, int depth, int maximumNodes = 250);

    public bool TryGetNode(DependencyNodeId id, out DependencyNode? node);
}

public sealed record DependencyGraphFilter(
    DependencyEdgeKind? Relationship = null,
    RelationshipEvidenceLevel? Evidence = null,
    string? ProfileId = null);

public sealed record DependencyPath(
    IReadOnlyList<DependencyNodeId> Nodes,
    IReadOnlyList<DependencyEdge> Edges);

public interface IDependencyQueryService
{
    public IReadOnlyList<DependencyEdge> FilterEdges(
        DependencyGraphSnapshot snapshot,
        DependencyGraphFilter filter);

    public DependencyPath? FindShortestPath(
        DependencyGraphSnapshot snapshot,
        DependencyNodeId start,
        DependencyNodeId target,
        DependencyGraphFilter filter,
        int maximumVisitedNodes = 5_000);
}

/// <summary>
/// Bounded graph queries shared by desktop and browser presentation. Paths treat dependency edges
/// as navigable in both directions while preserving each edge's encoded source/target orientation.
/// </summary>
public sealed class DependencyQueryService : IDependencyQueryService
{
    public IReadOnlyList<DependencyEdge> FilterEdges(
        DependencyGraphSnapshot snapshot,
        DependencyGraphFilter filter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);
        return snapshot.Edges.Where(edge =>
                (!filter.Relationship.HasValue || edge.Kind == filter.Relationship.Value) &&
                (!filter.Evidence.HasValue || edge.EvidenceLevel == filter.Evidence.Value) &&
                (string.IsNullOrWhiteSpace(filter.ProfileId) ||
                 string.Equals(edge.ProfileId, filter.ProfileId, StringComparison.Ordinal)))
            .ToArray();
    }

    public DependencyPath? FindShortestPath(
        DependencyGraphSnapshot snapshot,
        DependencyNodeId start,
        DependencyNodeId target,
        DependencyGraphFilter filter,
        int maximumVisitedNodes = 5_000)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVisitedNodes, 1);
        if (start == target)
        {
            return new DependencyPath([start], []);
        }

        IReadOnlyList<DependencyEdge> edges = FilterEdges(snapshot, filter);
        Dictionary<DependencyNodeId, List<DependencyEdge>> adjacent = [];
        foreach (DependencyEdge edge in edges)
        {
            AddAdjacent(adjacent, edge.Source, edge);
            AddAdjacent(adjacent, edge.Target, edge);
        }

        Queue<DependencyNodeId> pending = new();
        Dictionary<DependencyNodeId, (DependencyNodeId Previous, DependencyEdge Edge)> previous = [];
        HashSet<DependencyNodeId> visited = [start];
        pending.Enqueue(start);
        while (pending.Count > 0 && visited.Count <= maximumVisitedNodes)
        {
            DependencyNodeId current = pending.Dequeue();
            if (!adjacent.TryGetValue(current, out List<DependencyEdge>? candidates))
            {
                continue;
            }
            foreach (DependencyEdge edge in candidates.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                DependencyNodeId next = edge.Source == current ? edge.Target : edge.Source;
                if (!visited.Add(next))
                {
                    continue;
                }
                previous[next] = (current, edge);
                if (next == target)
                {
                    return Reconstruct(start, target, previous);
                }
                if (visited.Count >= maximumVisitedNodes)
                {
                    break;
                }
                pending.Enqueue(next);
            }
        }
        return null;
    }

    private static void AddAdjacent(
        Dictionary<DependencyNodeId, List<DependencyEdge>> adjacent,
        DependencyNodeId node,
        DependencyEdge edge)
    {
        if (!adjacent.TryGetValue(node, out List<DependencyEdge>? values))
        {
            values = [];
            adjacent[node] = values;
        }
        values.Add(edge);
    }

    private static DependencyPath Reconstruct(
        DependencyNodeId start,
        DependencyNodeId target,
        Dictionary<DependencyNodeId, (DependencyNodeId Previous, DependencyEdge Edge)> previous)
    {
        List<DependencyNodeId> nodes = [target];
        List<DependencyEdge> edges = [];
        DependencyNodeId current = target;
        while (current != start)
        {
            (DependencyNodeId parent, DependencyEdge edge) = previous[current];
            nodes.Add(parent);
            edges.Add(edge);
            current = parent;
        }
        nodes.Reverse();
        edges.Reverse();
        return new DependencyPath(nodes, edges);
    }
}

/// <summary>
/// Thread-safe, incremental graph. Producers replace only their own scope, so a document edit does
/// not force a corpus-wide rebuild. Queries return immutable snapshots suitable for desktop or WASM UI.
/// </summary>
public sealed class DependencyGraph : IDependencyGraph
{
    private readonly object gate = new();
    private readonly Dictionary<DependencyNodeId, DependencyNode> nodes = [];
    private readonly Dictionary<string, DependencyEdge> edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HashSet<DependencyNodeId> Nodes, HashSet<string> Edges)> scopes =
        new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public DependencyGraphSnapshot Snapshot()
    {
        lock (gate)
        {
            return new DependencyGraphSnapshot(
                new ReadOnlyCollection<DependencyNode>(nodes.Values.OrderBy(node => node.Id.Value, StringComparer.Ordinal).ToArray()),
                new ReadOnlyCollection<DependencyEdge>(edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray()),
                DateTimeOffset.UtcNow);
        }
    }

    public void ReplaceScope(string scope, IEnumerable<DependencyNode> nodes, IEnumerable<DependencyEdge> edges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        DependencyNode[] nodeSnapshot = nodes.ToArray();
        DependencyEdge[] edgeSnapshot = edges.ToArray();
        if (nodeSnapshot.Select(node => node.Id).Distinct().Count() != nodeSnapshot.Length)
        {
            throw new ArgumentException("A graph scope cannot contain duplicate node identities.", nameof(nodes));
        }

        if (edgeSnapshot.Select(edge => edge.Id).Distinct(StringComparer.Ordinal).Count() != edgeSnapshot.Length)
        {
            throw new ArgumentException("A graph scope cannot contain duplicate edge identities.", nameof(edges));
        }

        lock (gate)
        {
            if (scopes.Remove(scope, out var old))
            {
                foreach (string edge in old.Edges)
                {
                    this.edges.Remove(edge);
                }

                foreach (DependencyNodeId node in old.Nodes)
                {
                    bool ownedElsewhere = scopes.Values.Any(value => value.Nodes.Contains(node));
                    if (!ownedElsewhere)
                    {
                        this.nodes.Remove(node);
                    }
                }
            }

            HashSet<DependencyNodeId> ownedNodes = [];
            foreach (DependencyNode node in nodeSnapshot)
            {
                this.nodes[node.Id] = node;
                ownedNodes.Add(node.Id);
            }

            HashSet<string> ownedEdges = new(StringComparer.Ordinal);
            foreach (DependencyEdge edge in edgeSnapshot)
            {
                this.edges[edge.Id] = edge;
                ownedEdges.Add(edge.Id);
            }

            scopes[scope] = (ownedNodes, ownedEdges);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<DependencyEdge> GetUses(DependencyNodeId id)
    {
        lock (gate)
        {
            return edges.Values.Where(edge => edge.Source == id).OrderBy(edge => edge.Kind).ToArray();
        }
    }

    public IReadOnlyList<DependencyEdge> GetReferencedBy(DependencyNodeId id)
    {
        lock (gate)
        {
            return edges.Values.Where(edge => edge.Target == id).OrderBy(edge => edge.Kind).ToArray();
        }
    }

    public IReadOnlyList<DependencyNode> Expand(DependencyNodeId root, int depth, int maximumNodes = 250)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        if (maximumNodes is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        }

        lock (gate)
        {
            HashSet<DependencyNodeId> visited = [root];
            Queue<(DependencyNodeId Id, int Depth)> pending = new();
            pending.Enqueue((root, 0));
            while (pending.Count > 0 && visited.Count < maximumNodes)
            {
                (DependencyNodeId current, int currentDepth) = pending.Dequeue();
                if (currentDepth >= depth)
                {
                    continue;
                }

                IEnumerable<DependencyNodeId> adjacent = edges.Values
                    .Where(edge => edge.Source == current || edge.Target == current)
                    .Select(edge => edge.Source == current ? edge.Target : edge.Source);
                foreach (DependencyNodeId id in adjacent)
                {
                    if (visited.Add(id))
                    {
                        pending.Enqueue((id, currentDepth + 1));
                        if (visited.Count == maximumNodes)
                        {
                            break;
                        }
                    }
                }
            }

            return visited.Where(nodes.ContainsKey).Select(id => nodes[id]).ToArray();
        }
    }

    public bool TryGetNode(DependencyNodeId id, out DependencyNode? node)
    {
        lock (gate)
        {
            return nodes.TryGetValue(id, out node);
        }
    }
}

public interface IDependencyGraphBuilder
{
    public Task<DependencyGraphSnapshot> BuildAsync(
        string profileId,
        IEnumerable<IndexedAsset> assets,
        CancellationToken cancellationToken = default);
}

public sealed class DependencyGraphBuilder(IDependencyGraph graph) : IDependencyGraphBuilder
{
    private readonly IDependencyGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));

    public async Task<DependencyGraphSnapshot> BuildAsync(
        string profileId,
        IEnumerable<IndexedAsset> assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        IndexedAsset[] assetSnapshot = assets.ToArray();
        List<DependencyNode> nodes = [];
        List<DependencyEdge> edges = [];
        Dictionary<GameDataFamily, Dictionary<string, DependencyNodeId>> recordsByFamily = [];
        Dictionary<string, DependencyNodeId> texturesByStem = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DependencyNodeId> languageKeys = new(StringComparer.Ordinal);
        List<(IndexedAsset Asset, GameDataDocument Document)> gameDataDocuments = [];
        AssetPlatformProfile profile = ProfileCatalog.Resolve(profileId);

        foreach (IndexedAsset asset in assetSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DependencyNodeId assetId = DependencyNodeId.Asset(profileId, asset.RelativePath);
            DependencyNodeKind kind = asset.Kind switch
            {
                AssetKind.Aem => DependencyNodeKind.AemModel,
                AssetKind.Aei => DependencyNodeKind.AeiTexture,
                AssetKind.Language => DependencyNodeKind.BinFile,
                AssetKind.GameData => DependencyNodeKind.BinFile,
                _ => asset.Ownership == AssetOwnership.Mod ? DependencyNodeKind.ModAsset : DependencyNodeKind.GeneratedAsset,
            };
            nodes.Add(new DependencyNode(assetId, kind, asset.FileName, profileId, asset.RelativePath));
            if (asset.Kind == AssetKind.Aei)
            {
                texturesByStem.TryAdd(Path.GetFileNameWithoutExtension(asset.FileName), assetId);
                try
                {
                    AeiFile texture = new AeiParser().Parse(asset.FullPath, new AeiParserOptions(profile), cancellationToken);
                    foreach (AeiRegion region in texture.Regions)
                    {
                        DependencyNodeId regionId = new($"{assetId.Value}|region|{region.Index}");
                        nodes.Add(new DependencyNode(regionId, DependencyNodeKind.AeiAtlasRegion,
                            $"Region {region.Index}", profileId, asset.RelativePath,
                            region.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            $"{region.X},{region.Y} {region.Width}x{region.Height}"));
                        edges.Add(CreateEdge(assetId, regionId, DependencyEdgeKind.Uses,
                            RelationshipEvidenceLevel.ConfirmedEncodedReference,
                            "The atlas region is encoded in the AEI container.", profileId, null, false));
                    }
                }
                catch (Exception exception) when (IsAssetReadFailure(exception))
                {
                    AddReadFailure(assetId, asset, profileId, exception, nodes, edges);
                }
            }
            else if (asset.Kind == AssetKind.Aem)
            {
                try
                {
                    AemFile model = new AemParser().Parse(asset.FullPath, new AemParserOptions(profile), cancellationToken);
                    foreach (AemSubmesh submesh in model.Submeshes)
                    {
                        DependencyNodeId submeshId = new($"{assetId.Value}|submesh|{submesh.Index}");
                        nodes.Add(new DependencyNode(submeshId, DependencyNodeKind.AemSubmesh,
                            $"{asset.FileName} · submesh {submesh.Index}", profileId, asset.RelativePath,
                            submesh.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            $"{submesh.Positions.Length:N0} vertices · {submesh.Indices.Length / 3:N0} triangles"));
                        edges.Add(CreateEdge(assetId, submeshId, DependencyEdgeKind.Uses,
                            RelationshipEvidenceLevel.ConfirmedEncodedReference,
                            "The submesh is encoded in the AEM container.", profileId, null, false));
                    }
                }
                catch (Exception exception) when (IsAssetReadFailure(exception))
                {
                    AddReadFailure(assetId, asset, profileId, exception, nodes, edges);
                }
            }
            else if (asset.Kind == AssetKind.Language)
            {
                try
                {
                    LanguageTable language = new LanguageTableParser().Parse(asset.FullPath);
                    foreach (LanguageEntry entry in language.Entries)
                    {
                        string key = entry.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (!languageKeys.TryGetValue(key, out DependencyNodeId entryId))
                        {
                            entryId = DependencyNodeId.Language(profileId, key);
                            languageKeys.Add(key, entryId);
                            nodes.Add(new DependencyNode(entryId, DependencyNodeKind.LanguageEntry,
                                $"Language key {key}", profileId, asset.RelativePath, key,
                                "Locale-independent key; displayed value depends on the opened language table."));
                        }

                        edges.Add(CreateEdge(assetId, entryId, DependencyEdgeKind.Uses,
                            RelationshipEvidenceLevel.ConfirmedEncodedReference,
                            "This locale file contains the indexed language entry.", profileId, null, false));
                    }
                }
                catch (Exception exception) when (IsAssetReadFailure(exception))
                {
                    AddReadFailure(assetId, asset, profileId, exception, nodes, edges);
                }
            }

            if (asset.Kind != AssetKind.GameData)
            {
                continue;
            }

            GameDataDocument document;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(asset.FullPath, cancellationToken).ConfigureAwait(false);
                document = new GameDataFormatRegistry().Parse(asset.FileName, bytes);
            }
            catch (Exception exception) when (IsAssetReadFailure(exception))
            {
                AddReadFailure(assetId, asset, profileId, exception, nodes, edges);
                continue;
            }
            gameDataDocuments.Add((asset, document));
            foreach (GameDataRecord record in document.Records)
            {
                string stableId = StableRecordId(document.Family, record);
                DependencyNodeId recordId = DependencyNodeId.Record(profileId, document.Family, asset.RelativePath, stableId);
                nodes.Add(new DependencyNode(
                    recordId,
                    NodeKind(document.Family),
                    RecordName(document.Family, record, stableId),
                    profileId,
                    asset.RelativePath,
                    stableId));
                edges.Add(CreateEdge(assetId, recordId, DependencyEdgeKind.Uses,
                    RelationshipEvidenceLevel.ConfirmedEncodedReference,
                    "The record is physically contained in this BIN file.", profileId, null, false));
                if (!recordsByFamily.TryGetValue(document.Family, out Dictionary<string, DependencyNodeId>? familyRecords))
                {
                    familyRecords = new Dictionary<string, DependencyNodeId>(StringComparer.OrdinalIgnoreCase);
                    recordsByFamily.Add(document.Family, familyRecords);
                }

                familyRecords.TryAdd(stableId, recordId);
            }
        }

        AddFieldReferences(profileId, gameDataDocuments, nodes, edges, recordsByFamily, languageKeys);
        AddAssetCandidates(profileId, nodes, edges, texturesByStem);
        IGrouping<DependencyNodeId, DependencyNode>? duplicateNode = nodes
            .GroupBy(node => node.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNode is not null)
        {
            throw new InvalidDataException(
                $"Dependency producers generated duplicate node identity '{duplicateNode.Key.Value}' from: " +
                string.Join(", ", duplicateNode.Select(node => node.SourcePath)));
        }
        graph.ReplaceScope("corpus:" + profileId, nodes, edges);
        return graph.Snapshot();
    }

    private static bool IsAssetReadFailure(Exception exception) =>
        exception is IOException or InvalidDataException or NotSupportedException or ArgumentException;

    private static void AddReadFailure(
        DependencyNodeId assetId,
        IndexedAsset asset,
        string profileId,
        Exception exception,
        List<DependencyNode> nodes,
        List<DependencyEdge> edges)
    {
        DependencyNodeId failureId = new($"{assetId.Value}|read-failure");
        nodes.Add(new DependencyNode(
            failureId,
            DependencyNodeKind.UnknownExternalReference,
            $"Unreadable dependency metadata: {asset.FileName}",
            profileId,
            asset.RelativePath,
            Detail: exception.Message));
        edges.Add(CreateEdge(
            assetId,
            failureId,
            DependencyEdgeKind.MissingReference,
            RelationshipEvidenceLevel.Broken,
            $"Dependency metadata could not be read: {exception.Message}",
            profileId,
            null,
            false,
            DependencyValidationState.Broken));
    }

    private static void AddFieldReferences(
        string profileId,
        IEnumerable<(IndexedAsset Asset, GameDataDocument Document)> documents,
        List<DependencyNode> nodes,
        List<DependencyEdge> edges,
        Dictionary<GameDataFamily, Dictionary<string, DependencyNodeId>> recordsByFamily,
        Dictionary<string, DependencyNodeId> languageKeys)
    {
        foreach ((IndexedAsset asset, GameDataDocument document) in documents)
        {
            foreach (GameDataRecord record in document.Records)
            {
                string stableId = StableRecordId(document.Family, record);
                DependencyNodeId source = DependencyNodeId.Record(profileId, document.Family, asset.RelativePath, stableId);
                foreach (GameDataField field in record.Fields)
                {
                    if (field.Name.Equals("MessageId", StringComparison.Ordinal) &&
                        int.TryParse(field.Value, out int languageKey) && languageKey >= 0)
                    {
                        string key = languageKey.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        DependencyNodeId languageTarget = languageKeys.TryGetValue(key, out DependencyNodeId language)
                            ? language
                            : DependencyNodeId.Missing(profileId, "LanguageEntry", key);
                        bool languageFound = languageKeys.ContainsKey(key);
                        if (!languageFound && nodes.All(node => node.Id != languageTarget))
                        {
                            nodes.Add(new DependencyNode(languageTarget, DependencyNodeKind.UnknownExternalReference,
                                $"Missing language key {key}", profileId, string.Empty, key));
                        }
                        edges.Add(CreateEdge(source, languageTarget,
                            languageFound ? DependencyEdgeKind.LocalizedBy : DependencyEdgeKind.MissingReference,
                            languageFound ? RelationshipEvidenceLevel.ConfirmedEncodedReference : RelationshipEvidenceLevel.Broken,
                            "Agent MessageId indexes the locale language table.", profileId, field.Name,
                            field.Editable, languageFound ? DependencyValidationState.Valid : DependencyValidationState.Broken));
                        continue;
                    }

                    if (!TryReference(field, out GameDataFamily targetFamily, out DependencyEdgeKind kind) ||
                        !int.TryParse(field.Value, out int numeric) || numeric < 0)
                    {
                        continue;
                    }

                    string targetKey = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    DependencyNodeId target = default;
                    bool found = recordsByFamily.TryGetValue(targetFamily, out Dictionary<string, DependencyNodeId>? family) &&
                        family.TryGetValue(targetKey, out target);
                    if (!found)
                    {
                        target = DependencyNodeId.Missing(profileId, targetFamily.ToString(), targetKey);
                        if (nodes.All(node => node.Id != target))
                        {
                            nodes.Add(new DependencyNode(target, DependencyNodeKind.UnknownExternalReference,
                                $"Missing {targetFamily} {targetKey}", profileId, string.Empty, targetKey));
                        }
                    }

                    edges.Add(CreateEdge(
                        source,
                        target,
                        found ? kind : DependencyEdgeKind.MissingReference,
                        found ? RelationshipEvidenceLevel.ConfirmedEncodedReference : RelationshipEvidenceLevel.Broken,
                        $"Field {field.Name} stores a {targetFamily} record identifier.",
                        profileId,
                        field.Name,
                        field.Editable,
                        found ? DependencyValidationState.Valid : DependencyValidationState.Broken));
                }
            }
        }
    }

    private static void AddAssetCandidates(
        string profileId,
        List<DependencyNode> nodes,
        List<DependencyEdge> edges,
        Dictionary<string, DependencyNodeId> assetsByStem)
    {
        foreach (DependencyNode model in nodes.Where(node => node.Kind == DependencyNodeKind.AemModel))
        {
            string stem = Path.GetFileNameWithoutExtension(model.SourcePath);
            if (assetsByStem.TryGetValue(stem, out DependencyNodeId texture) && texture != model.Id)
            {
                edges.Add(CreateEdge(model.Id, texture, DependencyEdgeKind.CandidateMatch,
                    RelationshipEvidenceLevel.HighConfidenceHeuristic,
                    "AEM and AEI file stems match; this is a viewer candidate, not a confirmed game-effective mapping.",
                    profileId, null, false, DependencyValidationState.Warning));
            }
        }
    }

    private static bool TryReference(GameDataField field, out GameDataFamily target, out DependencyEdgeKind kind)
    {
        string name = field.Name;
        kind = DependencyEdgeKind.References;
        if (name.Contains("SystemId", StringComparison.Ordinal))
        {
            target = GameDataFamily.SystemsAndConnections;
            kind = name.Contains("Neighbour", StringComparison.Ordinal) ? DependencyEdgeKind.ConnectedTo : DependencyEdgeKind.LocatedIn;
            return true;
        }

        if (name.Contains("StationId", StringComparison.Ordinal))
        {
            target = GameDataFamily.Stations;
            kind = DependencyEdgeKind.LocatedIn;
            return true;
        }

        if (name.Contains("ShipId", StringComparison.Ordinal))
        {
            target = GameDataFamily.Ships;
            return true;
        }

        if (name.Contains("BlueprintId", StringComparison.Ordinal) ||
            name.Contains("LootItemId", StringComparison.Ordinal) ||
            name.Equals("WeaponId", StringComparison.Ordinal))
        {
            target = GameDataFamily.ItemsAndBlueprints;
            kind = name.Contains("Loot", StringComparison.Ordinal) ? DependencyEdgeKind.Rewards : DependencyEdgeKind.References;
            return true;
        }

        target = GameDataFamily.Unknown;
        return false;
    }

    private static string StableRecordId(GameDataFamily family, GameDataRecord record)
    {
        // Only these three families expose a confirmed unique record identifier. StationId and
        // OwnerId in agents/physical tables are foreign/owner references and legitimately repeat.
        string? identityField = family switch
        {
            GameDataFamily.WantedTargets => "Id",
            GameDataFamily.Ships => "ShipId",
            GameDataFamily.Stations => "StationId",
            _ => null,
        };
        GameDataField? id = identityField is null
            ? null
            : record.Fields.FirstOrDefault(field => field.Name == identityField);
        return id?.Value ?? record.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RecordName(GameDataFamily family, GameDataRecord record, string stableId)
    {
        string? name = record.Fields.FirstOrDefault(field => field.Name == "Name")?.Value;
        return string.IsNullOrWhiteSpace(name) ? $"{family} {stableId}" : name;
    }

    private static DependencyNodeKind NodeKind(GameDataFamily family) => family switch
    {
        GameDataFamily.Ships => DependencyNodeKind.Ship,
        GameDataFamily.ItemsAndBlueprints => DependencyNodeKind.Item,
        GameDataFamily.SystemsAndConnections => DependencyNodeKind.System,
        GameDataFamily.Stations => DependencyNodeKind.Station,
        GameDataFamily.Agents => DependencyNodeKind.Agent,
        GameDataFamily.WantedTargets => DependencyNodeKind.WantedTarget,
        GameDataFamily.NewsTicker => DependencyNodeKind.NewsEntry,
        _ => DependencyNodeKind.BinRecord,
    };

    private static DependencyEdge CreateEdge(
        DependencyNodeId source,
        DependencyNodeId target,
        DependencyEdgeKind kind,
        RelationshipEvidenceLevel evidence,
        string description,
        string profileId,
        string? field,
        bool writable,
        DependencyValidationState state = DependencyValidationState.Valid)
    {
        string id = $"{source.Value}>{kind}>{target.Value}>{field}";
        return new DependencyEdge(id, source, target, kind, evidence, description, profileId, field, writable, state);
    }
}

public sealed class DependencyReferenceValidator(IDependencyGraph graph)
{
    private readonly IDependencyGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));

    public IReadOnlyList<DependencyGraphIssue> Validate() => graph.Snapshot().Edges
        .Where(edge => edge.ValidationState is DependencyValidationState.Broken or DependencyValidationState.Unresolved)
        .Select(edge => new DependencyGraphIssue(
            edge.ValidationState,
            edge.Source,
            edge.Target,
            edge.Evidence,
            edge.OriginatingField))
        .ToArray();
}

public enum RelationshipDecision
{
    None,
    Confirmed,
    Rejected,
}

public sealed class RelationshipEvidenceService
{
    public RelationshipDecision GetDecision(WorkspaceDefinition workspace, DependencyEdge edge)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(edge);
        return workspace.RelationshipDecisions.TryGetValue(edge.Id, out string? value) &&
            Enum.TryParse(value, ignoreCase: true, out RelationshipDecision decision)
                ? decision
                : RelationshipDecision.None;
    }

    public void Confirm(WorkspaceDefinition workspace, DependencyEdge edge)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(edge);
        workspace.RelationshipDecisions[edge.Id] = RelationshipDecision.Confirmed.ToString();
    }

    public void Reject(WorkspaceDefinition workspace, DependencyEdge edge)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(edge);
        workspace.RelationshipDecisions[edge.Id] = RelationshipDecision.Rejected.ToString();
    }

    public void Clear(WorkspaceDefinition workspace, DependencyEdge edge)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(edge);
        workspace.RelationshipDecisions.Remove(edge.Id);
    }
}

/// <summary>
/// Projects workspace material choices into the shared graph without implying that a viewer-only
/// assignment is encoded by the game. This scope can be replaced after a single material edit.
/// </summary>
public sealed class MaterialDependencyContributor(IDependencyGraph graph)
{
    private readonly IDependencyGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));

    public void Update(string profileId, WorkspaceDefinition workspace, IEnumerable<IndexedAsset> assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(assets);
        IndexedAsset[] snapshot = assets.ToArray();
        Dictionary<string, IndexedAsset> byRelative = snapshot
            .GroupBy(asset => Normalize(asset.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IndexedAsset> byFull = snapshot
            .GroupBy(asset => Path.GetFullPath(asset.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<DependencyNode> nodes = [];
        List<DependencyEdge> edges = [];

        foreach ((string key, string configured) in workspace.MaterialOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int marker = key.LastIndexOf("#primitive=", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0 || !int.TryParse(key[(marker + 11)..], out int primitiveIndex) || primitiveIndex < 0)
            {
                continue;
            }

            string modelPath = Normalize(key[..marker]);
            DependencyNodeId model = DependencyNodeId.Asset(profileId, modelPath);
            DependencyNodeId submesh = new($"{model.Value}|submesh|{primitiveIndex}");
            DependencyNodeId mapping = new($"{model.Value}|material|{primitiveIndex}");
            nodes.Add(new DependencyNode(mapping, DependencyNodeKind.WorkspaceOverride,
                $"Material override · submesh {primitiveIndex}", profileId, modelPath,
                primitiveIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Viewer/export workspace metadata; not an AEM binary field."));
            edges.Add(CreateMaterialEdge(
                submesh,
                mapping,
                DependencyEdgeKind.Overrides,
                "The workspace contains an explicit material decision for this submesh.",
                profileId,
                DependencyValidationState.Valid));

            if (configured.Equals("!none", StringComparison.Ordinal))
            {
                continue;
            }

            IndexedAsset? texture = ResolveTexture(configured, workspace, byRelative, byFull);
            DependencyNodeId target;
            DependencyValidationState state;
            RelationshipEvidenceLevel evidence;
            DependencyEdgeKind kind;
            string description;
            if (texture is null)
            {
                target = DependencyNodeId.Missing(profileId, "AEI", configured);
                nodes.Add(new DependencyNode(target, DependencyNodeKind.UnknownExternalReference,
                    "Missing assigned AEI", profileId, configured, Detail: configured));
                state = DependencyValidationState.Broken;
                evidence = RelationshipEvidenceLevel.Broken;
                kind = DependencyEdgeKind.MissingReference;
                description = "The workspace material override points to an unavailable AEI asset.";
            }
            else
            {
                target = DependencyNodeId.Asset(profileId, texture.RelativePath);
                state = DependencyValidationState.Valid;
                evidence = RelationshipEvidenceLevel.ConfirmedByUser;
                kind = DependencyEdgeKind.ConfirmedMapping;
                description = "User-confirmed Workshop preview/export mapping; game-effective storage is not implied.";
            }

            edges.Add(new DependencyEdge(
                $"{mapping.Value}>{kind}>{target.Value}", mapping, target, kind, evidence,
                description, profileId, "Workspace.MaterialOverrides", true, state));
        }

        graph.ReplaceScope(
            "materials:" + profileId,
            nodes.GroupBy(node => node.Id).Select(group => group.First()),
            edges);
    }

    private static DependencyEdge CreateMaterialEdge(
        DependencyNodeId source,
        DependencyNodeId target,
        DependencyEdgeKind kind,
        string description,
        string profileId,
        DependencyValidationState state) => new(
            $"{source.Value}>{kind}>{target.Value}", source, target, kind,
            RelationshipEvidenceLevel.ConfirmedByUser, description, profileId,
            "Workspace.MaterialOverrides", true, state);

    private static IndexedAsset? ResolveTexture(
        string configured,
        WorkspaceDefinition workspace,
        Dictionary<string, IndexedAsset> byRelative,
        Dictionary<string, IndexedAsset> byFull)
    {
        if (byRelative.TryGetValue(Normalize(configured), out IndexedAsset? relative))
        {
            return relative;
        }

        string candidate = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(workspace.GameAssetRoot ?? Path.GetDirectoryName(workspace.FilePath) ?? ".", configured));
        return byFull.TryGetValue(candidate, out IndexedAsset? full) ? full : null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
