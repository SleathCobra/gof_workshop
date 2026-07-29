using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Gof2Workshop.App.Views;

public sealed partial class InspectorPaneView : UserControl
{
    private bool dockDragPending;

    public InspectorPaneView()
    {
        AvaloniaXamlLoader.Load(this);
        Button floatHandle = this.FindControl<Button>("FloatHandle")
            ?? throw new InvalidOperationException("Inspector float handle is missing.");
        floatHandle.AddHandler(
            InputElement.PointerPressedEvent,
            OnDockDragPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        floatHandle.AddHandler(
            InputElement.PointerMovedEvent,
            OnDockDragMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        floatHandle.AddHandler(
            InputElement.PointerReleasedEvent,
            OnDockDragReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    public event EventHandler<PaneFloatRequestedEventArgs>? FloatRequested;

    private void OnDockDragPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window and not MainWindow)
        {
            eventArgs.Handled = true;
            window.BeginMoveDrag(eventArgs);
            return;
        }

        dockDragPending = true;
        eventArgs.Handled = true;
        eventArgs.Pointer.Capture(sender as Control);
    }

    private void OnDockDragMoved(object? sender, PointerEventArgs eventArgs)
    {
        _ = sender;
        if (dockDragPending)
        {
            eventArgs.Handled = true;
        }
    }

    private void OnDockDragReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        _ = sender;
        if (!dockDragPending)
        {
            return;
        }

        dockDragPending = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
        Avalonia.PixelPoint? screenPosition = TopLevel.GetTopLevel(this) is Window window
            ? window.PointToScreen(eventArgs.GetPosition(window))
            : null;
        FloatRequested?.Invoke(
            this,
            new PaneFloatRequestedEventArgs(screenPosition));
    }
}
