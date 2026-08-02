using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Gof2Workshop.App.Documents;

namespace Gof2Workshop.App.Views;

public sealed partial class AemAuthoringDocumentView : UserControl
{
    private string? draggedStableId;

    public AemAuthoringDocumentView()
    {
        InitializeComponent();
    }

    private void OnSceneSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        _ = args;
        if (sender is ListBox list && DataContext is AemAuthoringDocumentViewModel viewModel)
        {
            viewModel.SetSubmeshSelection(
                list.SelectedItems?.OfType<AemAuthoringSubmeshRow>() ?? []);
        }
    }

    private void OnScenePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not ListBox list ||
            !args.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ListBoxItem? item = args.Source as ListBoxItem ??
            (args.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>();
        draggedStableId = (item?.DataContext as AemAuthoringSubmeshRow)?.StableId;
    }

    private void OnScenePointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        _ = sender;
        ListBoxItem? item = args.Source as ListBoxItem ??
            (args.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>();
        if (draggedStableId is not null &&
            item?.DataContext is AemAuthoringSubmeshRow target &&
            !draggedStableId.Equals(target.StableId, StringComparison.Ordinal) &&
            DataContext is AemAuthoringDocumentViewModel viewModel)
        {
            viewModel.MoveSubmesh(draggedStableId, target.Index);
            args.Handled = true;
        }
        draggedStableId = null;
    }
}
