using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Gof2Workshop.App.Views;

public sealed partial class AeiDocumentView : UserControl
{
    public AeiDocumentView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnFitClick(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TexturePreview.FitToView();
    }

    private void OnActualSizeClick(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TexturePreview.ActualSize();
    }

    private TextureCanvas TexturePreview => this.FindControl<TextureCanvas>("PreviewCanvas")
        ?? throw new InvalidOperationException("Texture preview control is missing.");
}
