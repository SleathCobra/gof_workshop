using System.Buffers.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.Workbench.Tests;

[TestClass]
public sealed class DependencyGraphTests
{
    [TestMethod]
    public void ScopeReplacementIsIncrementalAndQueriesAreBidirectional()
    {
        DependencyGraph graph = new();
        DependencyNodeId ship = new("pc|ship|1");
        DependencyNodeId system = new("pc|system|2");
        DependencyNodeId texture = new("pc|texture|x");
        DependencyNode[] nodes =
        [
            new(ship, DependencyNodeKind.Ship, "Ship", "gof2-pc-1x", "ships.bin"),
            new(system, DependencyNodeKind.System, "System", "gof2-pc-1x", "systems.bin"),
        ];
        DependencyEdge edge = new(
            "ship-system",
            ship,
            system,
            DependencyEdgeKind.LocatedIn,
            RelationshipEvidenceLevel.ConfirmedEncodedReference,
            "Synthetic reference",
            "gof2-pc-1x",
            "SystemId",
            true,
            DependencyValidationState.Valid);
        graph.ReplaceScope("bin", nodes, [edge]);

        Assert.AreEqual(1, graph.GetUses(ship).Count);
        Assert.AreEqual(1, graph.GetReferencedBy(system).Count);
        Assert.AreEqual(2, graph.Expand(ship, 1).Count);

        graph.ReplaceScope("assets", [new DependencyNode(texture, DependencyNodeKind.AeiTexture, "Texture", "gof2-pc-1x", "x.aei")], []);
        Assert.AreEqual(3, graph.Snapshot().Nodes.Count);
        graph.ReplaceScope("bin", [nodes[0]], []);
        DependencyGraphSnapshot replaced = graph.Snapshot();
        Assert.AreEqual(2, replaced.Nodes.Count);
        Assert.AreEqual(0, replaced.Edges.Count);
    }

    [TestMethod]
    public void BrokenReferencesAreStructuredProblems()
    {
        DependencyGraph graph = new();
        DependencyNodeId source = new("source");
        DependencyNodeId missing = new("missing");
        graph.ReplaceScope(
            "test",
            [
                new(source, DependencyNodeKind.Agent, "Agent", "pc", "agents.bin"),
                new(missing, DependencyNodeKind.UnknownExternalReference, "Missing station", "pc", string.Empty),
            ],
            [
                new DependencyEdge(
                    "broken",
                    source,
                    missing,
                    DependencyEdgeKind.MissingReference,
                    RelationshipEvidenceLevel.Broken,
                    "Station 99 does not exist.",
                    "pc",
                    "StationId",
                    true,
                    DependencyValidationState.Broken),
            ]);

        DependencyGraphIssue issue = new DependencyReferenceValidator(graph).Validate().Single();
        Assert.AreEqual(DependencyValidationState.Broken, issue.Severity);
        Assert.AreEqual("StationId", issue.OriginatingField);
    }

    [TestMethod]
    public void QueryServiceFiltersAndFindsBoundedShortestPath()
    {
        DependencyNodeId model = new("pc|model");
        DependencyNodeId texture = new("pc|texture");
        DependencyNodeId region = new("pc|region");
        DependencyNodeId unrelated = new("android|unrelated");
        DependencyEdge[] edges =
        [
            Edge("material", model, texture, DependencyEdgeKind.TexturedBy,
                RelationshipEvidenceLevel.ConfirmedByUser, "gof2-pc-1x"),
            Edge("region", texture, region, DependencyEdgeKind.Uses,
                RelationshipEvidenceLevel.ConfirmedEncodedReference, "gof2-pc-1x"),
            Edge("other", unrelated, texture, DependencyEdgeKind.CandidateMatch,
                RelationshipEvidenceLevel.LowConfidenceCandidate, "gof2-android"),
        ];
        DependencyGraphSnapshot snapshot = new(
            [
                new(model, DependencyNodeKind.AemModel, "Model", "gof2-pc-1x", "model.aem"),
                new(texture, DependencyNodeKind.AeiTexture, "Texture", "gof2-pc-1x", "texture.aei"),
                new(region, DependencyNodeKind.AeiAtlasRegion, "Region", "gof2-pc-1x", "texture.aei"),
                new(unrelated, DependencyNodeKind.BinRecord, "Other", "gof2-android", "other.bin"),
            ],
            edges,
            DateTimeOffset.UtcNow);
        DependencyQueryService service = new();

        IReadOnlyList<DependencyEdge> pc = service.FilterEdges(snapshot, new(ProfileId: "gof2-pc-1x"));
        Assert.AreEqual(2, pc.Count);
        DependencyPath path = service.FindShortestPath(snapshot, model, region, new(ProfileId: "gof2-pc-1x"))!;
        Assert.IsTrue(path.Nodes.SequenceEqual([model, texture, region]));
        Assert.IsTrue(path.Edges.Select(value => value.Id).SequenceEqual(["material", "region"]));
        Assert.IsNull(service.FindShortestPath(snapshot, model, unrelated, new(ProfileId: "gof2-pc-1x")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.FindShortestPath(snapshot, model, region, new(), maximumVisitedNodes: 0));
    }

    [TestMethod]
    public void NodeIdsAreStableAndProfileIsolated()
    {
        DependencyNodeId pc = DependencyNodeId.Record("gof2-pc-1x", Gof2Workshop.GameData.GameDataFamily.Stations, "TXT/STATIONS.BIN", "7");
        DependencyNodeId same = DependencyNodeId.Record("gof2-pc-1x", Gof2Workshop.GameData.GameDataFamily.Stations, "txt\\stations.bin", "7");
        DependencyNodeId android = DependencyNodeId.Record("gof2-android", Gof2Workshop.GameData.GameDataFamily.Stations, "txt/stations.bin", "7");
        Assert.AreEqual(pc, same);
        Assert.AreNotEqual(pc, android);
    }

    [TestMethod]
    public void UserEvidenceDecisionsRemainWorkspaceFacts()
    {
        WorkspaceDefinition workspace = new();
        DependencyEdge candidate = new(
            "candidate-edge", new DependencyNodeId("model"), new DependencyNodeId("texture"),
            DependencyEdgeKind.CandidateMatch, RelationshipEvidenceLevel.LowConfidenceCandidate,
            "Filename candidate", "gof2-pc-1x", null, false, DependencyValidationState.Warning);
        RelationshipEvidenceService service = new();
        Assert.AreEqual(RelationshipDecision.None, service.GetDecision(workspace, candidate));
        service.Confirm(workspace, candidate);
        Assert.AreEqual(RelationshipDecision.Confirmed, service.GetDecision(workspace, candidate));
        Assert.AreEqual(RelationshipEvidenceLevel.LowConfidenceCandidate, candidate.EvidenceLevel);
        service.Reject(workspace, candidate);
        Assert.AreEqual(RelationshipDecision.Rejected, service.GetDecision(workspace, candidate));
        service.Clear(workspace, candidate);
        Assert.AreEqual(RelationshipDecision.None, service.GetDecision(workspace, candidate));
    }

    [TestMethod]
    public async Task MetadataFailureIsIsolatedAsBrokenEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gof2-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "truncated.aem");
            await File.WriteAllBytesAsync(path, "V4AE"u8.ToArray());
            IndexedAsset asset = new(
                path,
                "meshes/truncated.aem",
                "truncated.aem",
                AssetKind.Aem,
                AssetOwnership.Game,
                4,
                DateTimeOffset.UtcNow,
                "AEM v4",
                "4",
                AssetSupport.Supported,
                true,
                null);
            DependencyGraph graph = new();

