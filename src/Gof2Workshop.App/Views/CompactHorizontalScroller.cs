using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Gof2Workshop.App.Views;

public sealed class CompactHorizontalScroller : ContentControl
{
    public static readonly StyledProperty<double> ScrollStepProperty =
        AvaloniaProperty.Register<CompactHorizontalScroller, double>(
            nameof(ScrollStep),
            140);

    private ScrollViewer? scrollViewer;
    private Button? previousButton;
    private Button? nextButton;

    public double ScrollStep
    {
        get => GetValue(ScrollStepProperty);
        set => SetValue(ScrollStepProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachTemplateParts();
        base.OnApplyTemplate(e);

        scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        previousButton = e.NameScope.Find<Button>("PART_PreviousButton");
        nextButton = e.NameScope.Find<Button>("PART_NextButton");

        if (scrollViewer is not null)
        {
            scrollViewer.ScrollChanged += OnScrollChanged;
        }

        if (previousButton is not null)
        {
            previousButton.Click += OnPreviousClick;
        }

        if (nextButton is not null)
        {
            nextButton.Click += OnNextClick;
        }

        UpdateButtonStates();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (scrollViewer is null)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)
            ? -e.Delta.X
            : -e.Delta.Y;
        if (Math.Abs(delta) > double.Epsilon)
        {
            ScrollBy(delta * ScrollStep);
            e.Handled = true;
        }
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        ScrollBy(-ScrollStep);
        eventArgs.Handled = true;
    }

    private void OnNextClick(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        ScrollBy(ScrollStep);
        eventArgs.Handled = true;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        UpdateButtonStates();
    }

    private void ScrollBy(double delta)
    {
        if (scrollViewer is null)
        {
            return;
        }

        double maximum = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        double target = Math.Clamp(scrollViewer.Offset.X + delta, 0, maximum);
        scrollViewer.Offset = new Vector(target, 0);
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        if (scrollViewer is null)
        {
            if (previousButton is not null)
            {
                previousButton.IsEnabled = false;
            }

            if (nextButton is not null)
            {
                nextButton.IsEnabled = false;
            }

            return;
        }

        double maximum = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        if (previousButton is not null)
        {
            previousButton.IsEnabled = scrollViewer.Offset.X > 0.5;
        }

        if (nextButton is not null)
        {
            nextButton.IsEnabled = scrollViewer.Offset.X < maximum - 0.5;
        }
    }

    private void DetachTemplateParts()
    {
        if (scrollViewer is not null)
        {
            scrollViewer.ScrollChanged -= OnScrollChanged;
        }

        if (previousButton is not null)
        {
            previousButton.Click -= OnPreviousClick;
        }

        if (nextButton is not null)
        {
            nextButton.Click -= OnNextClick;
        }
    }
}
