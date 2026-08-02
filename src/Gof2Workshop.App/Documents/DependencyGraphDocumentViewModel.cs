using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public sealed record DependencyGraphNodeVisual(
    DependencyNode Node,
    double X,
    double Y,
    string Border,
    string Detail)
{
    public string Label => Node.DisplayName;
}

public sealed record DependencyGraphEdgeVisual(
    DependencyEdge Edge,
    Point Start,
    Point End,
    string Stroke,
    string ToolTip);

/// <summary>
/// Bounded, lazy graph projection. It never materializes the complete corpus graph for display.
/// </summary>
public sealed class DependencyGraphDocumentViewModel : DocumentViewModelBase
{
    private readonly IDependencyGraph graph;
    private readonly DependencyQueryService queryService = new();
    private readonly Func<DependencyNode, Task> openNode;
    private readonly Func<string, Task>? exportReport;
    private DependencyGraphNodeVisual? selectedNode;
    private int depth = 1;
    private double zoom = 1;
    private string relationshipFilter = "All";
    private string evidenceFilter = "All";
    private string platformFilter = "All";
    private DependencyPath? tracedPath;

    public DependencyGraphDocumentViewModel(
        IDependencyGraph graph,
        DependencyNode root,
        Func<DependencyNode, Task> openNode,
        Func<string, Task>? exportReport = null)
        : base($"dependency-graph:{root.Id.Value}", $"References · {root.DisplayName}", "Dependency Graph", null, true)
    {
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Root = root ?? throw new ArgumentNullException(nameof(root));
        this.openNode = openNode ?? throw new ArgumentNullException(nameof(openNode));
        this.exportReport = exportReport;
        DependencyGraphSnapshot snapshot = graph.Snapshot();
        RelationshipFilters = ["All", .. Enum.GetNames<DependencyEdgeKind>()];
        EvidenceFilters = ["All", .. Enum.GetNames<RelationshipEvidenceLevel>()];
        PlatformFilters = ["All", .. snapshot.Nodes.Select(value => value.ProfileId)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        ExpandCommand = new RelayCommand(() => { Depth++; }, () => Depth < 4);
        CollapseCommand = new RelayCommand(() => { Depth--; }, () => Depth > 1);
        OpenCommand = new AsyncRelayCommand(
            parameter => parameter is DependencyGraphNodeVisual visual
                ? openNode(visual.Node)
                : SelectedNode is null ? Task.CompletedTask : openNode(SelectedNode.Node),
            parameter => parameter is DependencyGraphNodeVisual || SelectedNode is not null);
        TraceCommand = new RelayCommand(TraceSelected,
            () => SelectedNode is not null && SelectedNode.Node.Id != Root.Id);
        ClearTraceCommand = new RelayCommand(ClearTrace, () => tracedPath is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => this.exportReport is not null);
        Refresh();
    }

    public DependencyNode Root { get; }

    public ObservableCollection<DependencyGraphNodeVisual> Nodes { get; } = [];

    public ObservableCollection<DependencyGraphEdgeVisual> Edges { get; } = [];

    public IReadOnlyList<string> RelationshipFilters { get; }

    public IReadOnlyList<string> EvidenceFilters { get; }

    public IReadOnlyList<string> PlatformFilters { get; }

    public DependencyGraphNodeVisual? SelectedNode
    {
        get => selectedNode;
        set
        {
            if (SetProperty(ref selectedNode, value))
            {
                ((AsyncRelayCommand)OpenCommand).RaiseCanExecuteChanged();
                ((RelayCommand)TraceCommand).RaiseCanExecuteChanged();
                RaiseInspectorChanged();
            }
        }
    }

    public string RelationshipFilter
    {
        get => relationshipFilter;
        set
        {
            if (SetProperty(ref relationshipFilter, value ?? "All"))
            {
                FilterChanged();
            }
        }
    }

    public string EvidenceFilter
    {
        get => evidenceFilter;
        set
        {
            if (SetProperty(ref evidenceFilter, value ?? "All"))
            {
                FilterChanged();
            }
        }
    }

    public string PlatformFilter
    {
        get => platformFilter;
        set
        {
            if (SetProperty(ref platformFilter, value ?? "All"))
            {
                FilterChanged();
            }
        }
    }

    public int Depth
    {
        get => depth;
        private set
        {
            if (SetProperty(ref depth, Math.Clamp(value, 1, 4)))
            {
                Refresh();
                ((RelayCommand)ExpandCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CollapseCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public double Zoom
    {
        get => zoom;
        set
        {
            double bounded = Math.Clamp(value, 0.5, 2.0);
            if (SetProperty(ref zoom, bounded))
            {
                Refresh();
            }
        }
    }

    public double CanvasWidth { get; private set; } = 1200;

    public double CanvasHeight { get; private set; } = 700;

    public string Summary => $"{Nodes.Count:N0} nodes · {Edges.Count:N0} relationships · depth {Depth} · bounded to 120 nodes";

    public string TraceSummary => tracedPath is null
        ? "No traced path"
        : $"Shortest path: {tracedPath.Nodes.Count:N0} nodes / {tracedPath.Edges.Count:N0} edges";

    public System.Windows.Input.ICommand ExpandCommand { get; }

    public System.Windows.Input.ICommand CollapseCommand { get; }

    public System.Windows.Input.ICommand OpenCommand { get; }

    public System.Windows.Input.ICommand TraceCommand { get; }

    public System.Windows.Input.ICommand ClearTraceCommand { get; }

    public System.Windows.Input.ICommand ExportCommand { get; }

    public override IReadOnlyList<InspectorGroup> InspectorGroups
    {
        get
        {
            DependencyNode node = SelectedNode?.Node ?? Root;
            return
            [
                new("Dependency node",
                [
                    new InspectorProperty("Kind", node.Kind.ToString()),
                    new InspectorProperty("Name", node.DisplayName),
                    new InspectorProperty("Profile", node.ProfileId),
                    new InspectorProperty("Source", node.SourcePath),
                    new InspectorProperty("Record ID", node.RecordId ?? "—"),
                ]),
                new("Graph bounds",
                [
                    new InspectorProperty("Depth", Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new InspectorProperty("Visible nodes", Nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new InspectorProperty("Maximum", "120 nodes"),
                ], IsAdvanced: true),
            ];
        }
    }

    public override string AssetDetails => JsonSerializer.Serialize(new
    {
        root = Root.Id.Value,
        depth = Depth,
        nodes = Nodes.Select(value => value.Node),
        edges = Edges.Select(value => value.Edge),
    }, DetailsJsonOptions);

    private void Refresh()
    {
        DependencyGraphSnapshot snapshot = graph.Snapshot();
        IReadOnlyList<DependencyEdge> filteredEdges = queryService.FilterEdges(snapshot, CreateFilter());
        Dictionary<DependencyNodeId, DependencyNode> allNodes = snapshot.Nodes.ToDictionary(node => node.Id);
        Dictionary<DependencyNodeId, int> levels = new() { [Root.Id] = 0 };
        Queue<DependencyNodeId> pending = new();
        pending.Enqueue(Root.Id);
        while (pending.Count > 0 && levels.Count < 120)
        {
            DependencyNodeId current = pending.Dequeue();
            int currentLevel = levels[current];
            if (currentLevel >= Depth)
            {
                continue;
            }

            IEnumerable<DependencyNodeId> adjacent = filteredEdges.Where(edge => edge.Source == current)
                .Select(edge => edge.Target)
                .Concat(filteredEdges.Where(edge => edge.Target == current).Select(edge => edge.Source))
                .Distinct()
                .OrderBy(id => id.Value, StringComparer.Ordinal);
            foreach (DependencyNodeId id in adjacent)
            {
                if (levels.Count >= 120)
                {
                    break;
                }
                if (!levels.TryAdd(id, currentLevel + 1))
                {
                    continue;
                }
                pending.Enqueue(id);
            }
        }

        Dictionary<DependencyNodeId, DependencyGraphNodeVisual> visuals = [];
        foreach (IGrouping<int, KeyValuePair<DependencyNodeId, int>> level in levels.GroupBy(pair => pair.Value).OrderBy(group => group.Key))
        {
            int row = 0;
            foreach (KeyValuePair<DependencyNodeId, int> pair in level.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
            {
                if (!allNodes.TryGetValue(pair.Key, out DependencyNode? node))
                {
                    continue;
                }
                double x = (45 + level.Key * 285) * Zoom;
                double y = (45 + row++ * 92) * Zoom;
                bool traced = tracedPath?.Nodes.Contains(node.Id) == true;
                string border = traced ? "#62D394" : node.Kind == DependencyNodeKind.UnknownExternalReference ? "#E46F6F" :
                    node.IsModified ? "#D6A75C" : node.Id == Root.Id ? "#4EA1E8" : "#6F8299";
                visuals[pair.Key] = new DependencyGraphNodeVisual(node, x, y, border,
                    $"{node.Kind} · {node.RecordId ?? node.SourcePath}");
            }
        }

        Nodes.Clear();
        foreach (DependencyGraphNodeVisual visual in visuals.Values.OrderBy(value => levels[value.Node.Id]).ThenBy(value => value.Y))
        {
            Nodes.Add(visual);
        }
        Edges.Clear();
        foreach (DependencyEdge edge in filteredEdges.Where(edge => visuals.ContainsKey(edge.Source) && visuals.ContainsKey(edge.Target)))
        {
            DependencyGraphNodeVisual source = visuals[edge.Source];
            DependencyGraphNodeVisual target = visuals[edge.Target];
            string stroke = tracedPath?.Edges.Any(value => value.Id == edge.Id) == true ? "#62D394" :
                edge.ValidationState == DependencyValidationState.Broken ? "#E46F6F" :
                edge.EvidenceLevel is RelationshipEvidenceLevel.LowConfidenceCandidate or RelationshipEvidenceLevel.HighConfidenceHeuristic
                    ? "#D6A75C" : "#60748B";
            Edges.Add(new DependencyGraphEdgeVisual(edge,
                new Point(source.X + 220 * Zoom, source.Y + 29 * Zoom),
                new Point(target.X, target.Y + 29 * Zoom), stroke,
                $"{edge.Kind} · {edge.EvidenceLevel} · {edge.Evidence}"));
        }

        int widestLevel = levels.Values.DefaultIfEmpty().Max();
        int tallestCount = levels.GroupBy(pair => pair.Value).Select(group => group.Count()).DefaultIfEmpty(1).Max();
        CanvasWidth = Math.Max(900, (350 + widestLevel * 285) * Zoom);
        CanvasHeight = Math.Max(550, (120 + tallestCount * 92) * Zoom);
        SelectedNode = SelectedNode is null ? Nodes.FirstOrDefault(value => value.Node.Id == Root.Id) :
            Nodes.FirstOrDefault(value => value.Node.Id == SelectedNode.Node.Id) ?? Nodes.FirstOrDefault();
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(TraceSummary));
        OnPropertyChanged(nameof(AssetDetails));
        RaiseInspectorChanged();
    }

    private DependencyGraphFilter CreateFilter() => new(
        Enum.TryParse(RelationshipFilter, out DependencyEdgeKind relationship) ? relationship : null,
        Enum.TryParse(EvidenceFilter, out RelationshipEvidenceLevel evidence) ? evidence : null,
        PlatformFilter == "All" ? null : PlatformFilter);

    private void FilterChanged()
    {
        tracedPath = null;
        ((RelayCommand)ClearTraceCommand).RaiseCanExecuteChanged();
        Refresh();
    }

    private void TraceSelected()
    {
        if (SelectedNode is null)
        {
            return;
        }
        tracedPath = queryService.FindShortestPath(
            graph.Snapshot(), Root.Id, SelectedNode.Node.Id, CreateFilter(), maximumVisitedNodes: 5_000);
        ((RelayCommand)ClearTraceCommand).RaiseCanExecuteChanged();
        Refresh();
    }

    private void ClearTrace()
    {
        tracedPath = null;
        ((RelayCommand)ClearTraceCommand).RaiseCanExecuteChanged();
        Refresh();
    }

    private async Task ExportAsync()
    {
        if (exportReport is null)
        {
            return;
        }
        string json = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            root = Root,
            filter = CreateFilter(),
            trace = tracedPath,
            nodes = Nodes.Select(value => value.Node),
            edges = Edges.Select(value => value.Edge),
        }, DetailsJsonOptions);
        await exportReport(json);
    }
}
