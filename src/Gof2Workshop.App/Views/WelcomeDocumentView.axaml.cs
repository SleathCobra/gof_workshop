using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Gof2Workshop.App.Views;

public sealed partial class WelcomeDocumentView : UserControl
{
    public WelcomeDocumentView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
