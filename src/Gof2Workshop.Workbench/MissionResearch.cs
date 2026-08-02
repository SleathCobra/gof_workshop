using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Gof2Workshop.GameData;

namespace Gof2Workshop.Workbench;

public enum MissionEvidenceConfidence
{
    Confirmed,
    Strong,
    Hypothesis,
    Unknown,
}

public enum MissionEvidenceKind
{
    CampaignRuntime,
    FreelanceRuntime,
    WantedContract,
    SaveStateField,
    UnknownCandidate,
}

public sealed record MissionReferenceEvidence(
    string Kind,
    string Value,
    string Evidence,
    MissionEvidenceConfidence Confidence);

public sealed record MissionStateEvidence(
    string Id,
    string DisplayName,
    string Evidence,
    MissionEvidenceConfidence Confidence);

public sealed record MissionTransitionEvidence(
    string From,
    string To,
    string Trigger,
    string Evidence,
    MissionEvidenceConfidence Confidence);

public sealed record NativeHandlerEvidence(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Platforms,
    string CallingContext,
    string KnownEffects,
    string UnknownBehavior,
    string ResearchSource,
    MissionEvidenceConfidence Confidence);

public sealed record ObjectiveTypeEvidence(
    int Type,
    string DisplayName,
    string ObservedCondition,
    string Parameters,
    MissionEvidenceConfidence Confidence);

public sealed record MissionEvidence(
    string Id,
    string DisplayName,
    MissionEvidenceKind Kind,
    string ProfileId,
    string Summary,
    IReadOnlyList<MissionStateEvidence> States,
    IReadOnlyList<MissionTransitionEvidence> Transitions,
    IReadOnlyList<MissionReferenceEvidence> References,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> Rewards,
    IReadOnlyList<string> NativeHandlerIds,
    IReadOnlyList<string> Unknowns,
    MissionEvidenceConfidence Confidence,
    string Source);

public sealed record MissionResearchDocument(
    int FormatVersion,
    string ProfileId,
    IReadOnlyList<MissionEvidence> Missions,
    IReadOnlyList<NativeHandlerEvidence> Handlers,
    IReadOnlyList<ObjectiveTypeEvidence> ObjectiveTypes,
    IReadOnlyList<string> PlatformFindings,
    IReadOnlyList<string> CreationBlockers)
{
    public const int CurrentFormatVersion = 1;

    public bool MissionCreationEnabled => CreationBlockers.Count == 0;

    public string ExportJson() => JsonSerializer.Serialize(this, MissionJsonContext.Default.MissionResearchDocument);
}

