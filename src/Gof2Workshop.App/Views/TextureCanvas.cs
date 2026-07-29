using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Gof2Workshop.App.Documents;
using Gof2Workshop.Formats.Aei;

namespace Gof2Workshop.App.Views;

public sealed class TextureCanvas : Control
{
    public static readonly StyledProperty<AeiDocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<TextureCanvas, AeiDocumentViewModel?>(nameof(Document));

    private Point pan;
    private Point pointerStart;
    private Point panStart;
    private bool pointerDown;
    private bool dragging;
    private double zoom = 1;
    private bool fitMode = true;

    public TextureCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public AeiDocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public void FitToView()
    {
        fitMode = true;
        zoom = 1;
        pan = default;
        InvalidateVisual();
    }

    public void ActualSize()
    {
        AeiDocumentViewModel? document = Document;
        if (document?.CurrentImage is null)
        {
            return;
        }

        fitMode = false;
        zoom = 1;
        pan = default;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        AeiDocumentViewModel? document = Document;
        if (document?.PreviewBitmap is null || document.CurrentImage is null)
        {
            DrawEmptyState(context, document?.DecodeStatus ?? "No texture loaded");
            return;
        }

        Rect destination = CalculateDestination(
            document.CurrentImage.Width,
            document.CurrentImage.Height);
        if (document.ShowCheckerboard)
        {
            DrawCheckerboard(context, destination);
        }

        context.DrawImage(
            document.PreviewBitmap,
            new Rect(0, 0, document.CurrentImage.Width, document.CurrentImage.Height),
            destination);
        if (document.ShowRegions &&
            document.CurrentImage.Width == document.File.Width &&
            document.CurrentImage.Height == document.File.Height)
        {
            DrawRegions(context, document, destination);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == DocumentProperty)
        {
            if (change.OldValue is AeiDocumentViewModel oldDocument)
            {
                oldDocument.PropertyChanged -= OnDocumentPropertyChanged;
            }

            if (change.NewValue is AeiDocumentViewModel newDocument)
            {
                newDocument.PropertyChanged += OnDocumentPropertyChanged;
            }

            FitToView();
        }

        base.OnPropertyChanged(change);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        pointerDown = true;
        dragging = false;
        pointerStart = point.Position;
        panStart = pan;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!pointerDown)
        {
            return;
        }

        Point current = e.GetPosition(this);
        Vector delta = current - pointerStart;
        if (dragging || Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
        {
            dragging = true;
            pan = panStart + delta;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pointerDown && !dragging)
        {
            SelectRegion(e.GetPosition(this));
        }

        pointerDown = false;
        dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        AeiDocumentViewModel? document = Document;
        if (document?.CurrentImage is null)
        {
            return;
        }

        Point pointer = e.GetPosition(this);
        Rect before = CalculateDestination(document.CurrentImage.Width, document.CurrentImage.Height);
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        zoom = Math.Clamp(zoom * factor, 0.05, 32);
        Rect after = CalculateDestination(document.CurrentImage.Width, document.CurrentImage.Height);
        if (before.Width > 0 && before.Height > 0)
        {
            double imageX = (pointer.X - before.X) / before.Width;
            double imageY = (pointer.Y - before.Y) / before.Height;
            pan += new Vector(
                pointer.X - (after.X + (imageX * after.Width)),
                pointer.Y - (after.Y + (imageY * after.Height)));
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is
            nameof(AeiDocumentViewModel.PreviewBitmap) or
            nameof(AeiDocumentViewModel.ShowCheckerboard) or
            nameof(AeiDocumentViewModel.ShowRegions) or
            nameof(AeiDocumentViewModel.ShowLabels) or
            nameof(AeiDocumentViewModel.SelectedRegion))
        {
            InvalidateVisual();
        }
    }

    private Rect CalculateDestination(int imageWidth, int imageHeight)
    {
        double baseScale = fitMode
            ? Math.Min(
                Math.Max(Bounds.Width - 24, 1) / imageWidth,
                Math.Max(Bounds.Height - 24, 1) / imageHeight)
            : 1;
        double scale = baseScale * zoom;
        double width = imageWidth * scale;
        double height = imageHeight * scale;
        return new Rect(
            ((Bounds.Width - width) * 0.5) + pan.X,
            ((Bounds.Height - height) * 0.5) + pan.Y,
            width,
            height);
    }

    private void DrawRegions(
        DrawingContext context,
        AeiDocumentViewModel document,
        Rect destination)
    {
        double scaleX = destination.Width / document.File.Width;
        double scaleY = destination.Height / document.File.Height;
        foreach (AeiRegion region in document.Regions)
        {
            Rect rectangle = new(
                destination.X + (region.X * scaleX),
                destination.Y + (region.Y * scaleY),
                region.Width * scaleX,
                region.Height * scaleY);
            bool selected = ReferenceEquals(region, document.SelectedRegion) ||
                region.Index == document.SelectedRegion?.Index;
            IPen pen = new Pen(
                selected ? Brushes.Gold : Brushes.DeepSkyBlue,
                selected ? 3 : 1);
            context.DrawRectangle(null, pen, rectangle);
            if (document.ShowLabels && rectangle.Width >= 18 && rectangle.Height >= 12)
            {
                FormattedText label = new(
                    region.Index.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    10,
                    selected ? Brushes.Gold : Brushes.White);
                Rect labelBackground = new(
                    rectangle.X,
                    rectangle.Y,
                    label.Width + 5,
                    label.Height + 2);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 5, 8, 12)), null, labelBackground);
                context.DrawText(label, new Point(rectangle.X + 2, rectangle.Y + 1));
            }
        }
    }

    private void SelectRegion(Point point)
    {
        AeiDocumentViewModel? document = Document;
        if (document?.CurrentImage is null ||
            document.CurrentImage.Width != document.File.Width ||
            document.CurrentImage.Height != document.File.Height)
        {
            return;
        }

        Rect destination = CalculateDestination(document.File.Width, document.File.Height);
        if (!destination.Contains(point))
        {
            document.SelectedRegion = null;
            return;
        }

        double imageX = (point.X - destination.X) * document.File.Width / destination.Width;
        double imageY = (point.Y - destination.Y) * document.File.Height / destination.Height;
        document.SelectedRegion = document.Regions.LastOrDefault(
            region =>
                imageX >= region.X &&
                imageY >= region.Y &&
                imageX < region.X + region.Width &&
                imageY < region.Y + region.Height);
    }

    private static void DrawCheckerboard(DrawingContext context, Rect destination)
    {
        const double tile = 12;
        int startX = (int)Math.Floor(destination.X / tile);
        int endX = (int)Math.Ceiling(destination.Right / tile);
        int startY = (int)Math.Floor(destination.Y / tile);
        int endY = (int)Math.Ceiling(destination.Bottom / tile);
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                IBrush brush = ((x + y) & 1) == 0
                    ? new SolidColorBrush(Color.FromRgb(47, 51, 58))
                    : new SolidColorBrush(Color.FromRgb(67, 72, 81));
                Rect tileRect = new Rect(x * tile, y * tile, tile, tile).Intersect(destination);
                context.FillRectangle(brush, tileRect);
            }
        }
    }

    private void DrawEmptyState(DrawingContext context, string message)
    {
        FormattedText text = new(
            message,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            14,
            Brushes.LightGray);
        context.DrawText(
            text,
            new Point(
                Math.Max(12, (Bounds.Width - text.Width) * 0.5),
                Math.Max(12, (Bounds.Height - text.Height) * 0.5)));
    }
}
