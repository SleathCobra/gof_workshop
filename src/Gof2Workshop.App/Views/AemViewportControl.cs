using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Gof2Workshop.App.Documents;

namespace Gof2Workshop.App.Views;

public sealed class AemViewportControl : Control
{
    public static readonly StyledProperty<AemDocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<AemViewportControl, AemDocumentViewModel?>(nameof(Document));

    private Point previousPoint;
    private bool dragging;
    private bool panning;

    public AemViewportControl()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += OnViewportSizeChanged;
    }

    public AemDocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        AemDocumentViewModel? document = Document;
        if (document?.PreviewBitmap is null)
        {
            context.FillRectangle(Brushes.Black, Bounds);
            return;
        }

        double imageWidth = document.PreviewBitmap.PixelSize.Width;
        double imageHeight = document.PreviewBitmap.PixelSize.Height;
        double scale = Math.Min(Bounds.Width / imageWidth, Bounds.Height / imageHeight);
        double width = imageWidth * scale;
        double height = imageHeight * scale;
        Rect destination = new(
            (Bounds.Width - width) * 0.5,
            (Bounds.Height - height) * 0.5,
            width,
            height);
        context.DrawImage(
            document.PreviewBitmap,
            new Rect(0, 0, imageWidth, imageHeight),
            destination);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == DocumentProperty)
        {
            if (change.OldValue is AemDocumentViewModel oldDocument)
            {
                oldDocument.PropertyChanged -= OnDocumentPropertyChanged;
            }

            if (change.NewValue is AemDocumentViewModel newDocument)
            {
                newDocument.PropertyChanged += OnDocumentPropertyChanged;
                NotifyViewportSize(newDocument);
            }
        }

        base.OnPropertyChanged(change);
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Document is not null)
        {
            NotifyViewportSize(Document);
        }
    }

    private void NotifyViewportSize(AemDocumentViewModel document)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        document.ResizeViewport(Bounds.Width, Bounds.Height, scaling);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed &&
            !point.Properties.IsRightButtonPressed &&
            !point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        Focus();
        dragging = true;
        panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        previousPoint = point.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging || Document is null)
        {
            return;
        }

        Point current = e.GetPosition(this);
        Vector delta = current - previousPoint;
        previousPoint = current;
        if (panning)
        {
            Document.Pan(delta.X, delta.Y, Bounds.Width, Bounds.Height);
        }
        else
        {
            Document.Orbit(delta.X, delta.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Document?.Zoom(e.Delta.Y);
        e.Handled = true;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName == nameof(AemDocumentViewModel.PreviewBitmap))
        {
            InvalidateVisual();
        }
    }
}