/// <summary>
/// Builds a read-only research projection. It deliberately does not invent a deployable mission
/// format: campaign/freelance behavior is native, while wanted.bin contributes real data records.
/// </summary>
public sealed class MissionEvidenceService
{
    private static readonly IReadOnlyList<NativeHandlerEvidence> Handlers =
    [
        new("LevelScript.process", "LevelScript state dispatcher", ["GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Called by the active Level update loop.",
            "Dispatches campaign-specific sequences, camera events, spawns, dialogue and progression side effects.",
            "Many numeric states and temporary fields have not been assigned semantic names.",
            "gof2hd-decomp: game/world/LevelScript.cpp; independently correlated with Status campaign-step checks.",
            MissionEvidenceConfidence.Confirmed),
        new("Level.createCampaignMission", "Campaign mission constructor", ["GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Selected when the persisted Mission is marked as a campaign mission.",
            "Creates runtime objectives and entities according to the current campaign step.",
            "Campaign steps are native branches rather than records in a discovered BIN table.",
            "gof2hd-decomp: game/world/Level.cpp; DeepOpen Level.java behavioral comparison.",
            MissionEvidenceConfidence.Confirmed),
        new("Generator.createFreelanceMission", "Freelance mission generator", ["GOF2 PC", "GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Called while generating station agents and job offers.",
            "Builds mission types 0..12, target locations, rewards, difficulty and commodities at runtime.",
            "Probability tuning and all HD/mobile differences have not been exhaustively compared.",
            "DeepOpen Generator.java and Mission.java; observed constructor fields corroborate runtime-only generation.",
            MissionEvidenceConfidence.Confirmed),
        new("Dialogue.campaignTables", "Campaign dialogue tables", ["GOF2 PC", "GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Dialogue selects briefing/success entries using the campaign-step value.",
            "Maps native campaign steps to language-entry identifiers.",
            "The tables are compiled into program code, not a discovered editable BIN family.",
            "DeepOpen Dialogue.java and GameText.java.",
            MissionEvidenceConfidence.Confirmed),
        new("Status.currentCampaignMission", "Persisted campaign-step field", ["GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Read by progression gates, dialogue, wanted-target availability and LevelScript.",
            "Stores the current native campaign step in save state.",
            "A cross-version safe save writer and capacity contract are not established.",
            "gof2hd-decomp Status.h/.cpp; DeepOpen GameRecord.java.",
            MissionEvidenceConfidence.Confirmed),
        new("Objective.achieved", "Runtime objective evaluator", ["GOF2 PC", "GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Called from level and radio mission-update paths with live Level state.",
            "Evaluates objective type and parameters against entities, routes, timers, messages, asteroids and cargo state.",
            "Several objective types remain unnamed even where their observable condition is known.",
            "DeepOpen Objective.java corroborated by gof2hd-decomp Objective.h and Level call sites.",
            MissionEvidenceConfidence.Confirmed),
        new("Status.nextCampaignMission", "Campaign-step advance", ["GOF2 PC", "GOF2 Android", "GOF2 iOS", "GOF2 macOS"],
            "Invoked by native campaign dialogue and LevelScript branches after completion events.",
            "Advances persisted campaign progression and participates in save-state updates.",
            "Not every branch advances linearly; expansion and version-repair cases remain native.",
            "DeepOpen LevelScript.java/Status.java and gof2hd-decomp Status/LevelScript call sites.",
            MissionEvidenceConfidence.Confirmed),
    ];

    private static readonly IReadOnlyList<ObjectiveTypeEvidence> ObjectiveTypes =
    [
        new(0, "Eliminate all enemies", "Live enemy count reaches zero.", "No indexed target.", MissionEvidenceConfidence.Confirmed),
        new(1, "Eliminate target", "Indexed enemy is dead.", "Enemy index.", MissionEvidenceConfidence.Confirmed),
        new(2, "Reach route destination", "Last mission-route waypoint is reached.", "Runtime route.", MissionEvidenceConfidence.Confirmed),
        new(3, "Survive timer", "Elapsed mission time exceeds a threshold.", "Time threshold.", MissionEvidenceConfidence.Confirmed),
        new(4, "Wait for message", "Indexed radio/dialogue message has finished.", "Message index.", MissionEvidenceConfidence.Confirmed),
        new(5, "All allies lost", "Friendly count reaches zero.", "No indexed target.", MissionEvidenceConfidence.Confirmed),
        new(6, "Target death alias", "Indexed enemy is dead; distinction from type 1 is unresolved.", "Enemy index.", MissionEvidenceConfidence.Strong),
        new(7, "Eliminate enemy prefix", "All enemies in an indexed prefix are dead.", "Count/end index.", MissionEvidenceConfidence.Confirmed),
        new(8, "Destroy asteroid threshold", "Destroyed asteroid count exceeds a threshold.", "Asteroid threshold.", MissionEvidenceConfidence.Confirmed),
        new(9, "Asteroid iteration condition", "Storage and evaluator branch are observed; gameplay intent is unclear.", "Index threshold.", MissionEvidenceConfidence.Hypothesis),
        new(10, "Asteroid iteration condition", "Storage and evaluator branch are observed; gameplay intent is unclear.", "Index threshold.", MissionEvidenceConfidence.Hypothesis),
        new(11, "Recover mission crate", "Indexed carrier loses its mission crate to the player.", "Enemy index.", MissionEvidenceConfidence.Confirmed),
        new(12, "Mission crate destroyed", "Indexed carrier dies while carrying the crate.", "Enemy index.", MissionEvidenceConfidence.Confirmed),
        new(13, "Never achieved in observed evaluator", "The observed evaluator returns false.", "Unknown.", MissionEvidenceConfidence.Strong),
        new(14, "Level counter threshold", "A live Level counter reaches the stored value.", "Threshold; counter meaning unresolved.", MissionEvidenceConfidence.Strong),
        new(15, "Target active", "Indexed entity is active.", "Enemy index.", MissionEvidenceConfidence.Confirmed),
        new(16, "Recover all crates", "Every relevant enemy has yielded its mission crate.", "Implicit enemy collection.", MissionEvidenceConfidence.Confirmed),
        new(17, "Any crate destroyed", "Any relevant enemy dies with its mission crate.", "Implicit enemy collection.", MissionEvidenceConfidence.Confirmed),
        new(18, "Eliminate indexed range", "All enemies between two indices are dead.", "Start and exclusive end.", MissionEvidenceConfidence.Confirmed),
        new(19, "Cargo theft", "Friendly mission cargo was stolen.", "No numeric parameter.", MissionEvidenceConfidence.Confirmed),
        new(20, "Challenge victory", "Pirates in range are dead and player score leads.", "Enemy range.", MissionEvidenceConfidence.Confirmed),
        new(21, "Challenge loss", "Pirates in range are dead and player score does not lead.", "Enemy range.", MissionEvidenceConfidence.Confirmed),
        new(22, "Final message complete", "The final radio message has finished.", "Implicit final message.", MissionEvidenceConfidence.Confirmed),
        new(23, "Target stunned", "Indexed enemy is stunned.", "Enemy index.", MissionEvidenceConfidence.Confirmed),
        new(24, "Unresolved/default", "No successful condition is observed.", "Unknown.", MissionEvidenceConfidence.Unknown),
        new(25, "Target stopped", "Indexed enemy speed reaches zero.", "Enemy index.", MissionEvidenceConfidence.Confirmed),
    ];

    public MissionResearchDocument Build(string profileId, IEnumerable<GameDataDocument> documents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(documents);
        List<MissionEvidence> missions = [BuildCampaign(profileId), BuildFreelance(profileId)];
        foreach (GameDataDocument wanted in documents.Where(document => document.Family == GameDataFamily.WantedTargets))
        {
            missions.AddRange(wanted.Records.Select(record => BuildWanted(profileId, wanted, record)));
        }

        string platform = profileId switch
        {
            "gof2-pc-1x" => "PC corpus has the same wanted-table records; runtime campaign implementation is represented by the J2ME/HD behavioral references.",
            "gof2-android" => "Android runtime research directly corroborates native LevelScript and Status campaign-step handling.",
            "gof2-ios" => "iOS assets match Android structured tables, but executable handler addresses are platform-specific.",
            "gof2-macos" => "macOS assets match mobile structured tables, but physical runtime tracing has not been performed.",
            _ => "GOF3D research remains isolated and is not used to infer GOF2 mission semantics.",
        };
        return new MissionResearchDocument(
            MissionResearchDocument.CurrentFormatVersion,
            profileId,
            new ReadOnlyCollection<MissionEvidence>(missions),
            Handlers,
            ObjectiveTypes,
            [platform],
            [
                "No standalone GOF2 campaign-mission BIN table has been discovered.",
                "Campaign trigger registration and execution are native LevelScript/Level branches.",
                "Save persistence and executable capacity limits are not write-validated.",
                "Dialogue and reward invocation cannot be deployed as new missions without a proven runtime target.",
            ]);
    }

    private static MissionEvidence BuildCampaign(string profileId) => new(
        "campaign-runtime",
        "Campaign progression (native)",
        MissionEvidenceKind.CampaignRuntime,
        profileId,
        "A persisted campaign-step integer selects native LevelScript and Level branches; no standalone campaign record file was found.",
        [
            new("campaignStep", "Persisted campaign step", "Status.currentCampaignMission is restored and read throughout runtime progression.", MissionEvidenceConfidence.Confirmed),
            new("missionConstructed", "Runtime mission constructed", "Level.createCampaignMission selects native entities, routes and objectives for the current step.", MissionEvidenceConfidence.Confirmed),
            new("scriptState", "LevelScript local state", "LevelScript.m_nState, timers, counters, flags and events drive per-step cinematics and triggers.", MissionEvidenceConfidence.Confirmed),
            new("advanced", "Campaign state advanced", "Native completion branches invoke Status.nextCampaignMission and save/progression side effects.", MissionEvidenceConfidence.Confirmed),
        ],
        [
            new("campaignStep", "missionConstructed", "Enter a level with a non-empty campaign mission", "Level selects campaign construction from persisted state.", MissionEvidenceConfidence.Confirmed),
            new("missionConstructed", "scriptState", "Level update begins", "LevelScript dispatches on campaign step and local m_nState.", MissionEvidenceConfidence.Confirmed),
            new("scriptState", "advanced", "Step-specific completion branch", "Only native branches define the actual completion transition.", MissionEvidenceConfidence.Confirmed),
            new("advanced", "campaignStep", "Save/load or next level", "Persisted state becomes the input to the next runtime construction.", MissionEvidenceConfidence.Strong),
        ],
        [new("Save field", "currentCampaignMission", "Status/GameRecord storage and runtime reads.", MissionEvidenceConfidence.Confirmed)],
        ["Native objective construction varies by campaign step."],
        ["Native reward/dialogue effects vary by campaign step."],
        ["LevelScript.process", "Level.createCampaignMission", "Dialogue.campaignTables", "Status.currentCampaignMission", "Objective.achieved", "Status.nextCampaignMission"],
        ["Individual step semantics are only partially named.", "There is no write-safe registration format for a new campaign step."],
        MissionEvidenceConfidence.Confirmed,
        "Runtime behavior reference; not a deployable data record.");

    private static MissionEvidence BuildFreelance(string profileId) => new(
        "freelance-runtime",
        "Freelance mission generator", MissionEvidenceKind.FreelanceRuntime, profileId,
        "The runtime generates courier, defense, protection, recovery, pirate hunt, salvage, wanted, junk, purchase, escort, intercept, passenger and challenge jobs.",
        [
            new("offered", "Offered by agent", "Generator creates and attaches a Mission to an Agent.", MissionEvidenceConfidence.Confirmed),
            new("active", "Accepted/active", "Status stores the selected runtime Mission object.", MissionEvidenceConfidence.Confirmed),
            new("success", "Succeeded", "Level objectives and Dialogue apply completion effects.", MissionEvidenceConfidence.Confirmed),
            new("failed", "Failed", "Level fail objectives select failure dialogue/state.", MissionEvidenceConfidence.Confirmed),
        ],
        [], [], ["Mission-type-specific runtime Objective."], ["Credits and standing; parameters are generated at runtime."],
        ["Generator.createFreelanceMission"],
        ["There is no persisted catalog of individual generated jobs to edit ahead of runtime."],
        MissionEvidenceConfidence.Confirmed,
        "Native/procedural runtime behavior.");

    private static MissionEvidence BuildWanted(string profileId, GameDataDocument document, GameDataRecord record)
    {
        Dictionary<string, string> fields = record.Fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        string id = fields.GetValueOrDefault("Id", record.Index.ToString(CultureInfo.InvariantCulture));
        string name = fields.GetValueOrDefault("Name", $"Wanted target {id}");
        List<MissionReferenceEvidence> references = [];
        AddReference(references, fields, "ShipId", "Ship");
        AddReference(references, fields, "WeaponId", "Weapon/item");
        AddReference(references, fields, "LootItemId", "Loot item");
        AddReference(references, fields, "RequiredMissionId", "Campaign prerequisite");
        return new MissionEvidence(
            "wanted:" + id,
            name,
            MissionEvidenceKind.WantedContract,
            profileId,
            $"Wanted/bounty record {id}; board, target loadout, hitpoints, loot, reward and prerequisites are encoded in {document.Name}.",
            [
                new("locked", "Unavailable", "RequiredMissionId/RequiredBounties are checked before activation.", MissionEvidenceConfidence.Confirmed),
                new("available", "Available", "Runtime exposes eligible wanted entries.", MissionEvidenceConfidence.Confirmed),
                new("active", "Active", "Wanted runtime object tracks an active flag.", MissionEvidenceConfidence.Confirmed),
                new("terminated", "Terminated", "Wanted runtime object tracks a terminated flag.", MissionEvidenceConfidence.Confirmed),
            ],
            [
                new("locked", "available", "Campaign and bounty prerequisites satisfied", "Status runtime checks wanted prerequisites.", MissionEvidenceConfidence.Confirmed),
                new("active", "terminated", "Target defeated", "Wanted runtime state exposes termination.", MissionEvidenceConfidence.Strong),
            ],
            references,
            [$"Defeat wanted target {name}; exact encounter spawning remains native."],
            [$"Credits: {fields.GetValueOrDefault("Reward", "unknown")}", $"Loot item/amount: {fields.GetValueOrDefault("LootItemId", "?")} / {fields.GetValueOrDefault("LootAmount", "?")}"],
            ["Status.currentCampaignMission", "LevelScript.process"],
            ["Spawn location/travel state is runtime state, not present in this BIN record."],
            MissionEvidenceConfidence.Confirmed,
            document.Name + " record " + record.Index.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddReference(List<MissionReferenceEvidence> target, Dictionary<string, string> fields, string field, string kind)
    {
        if (fields.TryGetValue(field, out string? value))
        {
            target.Add(new MissionReferenceEvidence(kind, value, $"Encoded {field} field.", MissionEvidenceConfidence.Confirmed));
        }
    }
}

public sealed record MissionEvidenceFilter(
    string? Search = null,
    MissionEvidenceKind? Kind = null,
    MissionEvidenceConfidence? Confidence = null,
    string? HandlerId = null);

public sealed class MissionEvidenceQueryService
{
    public IReadOnlyList<MissionEvidence> Filter(
        MissionResearchDocument research,
        MissionEvidenceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(research);
        ArgumentNullException.ThrowIfNull(filter);
        return research.Missions.Where(mission =>
                (!filter.Kind.HasValue || mission.Kind == filter.Kind.Value) &&
                (!filter.Confidence.HasValue || mission.Confidence == filter.Confidence.Value) &&
                (string.IsNullOrWhiteSpace(filter.HandlerId) ||
                 mission.NativeHandlerIds.Contains(filter.HandlerId, StringComparer.Ordinal)) &&
                MatchesSearch(mission, filter.Search))
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesSearch(MissionEvidence mission, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }
        return mission.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               mission.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               mission.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               mission.References.Any(reference =>
                   reference.Kind.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   reference.Value.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               mission.NativeHandlerIds.Any(handler => handler.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record SaveDifferenceRange(int Offset, int Length, byte[] Before, byte[] After);

public static class SaveStateDiffer
{
    public static IReadOnlyList<SaveDifferenceRange> Compare(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (before.Length != after.Length)
        {
            throw new ArgumentException("Save differential analysis requires equal-length snapshots; resizing semantics are unknown.");
        }

        List<SaveDifferenceRange> ranges = [];
        int start = -1;
        for (int index = 0; index <= before.Length; index++)
        {
            bool changed = index < before.Length && before[index] != after[index];
            if (changed && start < 0)
            {
                start = index;
            }
            else if (!changed && start >= 0)
            {
                int length = index - start;
                ranges.Add(new SaveDifferenceRange(start, length, before.Slice(start, length).ToArray(), after.Slice(start, length).ToArray()));
                start = -1;
            }
        }

        return ranges;
    }
}

public sealed class MissionDependencyContributor(IDependencyGraph graph)
{
    private readonly IDependencyGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));

    public void Update(MissionResearchDocument research)
    {
        ArgumentNullException.ThrowIfNull(research);
        DependencyGraphSnapshot snapshot = graph.Snapshot();
        List<DependencyNode> nodes = [];
        List<DependencyEdge> edges = [];
        foreach (MissionEvidence mission in research.Missions)
        {
            DependencyNodeId missionId = new($"{research.ProfileId}|mission|{mission.Id}");
            nodes.Add(new DependencyNode(
                missionId,
                DependencyNodeKind.MissionCandidate,
                mission.DisplayName,
                research.ProfileId,
                mission.Source,
                mission.Id,
                mission.Summary));
            if (mission.Kind == MissionEvidenceKind.WantedContract)
            {
                string wantedId = mission.Id["wanted:".Length..];
                DependencyNode? wanted = snapshot.Nodes.FirstOrDefault(node =>
                    node.Kind == DependencyNodeKind.WantedTarget && node.RecordId == wantedId);
                if (wanted is not null)
                {
                    edges.Add(Edge(missionId, wanted.Id, DependencyEdgeKind.GeneratedFrom,
                        "Wanted-contract evidence is encoded by this wanted.bin record.", research.ProfileId));
                }

                foreach (MissionReferenceEvidence reference in mission.References)
                {
                    AddReferenceEvidence(research.ProfileId, missionId, reference, snapshot, nodes, edges);
                }
            }

            foreach (string handlerId in mission.NativeHandlerIds)
            {
                NativeHandlerEvidence? handler = research.Handlers.FirstOrDefault(value => value.Id == handlerId);
                if (handler is null)
                {
                    continue;
                }

                DependencyNodeId handlerNodeId = new($"{research.ProfileId}|handler|{handler.Id}");
                if (nodes.All(node => node.Id != handlerNodeId))
                {
                    nodes.Add(new DependencyNode(handlerNodeId, DependencyNodeKind.NativeHandler,
                        handler.DisplayName, research.ProfileId, handler.ResearchSource, handler.Id, handler.KnownEffects));
                }

                edges.Add(Edge(missionId, handlerNodeId, DependencyEdgeKind.HandledBy,
                    handler.CallingContext, research.ProfileId));
            }
        }

        graph.ReplaceScope("missions:" + research.ProfileId, nodes, edges);
    }

    private static void AddReferenceEvidence(
        string profile,
        DependencyNodeId mission,
        MissionReferenceEvidence reference,
        DependencyGraphSnapshot snapshot,
        List<DependencyNode> nodes,
        List<DependencyEdge> edges)
    {
        if (reference.Kind == "Campaign prerequisite")
        {
            DependencyNodeId step = new($"{profile}|mission-state|campaign:{reference.Value}");
            if (nodes.All(node => node.Id != step))
            {
                nodes.Add(new DependencyNode(step, DependencyNodeKind.MissionCandidate,
                    $"Campaign step {reference.Value}", profile, "Native campaign state",
                    reference.Value, "RequiredMissionId is compared with the persisted native campaign-step value."));
            }

            edges.Add(Edge(mission, step, DependencyEdgeKind.TriggeredBy,
                reference.Evidence + " The target is a native campaign step, not a wanted-table record.", profile));
            return;
        }

        DependencyNodeKind targetKind = reference.Kind switch
        {
            "Ship" => DependencyNodeKind.Ship,
            "Weapon/item" or "Loot item" => DependencyNodeKind.Item,
            _ => DependencyNodeKind.UnknownExternalReference,
        };
        DependencyNode? target = snapshot.Nodes.FirstOrDefault(node =>
            node.Kind == targetKind && string.Equals(node.RecordId, reference.Value, StringComparison.Ordinal));
        DependencyNodeId targetId;
        DependencyValidationState state;
        RelationshipEvidenceLevel evidence;
        DependencyEdgeKind edgeKind = reference.Kind == "Loot item"
            ? DependencyEdgeKind.Rewards
            : DependencyEdgeKind.References;
        if (target is null)
        {
            targetId = DependencyNodeId.Missing(profile, reference.Kind, reference.Value);
            if (nodes.All(node => node.Id != targetId))
            {
                nodes.Add(new DependencyNode(targetId, DependencyNodeKind.UnknownExternalReference,
                    $"Missing {reference.Kind} {reference.Value}", profile, string.Empty, reference.Value));
            }

            state = DependencyValidationState.Broken;
            evidence = RelationshipEvidenceLevel.Broken;
            edgeKind = DependencyEdgeKind.MissingReference;
        }
        else
        {
            targetId = target.Id;
            state = DependencyValidationState.Valid;
            evidence = RelationshipEvidenceLevel.ConfirmedEncodedReference;
        }

        edges.Add(new DependencyEdge(
            $"{mission.Value}>{edgeKind}>{targetId.Value}>{reference.Kind}", mission, targetId,
            edgeKind, evidence, reference.Evidence, profile, reference.Kind,
            Writable: false, state));
    }

    private static DependencyEdge Edge(
        DependencyNodeId source,
        DependencyNodeId target,
        DependencyEdgeKind kind,
        string evidence,
        string profile) => new(
            $"{source.Value}>{kind}>{target.Value}", source, target, kind,
            RelationshipEvidenceLevel.ConfirmedRuntimeResearch, evidence, profile, null,
            Writable: false, DependencyValidationState.Valid);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(MissionResearchDocument))]
internal sealed partial class MissionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
