using System.Numerics;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Import;
using Gof2Workshop.Export;

namespace Gof2Workshop.Formats.Aem.Tests;

[TestClass]
public sealed class AemAuthoringProjectTests
{
    [TestMethod]
    public void MultiSubmeshOperationsUndoAndBuildReparse()
    {
        AemAuthoringProject project = new("synthetic_authoring", AemVersion.V4);
        project.AddPrimitive(Triangle("Hull", 0));
        project.AddPrimitive(Triangle("Wing", 2));
        string hull = project.Current.Submeshes[0].StableId;
        string wing = project.Current.Submeshes[1].StableId;
        project.Move(wing, 0);
        project.Duplicate(hull);
        project.SetPivot(hull, new Vector3(1, 2, 3));
        project.AssignMaterial(hull, "synthetic_diffuse.aei");
        project.ReplaceTrack(hull, AemAnimationChannel.TranslationX,
        [
            new AemAuthoringKey(0, 0),
            new AemAuthoringKey(1, 4),
        ]);

        Assert.AreEqual(3, project.Current.Submeshes.Count);
        Assert.IsTrue(project.Undo());
        Assert.IsTrue(project.Redo());
        AemAuthoringResult result = project.Build();
        Assert.AreEqual(3, result.Reparsed.Submeshes.Count);
        Assert.AreEqual(new Vector3(1, 2, 3), result.Reparsed.Submeshes[1].Pivot);
        AemAnimationCurve curve = result.Reparsed.Submeshes[1].Animation.Curves.Single(
            value => value.Channel == AemAnimationChannel.TranslationX);
        Assert.AreEqual(2, curve.Keys.Count);
        Assert.AreEqual(4, curve.Keys[1].Value.X, 0.0001f);
    }

    [TestMethod]
    public void ImportsSelectedExistingAemSubmeshesAndRejectsUnsafeTargets()
    {
        AemAuthoringProject source = new("source", AemVersion.V5);
        source.AddPrimitive(Triangle("One", 0));
        source.AddPrimitive(Triangle("Two", 3));
        AemAuthoringResult built = source.Build();

        AemAuthoringProject destination = new("destination", AemVersion.V5);
        destination.AddFromAem(built.Reparsed, [1]);
        Assert.AreEqual(1, destination.Current.Submeshes.Count);
        Assert.AreEqual(3, destination.Build().Reparsed.Submeshes[0].Positions[0].X, 0.0001f);
        Assert.Throws<NotSupportedException>(() => new AemAuthoringProject("android", AemVersion.V5, "gof2-android"));
        Assert.Throws<NotSupportedException>(() => destination.ReplaceTrack(
            destination.Current.Submeshes[0].StableId,
            AemAnimationChannel.UvOffsetX,
            [new AemAuthoringKey(0, 1)]));
    }

    [TestMethod]
    public void AnimatedGltfReimportWritesAemAndPreservesTranslationTiming()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gof2-authoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AemAuthoringProject source = new("animated_source", AemVersion.V4);
            source.AddPrimitive(Triangle("Animated", 0));
            string stableId = source.Current.Submeshes[0].StableId;
            source.ReplaceTrack(stableId, AemAnimationChannel.TranslationX,
            [
                new AemAuthoringKey(0, 0),
                new AemAuthoringKey(2, 4),
            ]);
            AemAuthoringResult initial = source.Build();
            GltfExportResult exported = new GltfExporter().Export(initial.Scene, directory, "animated");

            ImportedScene imported = new GltfModelImporter().Import(exported.GltfPath);
            Assert.AreEqual(1, imported.Animations?.Count);
            AemAuthoringProject destination = new("animated_destination", AemVersion.V4);
            destination.AddImportedScene(imported);
            AemAuthoringResult rebuilt = destination.Build();
            var last = rebuilt.Scene.Animations.Single().Tracks.Single().Keys[^1];
            Assert.AreEqual(2, last.TimeSeconds, 0.001f);
            Assert.AreEqual(4, last.Translation.X, 0.001f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ScratchKeyEditingAndGeometryValidationAreOperationBased()
    {
        AemAuthoringProject project = new("scratch_animation", AemVersion.V4);
        project.AddPrimitive(Triangle("Animated", 0));
        string stableId = project.Current.Submeshes[0].StableId;
        project.AddKey(stableId, AemAnimationChannel.TranslationX, new AemAuthoringKey(0, 0));
        project.AddKey(stableId, AemAnimationChannel.TranslationX, new AemAuthoringKey(1, 3));
        Assert.HasCount(2, project.Current.Submeshes[0].AnimationTracks.Single().Keys);
        project.DeleteKey(stableId, AemAnimationChannel.TranslationX, 0);
        Assert.HasCount(1, project.Current.Submeshes[0].AnimationTracks.Single().Keys);
        Assert.IsTrue(project.Undo());
        Assert.HasCount(2, project.Current.Submeshes[0].AnimationTracks.Single().Keys);
        Assert.AreEqual(3, project.Build().Scene.Animations.Single().Tracks.Single().Keys[^1].Translation.X, 0.001f);

        ImportedPrimitive invalid = Triangle("Invalid", 0) with
        {
            TextureCoordinates = [new Vector2(float.NaN, 0), Vector2.Zero, Vector2.Zero],
        };
        AemAuthoringProject invalidProject = new("invalid", AemVersion.V4);
        invalidProject.AddPrimitive(invalid);
        Assert.Throws<InvalidDataException>(() => invalidProject.Build());
    }

    private static ImportedPrimitive Triangle(string name, float offset) => new(
        name,
        [new Vector3(offset, 0, 0), new Vector3(offset + 1, 0, 0), new Vector3(offset, 1, 0)],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        null,
        [0, 1, 2],
        "synthetic_diffuse");
}
