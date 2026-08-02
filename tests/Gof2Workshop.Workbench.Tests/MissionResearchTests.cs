using System.Buffers.Binary;
using Gof2Workshop.GameData;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.Workbench.Tests;

[TestClass]
public sealed class MissionResearchTests
{
    [TestMethod]
    public void WantedRecordsBecomeReadOnlyMissionEvidenceAndGraphNodes()
    {
        using MemoryStream source = new();
        WriteString(source, "Synthetic Target");
        int[] values = [7, 2, 1, 1, 3, 4, 500, 12, 2, 9000, 5, 6, 2];
        foreach (int value in values)
        {
            WriteInt(source, value);
        }
        WriteInt(source, 0);

        GameDataDocument wanted = new GameDataFormatRegistry().Parse("wanted.bin", source.ToArray());
        MissionResearchDocument research = new MissionEvidenceService().Build("gof2-pc-1x", [wanted]);
        MissionEvidence mission = research.Missions.Single(value => value.Id == "wanted:7");
        Assert.AreEqual(MissionEvidenceKind.WantedContract, mission.Kind);
        Assert.IsTrue(mission.References.Any(reference => reference.Kind == "Ship" && reference.Value == "3"));
        Assert.IsFalse(research.MissionCreationEnabled);
        Assert.IsTrue(research.ExportJson().Contains("LevelScript.process", StringComparison.Ordinal));
        Assert.IsTrue(research.ObjectiveTypes.Count >= 20);
        Assert.IsTrue(research.ObjectiveTypes.Any(value =>
            value.Type == 1 && value.DisplayName.Contains("target", StringComparison.OrdinalIgnoreCase)));

        DependencyGraph graph = new();
        DependencyNode wantedNode = new(
            DependencyNodeId.Record("gof2-pc-1x", GameDataFamily.WantedTargets, "wanted.bin", "7"),
            DependencyNodeKind.WantedTarget, "Synthetic Target", "gof2-pc-1x", "wanted.bin", "7");
        graph.ReplaceScope("bin", [wantedNode], []);
        new MissionDependencyContributor(graph).Update(research);
        DependencyNodeId missionNode = new("gof2-pc-1x|mission|wanted:7");
        Assert.IsTrue(graph.GetUses(missionNode).Any(edge => edge.Kind == DependencyEdgeKind.GeneratedFrom));
        Assert.IsTrue(graph.GetUses(missionNode).Any(edge => edge.Kind == DependencyEdgeKind.HandledBy));
    }

    [TestMethod]
    public void SaveDifferReportsOnlyChangedContiguousRanges()
    {
        byte[] before = [1, 2, 3, 4, 5, 6, 7];
        byte[] after = [1, 9, 8, 4, 5, 0, 7];
        IReadOnlyList<SaveDifferenceRange> ranges = SaveStateDiffer.Compare(before, after);
        Assert.AreEqual(2, ranges.Count);
        Assert.AreEqual(1, ranges[0].Offset);
        Assert.AreEqual(2, ranges[0].Length);
        Assert.AreEqual(5, ranges[1].Offset);
        Assert.Throws<ArgumentException>(() => SaveStateDiffer.Compare([1], [1, 2]));
    }

    [TestMethod]
    public void MissionDefinitionsRemainResearchOnlyAndProfileIsolated()
    {
        MissionResearchDocument pc = new MissionEvidenceService().Build("gof2-pc-1x", []);
        MissionResearchDocument android = new MissionEvidenceService().Build("gof2-android", []);
        Assert.IsFalse(pc.MissionCreationEnabled);
        Assert.IsFalse(android.MissionCreationEnabled);
        Assert.AreNotEqual(pc.ProfileId, android.ProfileId);
        Assert.IsTrue(pc.CreationBlockers.Any(value =>
            value.Contains("trigger registration", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(pc.Handlers.Any(value => value.Id == "LevelScript.process"));
    }

    [TestMethod]
    public void MissionQueryFiltersKindConfidenceHandlerAndReferences()
    {
        MissionResearchDocument research = new MissionEvidenceService().Build("gof2-pc-1x", []);
        MissionEvidenceQueryService query = new();

        MissionEvidence campaign = query.Filter(research, new(
            Kind: MissionEvidenceKind.CampaignRuntime,
            Confidence: MissionEvidenceConfidence.Confirmed,
            HandlerId: "LevelScript.process")).Single();
        Assert.AreEqual("campaign-runtime", campaign.Id);
        MissionEvidence freelance = query.Filter(research, new(Search: "Generator.createFreelanceMission")).Single();
        Assert.AreEqual(MissionEvidenceKind.FreelanceRuntime, freelance.Kind);
        Assert.AreEqual(0, query.Filter(research, new(Search: "not-a-real-mission-reference")).Count);
    }

    private static void WriteInt(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteString(Stream output, string value)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)text.Length));
        output.Write(length);
        output.Write(text);
    }
}
