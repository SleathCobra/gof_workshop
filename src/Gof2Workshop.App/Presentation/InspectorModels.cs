namespace Gof2Workshop.App.Presentation;

public sealed record InspectorProperty(string Name, string Value, string? ToolTip = null);

public sealed record InspectorGroup(
    string Name,
    IReadOnlyList<InspectorProperty> Properties,
    bool IsAdvanced = false)
{
    public bool IsInitiallyExpanded => !IsAdvanced;
}

public interface IInspectorSource
{
    public event EventHandler? InspectorChanged;

    public IReadOnlyList<InspectorGroup> InspectorGroups { get; }

    public string AssetDetails { get; }
}
