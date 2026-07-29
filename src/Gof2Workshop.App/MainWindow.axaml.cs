using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.App.Views;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly IReadOnlyList<string> arguments;
    private readonly UserDialogService dialogs = new();
    private readonly WorkbenchViewModel viewModel;
    private bool disposed;
    private bool closingMainWindow;
    private PixelPoint lastPosition;
    private readonly Dictionary<FloatingPane, Window> floatingWindows = [];

    public MainWindow()
        : this([])
    {
    }

    public MainWindow(IReadOnlyList<string> arguments)
    {
        this.arguments = arguments ?? [];
        AvaloniaXamlLoader.Load(this);
        viewModel = new WorkbenchViewModel(dialogs);
        DataContext = viewModel;
        dialogs.Owner = this;
        ExplorerPane.FloatRequested += OnExplorerFloatRequested;
        InspectorPane.FloatRequested += OnInspectorFloatRequested;
        BottomPane.FloatRequested += OnBottomFloatRequested;
        viewModel.LayoutChanged += OnLayoutChanged;
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        PositionChanged += (_, eventArgs) => lastPosition = eventArgs.Point;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            await viewModel.InitializeAsync(arguments);
            ApplyWindowPlacement();
            ApplySavedLayout();
        }
        catch (Exception exception)
        {
            viewModel.StatusMessageForUnhandled(exception);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        closingMainWindow = true;
        DockAllFloatingPanes();
        viewModel.LayoutChanged -= OnLayoutChanged;
        Dispatcher.UIThread.UnhandledException -= OnUnhandledException;
        double windowWidth = Width;
        double windowHeight = Height;
        bool maximized = WindowState == WindowState.Maximized;
        double explorerWidth = MainLayoutGrid.ColumnDefinitions[1].ActualWidth;
        double inspectorWidth = MainLayoutGrid.ColumnDefinitions[5].ActualWidth;
        double bottomHeight = RootLayoutGrid.RowDefinitions[4].ActualHeight;
        Task.Run(
            () => viewModel.PersistAsync(
                windowWidth,
                windowHeight,
                lastPosition.X,
                lastPosition.Y,
                maximized,
                explorerWidth,
                inspectorWidth,
                bottomHeight)).GetAwaiter().GetResult();
        Dispose();
    }

    private void ApplySavedLayout()
    {
        WorkbenchLayoutState layout = viewModel.Workspace?.Layout ?? new WorkbenchLayoutState();
        layout.Normalize();
        MainLayoutGrid.ColumnDefinitions[1].Width =
            viewModel.ExplorerVisible && !viewModel.ExplorerFloating
            ? new GridLength(layout.ExplorerWidth)
            : new GridLength(0);
        MainLayoutGrid.ColumnDefinitions[2].Width =
            viewModel.ExplorerVisible && !viewModel.ExplorerFloating
            ? new GridLength(5)
            : new GridLength(0);
        MainLayoutGrid.ColumnDefinitions[4].Width =
            viewModel.InspectorVisible && !viewModel.InspectorFloating
            ? new GridLength(5)
            : new GridLength(0);
        MainLayoutGrid.ColumnDefinitions[5].Width =
            viewModel.InspectorVisible && !viewModel.InspectorFloating
            ? new GridLength(layout.InspectorWidth)
            : new GridLength(0);
        RootLayoutGrid.RowDefinitions[3].Height =
            viewModel.BottomVisible && !viewModel.BottomFloating
            ? new GridLength(5)
            : new GridLength(0);
        RootLayoutGrid.RowDefinitions[4].Height =
            viewModel.BottomVisible && !viewModel.BottomFloating
            ? new GridLength(layout.BottomHeight)
            : new GridLength(0);
        Dispatcher.UIThread.Post(
            UpdateFloatingPanes,
            DispatcherPriority.Background);
    }

    private void OnLayoutChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (viewModel.Workspace is not null)
        {
            if (MainLayoutGrid.ColumnDefinitions[1].ActualWidth >= 100)
            {
                viewModel.Workspace.Layout.ExplorerWidth =
                    MainLayoutGrid.ColumnDefinitions[1].ActualWidth;
            }

            if (MainLayoutGrid.ColumnDefinitions[5].ActualWidth >= 100)
            {
                viewModel.Workspace.Layout.InspectorWidth =
                    MainLayoutGrid.ColumnDefinitions[5].ActualWidth;
            }

            if (RootLayoutGrid.RowDefinitions[4].ActualHeight >= 100)
            {
                viewModel.Workspace.Layout.BottomHeight =
                    RootLayoutGrid.RowDefinitions[4].ActualHeight;
            }
        }

        ApplySavedLayout();
    }

    private void ApplyWindowPlacement()
    {
        WindowPlacementState placement = viewModel.WindowPlacement;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.X is not null && placement.Y is not null)
        {
            PixelRect? workingArea = Screens.Primary?.WorkingArea;
            int x = (int)Math.Round(placement.X.Value);
            int y = (int)Math.Round(placement.Y.Value);
            if (workingArea is not null)
            {
                x = Math.Clamp(
                    x,
                    workingArea.Value.X,
                    Math.Max(workingArea.Value.X, workingArea.Value.Right - 200));
                y = Math.Clamp(
                    y,
                    workingArea.Value.Y,
                    Math.Max(workingArea.Value.Y, workingArea.Value.Bottom - 100));
            }

            Position = new PixelPoint(x, y);
            lastPosition = Position;
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnUnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        _ = sender;
        viewModel.StatusMessageForUnhandled(eventArgs.Exception);
        ShowControlledError(eventArgs.Exception);
        eventArgs.Handled = true;
    }

    private void ShowControlledError(Exception exception)
    {
        Window dialog = new()
        {
            Title = "Galaxy on Fire 2 Workshop",
            Width = 560,
            Height = 250,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        TextBlock summary = new()
        {
            Text = "The Workshop could not complete that operation.",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        };
        TextBlock details = new()
        {
            Text = exception.Message,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        TextBlock guidance = new()
        {
            Text = "Technical details were added to Output and Problems.",
            Opacity = 0.65,
        };
        Button close = new()
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 90,
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                summary,
                details,
                guidance,
                close,
            },
        };
        _ = dialog.ShowDialog(this);
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { CommandParameter: IDocument document } &&
            viewModel.CloseDocumentCommand.CanExecute(document))
        {
            viewModel.CloseDocumentCommand.Execute(document);
        }

        eventArgs.Handled = true;
    }

    private void OnExitClick(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close();
    }

    private void OnExplorerFloatRequested(object? sender, PaneFloatRequestedEventArgs eventArgs)
    {
        _ = sender;
        FloatPane(FloatingPane.Explorer, eventArgs.ScreenPosition);
    }

    private void OnInspectorFloatRequested(object? sender, PaneFloatRequestedEventArgs eventArgs)
    {
        _ = sender;
        FloatPane(FloatingPane.Inspector, eventArgs.ScreenPosition);
    }

    private void OnBottomFloatRequested(object? sender, PaneFloatRequestedEventArgs eventArgs)
    {
        _ = sender;
        FloatPane(FloatingPane.Bottom, eventArgs.ScreenPosition);
    }

    private void FloatPane(FloatingPane pane, PixelPoint? screenPosition)
    {
        if (floatingWindows.ContainsKey(pane))
        {
            return;
        }

        SetFloatingState(pane, true);
        SetPaneFloating(pane, shouldFloat: true);
        if (screenPosition is PixelPoint pointer &&
            floatingWindows.TryGetValue(pane, out Window? window))
        {
            window.Position = new PixelPoint(pointer.X - 20, pointer.Y - 12);
        }
    }

    private void UpdateFloatingPanes()
    {
        SetPaneFloating(FloatingPane.Explorer, viewModel.ExplorerFloating);
        SetPaneFloating(FloatingPane.Inspector, viewModel.InspectorFloating);
        SetPaneFloating(FloatingPane.Bottom, viewModel.BottomFloating);
    }

    private void SetPaneFloating(FloatingPane pane, bool shouldFloat)
    {
        if (shouldFloat)
        {
            if (floatingWindows.ContainsKey(pane))
            {
                return;
            }

            (string title, double width, double height) = GetPaneParts(pane);
            Window window = new()
            {
                Title = $"Galaxy on Fire 2 Workshop — {title}",
                Width = width,
                Height = height,
                MinWidth = 260,
                MinHeight = 180,
                Content = CreateFloatingPaneView(pane),
                DataContext = viewModel,
            };
            floatingWindows[pane] = window;
            window.Closed += (_, _) => OnFloatingPaneClosed(pane, window);
            window.Show(this);
            return;
        }

        DockFloatingPane(pane);
    }

    private void OnFloatingPaneClosed(FloatingPane pane, Window window)
    {
        if (!floatingWindows.TryGetValue(pane, out Window? tracked) ||
            !ReferenceEquals(tracked, window))
        {
            return;
        }

        floatingWindows.Remove(pane);
        if (!closingMainWindow)
        {
            SetFloatingState(pane, false);
            ApplySavedLayout();
        }
    }

    private void DockFloatingPane(FloatingPane pane)
    {
        if (!floatingWindows.Remove(pane, out Window? window))
        {
            return;
        }

        window.Close();
    }

    private void DockAllFloatingPanes()
    {
        foreach (FloatingPane pane in floatingWindows.Keys.ToArray())
        {
            DockFloatingPane(pane);
        }
    }

    private (string Title, double Width, double Height) GetPaneParts(FloatingPane pane)
    {
        return pane switch
        {
            FloatingPane.Explorer => (
                "Explorer",
                Math.Max(320, viewModel.Workspace?.Layout.ExplorerWidth ?? 320),
                720),
            FloatingPane.Inspector => (
                "Inspector",
                Math.Max(320, viewModel.Workspace?.Layout.InspectorWidth ?? 320),
                720),
            FloatingPane.Bottom => (
                "Output / Problems / Asset Details",
                980,
                Math.Max(300, viewModel.Workspace?.Layout.BottomHeight ?? 300)),
            _ => throw new ArgumentOutOfRangeException(nameof(pane)),
        };
    }

    private static Control CreateFloatingPaneView(FloatingPane pane)
    {
        return pane switch
        {
            FloatingPane.Explorer => new ExplorerPaneView(),
            FloatingPane.Inspector => new InspectorPaneView(),
            FloatingPane.Bottom => new BottomPaneView(),
            _ => throw new ArgumentOutOfRangeException(nameof(pane)),
        };
    }

    private void SetFloatingState(FloatingPane pane, bool value)
    {
        switch (pane)
        {
            case FloatingPane.Explorer:
                viewModel.ExplorerFloating = value;
                break;
            case FloatingPane.Inspector:
                viewModel.InspectorFloating = value;
                break;
            case FloatingPane.Bottom:
                viewModel.BottomFloating = value;
                break;
        }
    }

    private Grid MainLayoutGrid => this.FindControl<Grid>("MainGrid")
        ?? throw new InvalidOperationException("Main workbench grid is missing.");

    private Grid RootLayoutGrid => this.FindControl<Grid>("RootGrid")
        ?? throw new InvalidOperationException("Root workbench grid is missing.");

    private ExplorerPaneView ExplorerPane => this.FindControl<ExplorerPaneView>("ExplorerHost")
        ?? throw new InvalidOperationException("Explorer pane is missing.");

    private InspectorPaneView InspectorPane => this.FindControl<InspectorPaneView>("InspectorHost")
        ?? throw new InvalidOperationException("Inspector pane is missing.");

    private BottomPaneView BottomPane => this.FindControl<BottomPaneView>("BottomHost")
        ?? throw new InvalidOperationException("Bottom pane is missing.");

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        viewModel.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private enum FloatingPane
    {
        Explorer,
        Inspector,
        Bottom,
    }
}
