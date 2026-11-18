using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchPC.Core.Layout;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Widgets;

/// <summary>
/// Dotted grid shown over the desktop (above the wallpaper, below the widgets) only while a widget is being moved
/// or resized in grid mode. Click-through; one static tiled brush, so it costs nothing once drawn.
/// </summary>
internal sealed class GridOverlayWindow : Window
{
    private readonly DesktopPinner _pinner;
    private readonly Rectangle _dots = new();
    private readonly Rectangle _frame = new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
        StrokeThickness = 1,
        StrokeDashArray = [4, 4],
        RadiusX = 10,
        RadiusY = 10,
    };

    private IntPtr _hwnd;
    private string? _monitor;

    public GridOverlayWindow(DesktopPinner pinner)
    {
        _pinner = pinner;
        Title = "NotchPC widget grid";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        Content = new System.Windows.Controls.Grid { Children = { _dots, _frame } };
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Shows the grid over the work area of <paramref name="monitor"/> (no-op if already there).</summary>
    public void ShowFor(MonitorDescriptor monitor, GridSpec grid)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(grid);
        var work = monitor.WorkArea;
        var widthDip = work.Width / monitor.Scale;
        var heightDip = work.Height / monitor.Scale;

        // One dot per grid intersection, starting at the margin.
        var dot = new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
            null,
            new EllipseGeometry(new Point(0, 0), 1.3, 1.3));
        var brush = new DrawingBrush(new DrawingGroup { Children = { new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, grid.Cell, grid.Cell))), dot } })
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(grid.Margin - (grid.Cell / 2), grid.Margin - (grid.Cell / 2), grid.Cell, grid.Cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(-grid.Cell / 2, -grid.Cell / 2, grid.Cell, grid.Cell),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        _dots.Fill = brush;
        _frame.Margin = new Thickness(grid.Margin - 6);

        Width = widthDip;
        Height = heightDip;
        if (!IsVisible)
        {
            Show();
        }

        if (_hwnd != IntPtr.Zero && (_monitor != monitor.DeviceName || WindowHelper.GetBounds(_hwnd) != work))
        {
            WindowHelper.Place(_hwnd, work);
        }

        _monitor = monitor.DeviceName;
        _pinner.BringToFrontOfPinned(_hwnd);
    }

    public void HideOverlay()
    {
        _monitor = null;
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            _pinner.HandleMessage(h, msg, lp);
            return IntPtr.Zero;
        });
        _pinner.Register(_hwnd);
        WindowHelper.MakeClickThrough(_hwnd);
    }
}
