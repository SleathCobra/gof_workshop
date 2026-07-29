using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Gof2Workshop.App.Views;

public sealed partial class UnsupportedDocumentView : UserControl
{
    public UnsupportedDocumentView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
