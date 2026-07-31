namespace Gof2Workshop.Workbench;

public sealed record TutorialStep(
    string Id,
    string Title,
    string Instruction,
    string Target,
    string CompletionCondition);

public sealed record TutorialDefinition(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<TutorialStep> Steps);

public static class TutorialCatalog
{
    public static readonly TutorialDefinition QuickInspect = new(
        "quick-inspect",
        "Quick inspection",
        "Inspect synthetic textures and models without creating a workspace.",
        [
            new("open", "Open sample files", "Use File > Open Files for Quick Inspect and choose a synthetic AEI and AEM.", "File/OpenFiles", "An inspection collection contains AEI and AEM assets."),
            new("texture", "Inspect the texture", "Open the AEI and inspect its regions and mip metadata.", "Explorer/QuickInspect", "An AEI document is active."),
            new("model", "Inspect the model", "Open the AEM, orbit it, and select a submesh.", "Aem/Viewport", "An AEM document has an active submesh."),
            new("material", "Review its material", "Review the resolved texture and confidence in Materials.", "Aem/Materials", "A material resolution has been reviewed."),
            new("export", "Export glTF", "Export a glTF copy to a folder you own.", "Document/Export", "A glTF export completes."),
        ]);

    public static readonly TutorialDefinition TextureMod = new(
        "texture-mod",
        "Texture mod",
        "Make and build a nondestructive edit using only the CC0 synthetic corpus.",
        [
            new("workspace", "Open the sample workspace", "Open samples/SyntheticDemo/project.gof2workspace.", "File/OpenWorkspace", "The Public Synthetic Demo workspace is active."),
            new("region", "Select a region", "Open synthetic_raw.aei and select its first atlas region.", "Aei/Regions", "An atlas region is selected."),
            new("import", "Import a replacement", "Import synthetic-region-replacement.png for the selected 8×8 region.", "Aei/ImportRegion", "A replace-region operation exists."),
            new("compare", "Compare", "Switch between Original, Working, and Difference views.", "Aei/Comparison", "The difference view has been inspected."),
            new("undo", "Undo and redo", "Undo the replacement, then redo it.", "Edit/UndoRedo", "The operation is restored."),
            new("validate", "Validate", "Validate reconstruction, reparse, and decode.", "Aei/Validate", "Validation succeeds."),
            new("stage", "Stage", "Stage the validated working asset.", "Changes/Stage", "One validated change is staged."),
            new("build", "Build", "Run Build Mod and inspect its report.", "Tools/BuildMod", "A deterministic sample build completes."),
        ]);

    public static readonly TutorialDefinition ModelImport = new(
        "model-import",
        "Model import",
        "Convert the public synthetic glTF to a validated AEM.",
        [
            new("import", "Import glTF", "Choose Asset > Import Custom Model and select synthetic_cube_import.gltf.", "Asset/ImportModel", "The import validation dialog opens."),
            new("profile", "Choose AEM v4", "Use the safe PC AEM v4 target.", "Import/TargetProfile", "AEM v4 is selected."),
            new("write", "Write and reparse", "Choose a workspace output and complete conversion.", "Import/Write", "The authored AEM reparses."),
            new("preview", "Preview", "Orbit the authored model and review its bounds.", "Aem/Viewport", "The authored AEM is open."),
        ]);

    public static readonly TutorialDefinition Animation = new(
        "animation-round-trip",
        "Animation inspection",
        "Inspect confirmed transform keys without rewriting unresolved channels.",
        [
            new("open", "Open animation", "Open synthetic_animated.aem.", "Explorer/GameAssets", "The animated synthetic AEM is active."),
            new("keys", "Inspect keyframes", "Select the Animation tab, curve, and key values.", "Aem/Animation", "A key is selected."),
            new("scrub", "Scrub and play", "Move the timeline, play, pause, and reset.", "Aem/Timeline", "Animation playback has been reviewed."),
            new("export", "Export animation", "Export glTF and inspect the animation status report.", "Document/Export", "Animated glTF export completes."),
        ]);

    public static readonly TutorialDefinition StructuredData = new(
        "structured-language",
        "Structured language data",
        "Edit a synthetic language table with exact reparse validation.",
        [
            new("workspace", "Open the sample workspace", "Open the CC0 Public Synthetic Demo workspace.", "File/OpenWorkspace", "The sample workspace is active."),
            new("open", "Open the language table", "Open Assets/Data/synthetic.lang.", "Explorer/ModWorkspace", "The Language Table document is active."),
            new("edit", "Edit an entry", "Select an entry, change its value, and apply it.", "Language/Entry", "A replace-entry operation exists."),
            new("undo", "Undo and redo", "Use Ctrl+Z and Ctrl+Y to verify the operation history.", "Edit/UndoRedo", "The edit is restored."),
            new("export", "Save a validated copy", "Save Copy reparses every output entry before the atomic write.", "Language/Export", "A validated copy is written."),
        ]);

    public static IReadOnlyList<TutorialDefinition> All { get; } =
        [QuickInspect, TextureMod, ModelImport, Animation, StructuredData];

    public static TutorialDefinition Resolve(string id) => All.FirstOrDefault(
        tutorial => string.Equals(tutorial.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown tutorial '{id}'.");
}

public sealed class TutorialSession
{
    public TutorialDefinition? ActiveTutorial { get; private set; }

    public int StepIndex { get; private set; }

    public bool IsActive => ActiveTutorial is not null;

    public TutorialStep? CurrentStep => ActiveTutorial is null
        ? null
        : ActiveTutorial.Steps[StepIndex];

    public bool CanGoBack => IsActive && StepIndex > 0;

    public bool IsLastStep => IsActive && StepIndex == ActiveTutorial!.Steps.Count - 1;

    public void Start(TutorialDefinition tutorial, int restoredStep = 0)
    {
        ArgumentNullException.ThrowIfNull(tutorial);
        if (tutorial.Steps.Count == 0)
        {
            throw new ArgumentException("A tutorial must contain at least one step.", nameof(tutorial));
        }

        ActiveTutorial = tutorial;
        StepIndex = Math.Clamp(restoredStep, 0, tutorial.Steps.Count - 1);
    }

    public bool Next()
    {
        if (!IsActive || IsLastStep)
        {
            return false;
        }

        StepIndex++;
        return true;
    }

    public bool Back()
    {
        if (!CanGoBack)
        {
            return false;
        }

        StepIndex--;
        return true;
    }

    public void Restart()
    {
        if (IsActive)
        {
            StepIndex = 0;
        }
    }

    public void Stop()
    {
        ActiveTutorial = null;
        StepIndex = 0;
    }
}
