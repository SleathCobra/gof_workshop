using System.Collections.ObjectModel;
using System.Text.Json;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public sealed class MissionDocumentViewModel : DocumentViewModelBase
{
    private readonly MissionEvidence mission;
    private readonly MissionResearchDocument research;

    public MissionDocumentViewModel(
        MissionEvidence mission,
        MissionResearchDocument research,
        IDependencyGraph dependencyGraph)
        : base(
            $"mission:{research.ProfileId}:{mission.Id}",
            mission.DisplayName,
            "Mission Research",
            null,
            isReadOnly: true)
    {
        ArgumentNullException.ThrowIfNull(dependencyGraph);
        this.mission = mission ?? throw new ArgumentNullException(nameof(mission));
        this.research = research ?? throw new ArgumentNullException(nameof(research));
        States = new ReadOnlyObservableCollection<MissionStateEvidence>(new ObservableCollection<MissionStateEvidence>(mission.States));
        Transitions = new ReadOnlyObservableCollection<MissionTransitionEvidence>(new ObservableCollection<MissionTransitionEvidence>(mission.Transitions));
        References = new ReadOnlyObservableCollection<MissionReferenceEvidence>(new ObservableCollection<MissionReferenceEvidence>(mission.References));
        Handlers = new ReadOnlyObservableCollection<NativeHandlerEvidence>(new ObservableCollection<NativeHandlerEvidence>(
            research.Handlers.Where(handler => mission.NativeHandlerIds.Contains(handler.Id, StringComparer.Ordinal))));
        ObjectiveTypes = new ReadOnlyObservableCollection<ObjectiveTypeEvidence>(
            new ObservableCollection<ObjectiveTypeEvidence>(research.ObjectiveTypes));
        DependencyNodeId nodeId = new($"{research.ProfileId}|mission|{mission.Id}");
        RelatedEdges = new ReadOnlyObservableCollection<DependencyEdge>(new ObservableCollection<DependencyEdge>(
            dependencyGraph.GetUses(nodeId).Concat(dependencyGraph.GetReferencedBy(nodeId))));
    }

    public string Summary => mission.Summary;

    public string EvidenceSource => mission.Source;

    public string Confidence => mission.Confidence.ToString();

    public string SafetyNotice => research.MissionCreationEnabled
        ? "A write target is available."
        : "READ-ONLY RESEARCH · Mission creation is gated because native runtime registration and save persistence are unresolved.";

    public ReadOnlyObservableCollection<MissionStateEvidence> States { get; }

    public ReadOnlyObservableCollection<MissionTransitionEvidence> Transitions { get; }

    public ReadOnlyObservableCollection<MissionReferenceEvidence> References { get; }

    public ReadOnlyObservableCollection<NativeHandlerEvidence> Handlers { get; }

    public ReadOnlyObservableCollection<ObjectiveTypeEvidence> ObjectiveTypes { get; }

    public ReadOnlyObservableCollection<DependencyEdge> RelatedEdges { get; }

    public IReadOnlyList<string> Objectives => mission.Objectives;

    public IReadOnlyList<string> Rewards => mission.Rewards;

    public IReadOnlyList<string> Unknowns => mission.Unknowns;

    public IReadOnlyList<string> CreationBlockers => research.CreationBlockers;

    public override IReadOnlyList<InspectorGroup> InspectorGroups =>
    [
        new("Mission evidence",
        [
            new InspectorProperty("Identity", mission.Id),
            new InspectorProperty("Kind", mission.Kind.ToString()),
            new InspectorProperty("Confidence", mission.Confidence.ToString()),
            new InspectorProperty("States", mission.States.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new InspectorProperty("References", mission.References.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]),
        new("Safety",
        [
            new InspectorProperty("Editing", research.MissionCreationEnabled ? "Available" : "Disabled"),
            new InspectorProperty("Reason", research.CreationBlockers.Count == 0 ? "No blocker" : research.CreationBlockers[0]),
        ], IsAdvanced: true),
    ];

    public override string AssetDetails => JsonSerializer.Serialize(mission, DetailsJsonOptions);
}
