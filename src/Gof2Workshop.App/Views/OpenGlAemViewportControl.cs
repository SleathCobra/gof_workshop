using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Gof2Workshop.App.Documents;
using Gof2Workshop.App.Rendering;

namespace Gof2Workshop.App.Views;

public sealed class OpenGlAemViewportControl : OpenGlControlBase
{
    public static readonly StyledProperty<AemDocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<OpenGlAemViewportControl, AemDocumentViewModel?>(nameof(Document));

    private OpenGlSceneRenderer? renderer;
    private Point previousPoint;
    private Point pressPoint;
    private bool dragging;
    private bool panning;
    private long lastMetricsUpdate;

    public OpenGlAemViewportControl()
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

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            renderer = new OpenGlSceneRenderer(gl, GlVersion);
            SceneViewportRendererInfo info = renderer.Info;
            PostToDocument(document => document.ReportGpuRendererReady(info));
            RequestNextFrameRendering();
        }
        catch (Exception exception)
        {
            renderer = null;
            PostToDocument(document => document.ReportGpuRendererFailure(exception.Message));
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        _ = gl;
        OpenGlSceneRenderer? currentRenderer = renderer;
        AemDocumentViewModel? document = Document;
        if (currentRenderer is null || document is null || document.UseSoftwareRenderer)
        {
            return;
        }

        try
        {
            double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            int width = Math.Clamp(
                (int)Math.Round(Math.Max(Bounds.Width, 1) * renderScaling),
                1,
                16_384);
            int height = Math.Clamp(
                (int)Math.Round(Math.Max(Bounds.Height, 1) * renderScaling),
                1,
                16_384);
            SceneViewportFrameMetrics metrics = currentRenderer.Render(
                document.CreateViewportRequest(),
                fb,
                width,
                height);
            long now = Environment.TickCount64;
            if (now - lastMetricsUpdate >= 250)
            {
                lastMetricsUpdate = now;
                PostToDocument(value => value.ReportGpuFrame(metrics, width, height));
            }

            if (document.IsPlaying)
            {
                RequestNextFrameRendering();
            }
        }
        catch (Exception exception)
        {
            PostToDocument(documentValue =>
                documentValue.ReportGpuRendererFailure(exception.Message));
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _ = gl;
        renderer?.DisposeCurrentContext();
        renderer = null;
        PostToDocument(document => document.ReportGpuRendererReleased());
    }

    protected override void OnOpenGlLost()
    {
        renderer = null;
        PostToDocument(document =>
            document.ReportGpuRendererFailure("The OpenGL context was lost."));
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
                RequestNextFrameRendering();
            }
        }

        base.OnPropertyChanged(change);
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
        panning = point.Properties.IsRightButtonPressed ||
            point.Properties.IsMiddleButtonPressed;
        previousPoint = point.Position;
        pressPoint = point.Position;
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
        Avalonia.Vector delta = current - previousPoint;
        previousPoint = current;
        if (panning)
        {
            Document.Pan(delta.X, delta.Y, Bounds.Width, Bounds.Height);
        }
        else
        {
            Document.Orbit(delta.X, delta.Y);
        }

        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Point released = e.GetPosition(this);
        bool wasClick = !panning &&
            Math.Abs(released.X - pressPoint.X) <= 4 &&
            Math.Abs(released.Y - pressPoint.Y) <= 4;
        dragging = false;
        e.Pointer.Capture(null);
        if (wasClick && Document is not null)
        {
            Document.PickSubmesh(
                released.X,
                released.Y,
                Bounds.Width,
                Bounds.Height);
        }

        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Document?.Zoom(e.Delta.Y);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Document is not null)
        {
            NotifyViewportSize(Document);
        }

        RequestNextFrameRendering();
    }

    private void NotifyViewportSize(AemDocumentViewModel document)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        document.ResizeViewport(Bounds.Width, Bounds.Height, scaling);
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is not nameof(AemDocumentViewModel.PreviewBitmap))
        {
            RequestNextFrameRendering();
        }
    }

    private void PostToDocument(Action<AemDocumentViewModel> action)
    {
        AemDocumentViewModel? document = Document;
        if (document is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(Document, document))
                {
                    action(document);
                }
            },
            DispatcherPriority.Background);
    }
}
