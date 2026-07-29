using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Gof2Workshop.App.Documents;

namespace Gof2Workshop.App.Views;

public sealed partial class AemDocumentView : UserControl
{
    private Point previousPoint;
    private Point pressPoint;
    private bool dragging;
    private bool panning;

    public AemDocumentView()
    {
        AvaloniaXamlLoader.Load(this);
        Border inputSurface = this.FindControl<Border>("ViewportInputSurface")
            ?? throw new InvalidOperationException("The AEM viewport input surface is missing.");
        inputSurface.PointerPressed += OnViewportPointerPressed;
        inputSurface.PointerMoved += OnViewportPointerMoved;
        inputSurface.PointerReleased += OnViewportPointerReleased;
        inputSurface.PointerWheelChanged += OnViewportPointerWheelChanged;
        inputSurface.PointerCaptureLost += OnViewportPointerCaptureLost;
    }

    private AemDocumentViewModel? Document => DataContext as AemDocumentViewModel;

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Border surface)
        {
            return;
        }

        PointerPoint point = eventArgs.GetCurrentPoint(surface);
        if (!point.Properties.IsLeftButtonPressed &&
            !point.Properties.IsRightButtonPressed &&
            !point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        surface.Focus();
        dragging = true;
        panning = point.Properties.IsRightButtonPressed ||
            point.Properties.IsMiddleButtonPressed;
        previousPoint = point.Position;
        pressPoint = point.Position;
        eventArgs.Pointer.Capture(surface);
        eventArgs.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!dragging || sender is not Border surface || Document is not { } document)
        {
            return;
        }

        Point current = eventArgs.GetPosition(surface);
        Vector delta = current - previousPoint;
        previousPoint = current;
        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        if (panning)
        {
            document.Pan(delta.X, delta.Y, surface.Bounds.Width, surface.Bounds.Height);
        }
        else
        {
            document.Orbit(delta.X, delta.Y);
        }

        eventArgs.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (sender is not Border surface)
        {
            return;
        }

        Point released = eventArgs.GetPosition(surface);
        bool wasClick = dragging &&
            !panning &&
            Math.Abs(released.X - pressPoint.X) <= 4 &&
            Math.Abs(released.Y - pressPoint.Y) <= 4;
        dragging = false;
        eventArgs.Pointer.Capture(null);
        if (wasClick && Document is { } document)
        {
            document.PickSubmesh(
                released.X,
                released.Y,
                surface.Bounds.Width,
                surface.Bounds.Height);
        }

        eventArgs.Handled = true;
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        Document?.Zoom(eventArgs.Delta.Y);
        eventArgs.Handled = true;
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        dragging = false;
    }
}
