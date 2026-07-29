using Avalonia;

namespace Gof2Workshop.App.Views;

public sealed class PaneFloatRequestedEventArgs(PixelPoint? screenPosition) : EventArgs
{
    public PixelPoint? ScreenPosition { get; } = screenPosition;
}
