using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchPC.App.Controls;

/// <summary>
/// Drag and drop to reorder the items of an ItemsControl (tiles, tabs). Pressing an item and moving a few pixels
/// starts the drag (a plain click still works); a bar shows where it lands; dropping calls <c>move(dragged, target,
/// after)</c>. Items of other types (e.g. an "Add" tile) are neither dragged nor targets. Several lists of the same
/// item type can exchange items (the notch tab bar and its "more" panel).
/// </summary>
internal sealed class DragReorder<T>
    where T : class
{
    private const double Threshold = 6;
    private static readonly string Format = "NotchPC.Reorder." + typeof(T).FullName;

    private readonly ItemsControl _list;
    private readonly Func<T, bool> _canMove;
    private readonly Action<T, T, bool> _move;
    private readonly Func<T, int>? _indexOf;
    private Point? _start;
    private T? _pressed;
    private InsertAdorner? _adorner;

    public DragReorder(ItemsControl list, Func<T, bool> canMove, Action<T, T, bool> move, Func<T, int>? indexOf = null)
    {
        _list = list;
        _canMove = canMove;
        _move = move;
        _indexOf = indexOf;
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += OnDown;
        list.PreviewMouseMove += OnMove;
        list.PreviewMouseLeftButtonUp += (_, _) => _start = null;
        list.DragOver += OnDragOver;
        list.DragLeave += OnDragLeave;
        list.Drop += OnDrop;
    }

    /// <summary>A drag started / ended (the notch stays open meanwhile).</summary>
    public event EventHandler? Started;

    public event EventHandler? Ended;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = ItemAt(e.OriginalSource as DependencyObject, out _);
        _start = _pressed is not null && _canMove(_pressed) ? e.GetPosition(_list) : null;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } start || _pressed is not { } item || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if ((e.GetPosition(_list) - start).Length < Threshold)
        {
            return;
        }

        _start = null;
        var container = _list.ItemContainerGenerator.ContainerFromItem(item) as UIElement;
        Started?.Invoke(this, EventArgs.Empty);
        if (container is not null)
        {
            container.Opacity = 0.45;
        }

        try
        {
            DragDrop.DoDragDrop(container ?? _list, new DataObject(Format, item), DragDropEffects.Move);
        }
        finally
        {
            if (container is not null)
            {
                container.Opacity = 1;
            }

            RemoveAdorner();
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(Format) is not T dragged)
        {
            return; // files and other drags go on to the window
        }

        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        if (Target(e, dragged) is { } t)
        {
            ShowAdorner(t.Container, t.After);
        }
        else
        {
            RemoveAdorner();
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(Format))
        {
            e.Handled = true;
            RemoveAdorner();
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(Format) is not T dragged)
        {
            return;
        }

        e.Handled = true;
        RemoveAdorner();
        if (Target(e, dragged) is { } t)
        {
            _move(dragged, t.Item, t.After);
        }
    }

    private (T Item, UIElement Container, bool After)? Target(DragEventArgs e, T dragged)
    {
        var target = ItemAt(e.OriginalSource as DependencyObject, out var container);
        if (target is null || container is null || ReferenceEquals(target, dragged) || !_canMove(target))
        {
            return null;
        }

        var from = _indexOf?.Invoke(dragged) ?? _list.Items.IndexOf(dragged);
        var to = _indexOf?.Invoke(target) ?? _list.Items.IndexOf(target);
        return (target, container, from >= 0 && from < to);
    }

    private T? ItemAt(DependencyObject? source, out UIElement? container)
    {
        container = null;
        while (source is not null and not Visual)
        {
            source = LogicalTreeHelper.GetParent(source);
        }

        if (source is null || ItemsControl.ContainerFromElement(_list, source) is not UIElement c)
        {
            return null;
        }

        container = c;
        return _list.ItemContainerGenerator.ItemFromContainer(c) as T;
    }

    private void ShowAdorner(UIElement container, bool after)
    {
        if (_adorner is { } a && ReferenceEquals(a.AdornedElement, container) && a.After == after)
        {
            return;
        }

        RemoveAdorner();
        if (AdornerLayer.GetAdornerLayer(container) is { } layer)
        {
            _adorner = new InsertAdorner(container, after);
            layer.Add(_adorner);
        }
    }

    private void RemoveAdorner()
    {
        if (_adorner is { } a)
        {
            AdornerLayer.GetAdornerLayer(a.AdornedElement)?.Remove(a);
            _adorner = null;
        }
    }

    /// <summary>A short accent bar at the left or right edge of the item: where the dragged item lands.</summary>
    private sealed class InsertAdorner : Adorner
    {
        public InsertAdorner(UIElement adorned, bool after)
            : base(adorned)
        {
            After = after;
            IsHitTestVisible = false;
        }

        public bool After { get; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            var brush = Application.Current?.TryFindResource("NotchAccent") as Brush ?? Brushes.White;
            var x = After ? size.Width - 1.5 : -1.5;
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(x, size.Height * 0.15, 3, size.Height * 0.7), 1.5, 1.5);
        }
    }
}
