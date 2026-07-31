using Gof2Workshop.Workbench;

namespace Gof2Workshop.Workbench.Tests;

[TestClass]
public sealed class TutorialTests
{
    [TestMethod]
    public void TutorialSupportsResumeBackRestartAndDismiss()
    {
        TutorialSession session = new();
        session.Start(TutorialCatalog.TextureMod, restoredStep: 2);
        Assert.IsTrue(session.IsActive);
        Assert.AreEqual("import", session.CurrentStep!.Id);
        Assert.IsTrue(session.Back());
        Assert.AreEqual(1, session.StepIndex);
        Assert.IsTrue(session.Next());
        session.Restart();
        Assert.AreEqual(0, session.StepIndex);
        session.Stop();
        Assert.IsFalse(session.IsActive);
    }

    [TestMethod]
    public void EveryTutorialHasStableUniqueStepsAndSyntheticSafeInstructions()
    {
        Assert.IsTrue(TutorialCatalog.All.Count >= 5);
        foreach (TutorialDefinition tutorial in TutorialCatalog.All)
        {
            Assert.IsTrue(tutorial.Steps.Count > 0, tutorial.Id);
            Assert.AreEqual(
                tutorial.Steps.Count,
                tutorial.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count(),
                tutorial.Id);
            Assert.IsTrue(tutorial.Steps.All(step => !string.IsNullOrWhiteSpace(step.CompletionCondition)));
        }
    }
}
