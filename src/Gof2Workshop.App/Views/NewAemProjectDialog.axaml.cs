using Avalonia.Controls;
using Avalonia.Interactivity;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Import;

namespace Gof2Workshop.App.Views;

public sealed partial class NewAemProjectDialog : Window
{
    public NewAemProjectDialog()
        : this(AemVersion.V4)
    {
    }

    public NewAemProjectDialog(AemVersion initialVersion)
    {
        InitializeComponent();
        VersionBox.SelectedIndex = initialVersion == AemVersion.V5 ? 1 : 0;
        TemplateBox.SelectedIndex = 0;
        string name = $"new_{DateTime.Now:yyyyMMdd_HHmmss}";
        NameBox.Text = name;
        TargetBox.Text = $"assets/main/3d/meshes/{name}.aem";
    }

    private void OnCancel(object? sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        Close(null);
    }

    private void OnCreate(object? sender, RoutedEventArgs args)
    {
        _ = sender;
        _ = args;
        string name = (NameBox.Text ?? string.Empty).Trim();
        string target = (TargetBox.Text ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorText.Text = "Enter a non-empty asset name without invalid filename characters.";
            return;
        }
        if (!target.EndsWith(".aem", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith('/') || target.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            ErrorText.Text = "The target must be a safe relative .aem path inside the mod.";
            return;
        }

        AemVersion version = VersionBox.SelectedIndex == 1 ? AemVersion.V5 : AemVersion.V4;
        string templateName = (TemplateBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? nameof(AemAuthoringTemplate.Empty);
        if (!Enum.TryParse(templateName, out AemAuthoringTemplate template))
        {
            template = AemAuthoringTemplate.Empty;
        }
        Close(new NewAemProjectOptions(name, target, version, template));
    }
}
