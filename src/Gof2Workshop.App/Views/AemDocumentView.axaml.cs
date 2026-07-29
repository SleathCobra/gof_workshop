using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Gof2Workshop.App.Views;

public sealed partial class AemDocumentView : UserControl
{
    public AemDocumentView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
