using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NotchPC.App.Shelf;

/// <summary>
/// Shelf tiles: drag a tile (or "drag all") into Explorer, an e-mail, a chat… to take the files out;
/// double-click opens. Drag-out only offers copy/link, so a file is never moved away by accident.
/// </summary>
public partial class ShelfPanel : UserControl
{
    private Point? _downAt;

    public ShelfPanel()
    {
        InitializeComponent();
    }

    private ShelfViewModel? Vm => DataContext as ShelfViewModel;

    private static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as ShelfItem;

    private void OnTileDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ItemOf(sender) is { } item)
        {
            ShelfViewModel.Open(item);
            _downAt = null;
            e.Handled = true;
            return;
        }

        _downAt = e.GetPosition(this);
    }

    private void OnTileUp(object sender, MouseButtonEventArgs e) => _downAt = null;

    private void OnTileMove(object sender, MouseEventArgs e)
    {
        if (ItemOf(sender) is { Exists: true } item && StartedDragging(e))
        {
            DragOut(sender as DependencyObject, [item.Path]);
        }
    }

    private void OnDragAllMove(object sender, MouseEventArgs e)
    {
        if (Vm is { } vm && StartedDragging(e))
        {
            DragOut(sender as DependencyObject, vm.Items.Where(i => i.Exists).Select(i => i.Path).ToArray());
        }
    }

    private bool StartedDragging(MouseEventArgs e)
    {
        if (_downAt is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return false;
        }

        var d = e.GetPosition(this) - start;
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void DragOut(DependencyObject? source, string[] paths)
    {
        _downAt = null;
        if (source is null || paths.Length == 0 || Vm is not { } vm)
        {
            return;
        }

        var list = new StringCollection();
        list.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(list);
        vm.IsDraggingOut = true;
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally
        {
            vm.IsDraggingOut = false;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Vm?.Remove(item);
        }
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ShelfViewModel.ShowInFolder(item);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.Clear();
}
