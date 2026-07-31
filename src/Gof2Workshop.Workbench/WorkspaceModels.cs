using System.Text.Json.Serialization;

namespace Gof2Workshop.Workbench;

public sealed class WorkspaceDefinition
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string Name { get; set; } = "Untitled Mod";

    public string ModId { get; set; } = "local.untitled-mod";

    public string Author { get; set; } = string.Empty;

    public string ModVersion { get; set; } = "0.1.0";

    public string ProfileId { get; set; } = "gof2-pc-1x";

    public string? GameAssetRoot { get; set; }

    public string ModRoot { get; set; } = ".";

    public string OutputRoot { get; set; } = "Generated";

    public List<WorkspaceDocumentState> OpenDocuments { get; set; } = [];

    public string? ActiveDocumentPath { get; set; }

    public WorkbenchLayoutState Layout { get; set; } = new();

    public AssetFilterState AssetFilter { get; set; } = new();

    public List<string> RecentAssets { get; set; } = [];

    public Dictionary<string, string> MaterialOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string? FilePath { get; set; }
}

public sealed record WorkspaceDocumentState(string AssetPath, string DocumentKind);

public sealed class WorkbenchLayoutState
{
    public double ExplorerWidth { get; set; } = 300;

    public double InspectorWidth { get; set; } = 300;

    public double BottomHeight { get; set; } = 220;

    public bool ExplorerVisible { get; set; } = true;

    public bool InspectorVisible { get; set; } = true;

    public bool BottomVisible { get; set; } = true;

    public bool ExplorerFloating { get; set; }

    public bool InspectorFloating { get; set; }

    public bool BottomFloating { get; set; }

    public string ActiveActivity { get; set; } = "Explorer";

    public string ActiveBottomTab { get; set; } = "Output";

    public void Normalize()
    {
        ExplorerWidth = ClampFinite(ExplorerWidth, 220, 700, 300);
        InspectorWidth = ClampFinite(InspectorWidth, 220, 700, 300);
        BottomHeight = ClampFinite(BottomHeight, 120, 600, 220);
        ActiveActivity = string.IsNullOrWhiteSpace(ActiveActivity) ? "Explorer" : ActiveActivity;
        ActiveBottomTab = string.IsNullOrWhiteSpace(ActiveBottomTab) ? "Output" : ActiveBottomTab;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}

public sealed class AssetFilterState
{
    public string SearchText { get; set; } = string.Empty;

    public string Kind { get; set; } = "All";

    public string Support { get; set; } = "All";

    public string? Format { get; set; }
}

public sealed class ApplicationState
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public List<string> RecentWorkspaces { get; set; } = [];

    public List<string> RecentStandaloneFiles { get; set; } = [];

    public string? LastWorkspace { get; set; }

    public WindowPlacementState Window { get; set; } = new();

    public string Theme { get; set; } = "System";

    public Dictionary<string, int> TutorialProgress { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WindowPlacementState
{
    public double Width { get; set; } = 1500;

    public double Height { get; set; } = 920;

    public double? X { get; set; }

    public double? Y { get; set; }

    public bool Maximized { get; set; }

    public void Normalize()
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 900, 6000) : 1500;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 600, 4000) : 920;
        if (X is not null && !double.IsFinite(X.Value))
        {
            X = null;
        }

        if (Y is not null && !double.IsFinite(Y.Value))
        {
            Y = null;
        }
    }
}