            DependencyGraphSnapshot snapshot = await new DependencyGraphBuilder(graph).BuildAsync(
                ProfileCatalog.Pc1X.Id,
                [asset]);

            Assert.IsTrue(snapshot.Nodes.Any(node => node.Id.Value.EndsWith("|read-failure", StringComparison.Ordinal)));
            Assert.IsTrue(snapshot.Edges.Any(edge => edge.ValidationState == DependencyValidationState.Broken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void WorkspaceMaterialOverridesAreViewerMappingsWithExplicitEvidence()
    {
        DependencyGraph graph = new();
        WorkspaceDefinition workspace = new()
        {
            GameAssetRoot = Path.GetTempPath(),
            MaterialOverrides =
            {
                ["meshes/ship.aem#primitive=0"] = "textures/ship.aei",
                ["meshes/ship.aem#primitive=1"] = "textures/missing.aei",
            },
        };
        IndexedAsset texture = new(
            Path.Combine(Path.GetTempPath(), "textures", "ship.aei"),
            "textures/ship.aei",
            "ship.aei",
            AssetKind.Aei,
            AssetOwnership.Game,
            1,
            DateTimeOffset.UtcNow,
            "Synthetic",
            null,
            AssetSupport.Supported,
            true,
            null);

        new MaterialDependencyContributor(graph).Update("gof2-pc-1x", workspace, [texture]);
        DependencyGraphSnapshot snapshot = graph.Snapshot();
        Assert.IsTrue(snapshot.Nodes.Any(node => node.Kind == DependencyNodeKind.WorkspaceOverride));
        Assert.IsTrue(snapshot.Edges.Any(edge =>
            edge.Kind == DependencyEdgeKind.ConfirmedMapping &&
            edge.EvidenceLevel == RelationshipEvidenceLevel.ConfirmedByUser &&
            edge.Evidence.Contains("game-effective", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(snapshot.Edges.Any(edge => edge.ValidationState == DependencyValidationState.Broken));
    }

    [TestMethod]
    public async Task RepeatedPhysicalOwnerIdsUseStableRecordIndices()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gof2-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = new byte[64];
            for (int group = 0; group < 2; group++)
            {
                int offset = group * 32;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), 17);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4, 4), 5);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8, 4), 1);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 28, 4), group + 1);
            }
            string path = Path.Combine(directory, "collision.bin");
            await File.WriteAllBytesAsync(path, bytes);
            IndexedAsset asset = new(
                path, "bin/collision.bin", "collision.bin", AssetKind.GameData, AssetOwnership.Game,
                bytes.Length, DateTimeOffset.UtcNow, "CollisionGeometry", null,
                AssetSupport.Supported, false, null);

            DependencyGraphSnapshot snapshot = await new DependencyGraphBuilder(new DependencyGraph()).BuildAsync(
                ProfileCatalog.Pc1X.Id,
                [asset]);
            DependencyNode[] records = snapshot.Nodes.Where(node =>
                node.Kind == DependencyNodeKind.BinRecord && node.SourcePath == "bin/collision.bin").ToArray();
            Assert.AreEqual(2, records.Length);
            Assert.AreNotEqual(records[0].Id, records[1].Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DependencyEdge Edge(
        string id,
        DependencyNodeId source,
        DependencyNodeId target,
        DependencyEdgeKind kind,
        RelationshipEvidenceLevel evidence,
        string profile) => new(
            id, source, target, kind, evidence, "Synthetic evidence", profile, null,
            Writable: false, DependencyValidationState.Valid);
}
