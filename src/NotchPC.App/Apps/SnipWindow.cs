using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NotchPC.Core.Layout;
using NotchPC.Core.Localization;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Apps;

/// <summary>
/// Area screenshot: shows a frozen picture of the monitor, the user drags a rectangle, the crop is returned.
/// Esc or right click cancels. Nothing runs while the window is not open.
/// </summary>
internal sealed class SnipWindow : Window
{
    private readonly BitmapSource _screen;
    private readonly MonitorDescriptor _monitor;
    private readonly RectangleGeometry _hole = new();
    private readonly Rectangle _frame;
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText;
    private readonly TaskCompletionSource<BitmapSource?> _result = new();
    private Point? _start;

    public SnipWindow(BitmapSource screen, MonitorDescriptor monitor)
    {
        _screen = screen;
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "NotchPC snip";

        var shade = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, 100000, 100000)), _hole),
            IsHitTestVisible = false,
        };
        _frame = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x11, 0x13)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Child = _sizeText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0B, 0x0D)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = Loc.Get("Apps.Snip.Hint"), Foreground = Brushes.White, FontSize = 13 },
        };

        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(_frame);
        canvas.Children.Add(_sizeLabel);

        var root = new Grid();
        root.Children.Add(new Image { Source = screen, Stretch = Stretch.Fill });
        root.Children.Add(shade);
        root.Children.Add(canvas);
        root.Children.Add(hint);
        Content = root;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Finish(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(null);
            }
        };
        Deactivated += (_, _) =>
        {
            if (_start is null)
            {
                Finish(null); // the user switched away
            }
        };
    }

    /// <summary>Shows the window and waits for the selection (null = cancelled).</summary>
    public Task<BitmapSource?> PickAsync()
    {
        Show();
        Activate();
        Focus();
        return _result.Task;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.PlaceTopmost(hwnd, _monitor.Bounds);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        CaptureMouse();
        Update(_start.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not null)
        {
            Update(e.GetPosition(this));
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        ReleaseMouseCapture();
        var rect = new Rect(start, e.GetPosition(this));
        _start = null;
        var crop = ToPixels(rect);
        Finish(crop.Width >= 4 && crop.Height >= 4 ? Crop(crop) : null);
    }

    private void Update(Point current)
    {
        if (_start is not { } start)
        {
            return;
        }

        var rect = new Rect(start, current);
        _hole.Rect = rect;
        _frame.Visibility = Visibility.Visible;
        _frame.Width = rect.Width;
        _frame.Height = rect.Height;
        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);

        var px = ToPixels(rect);
        _sizeText.Text = $"{px.Width} × {px.Height}";
        _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeLabel, rect.X);
        Canvas.SetTop(_sizeLabel, rect.Y > 30 ? rect.Y - 28 : rect.Bottom + 6);
    }

    private Int32Rect ToPixels(Rect rect)
    {
        var sx = _screen.PixelWidth / Math.Max(1, ActualWidth);
        var sy = _screen.PixelHeight / Math.Max(1, ActualHeight);
        var x = (int)Math.Clamp(Math.Round(rect.X * sx), 0, _screen.PixelWidth);
        var y = (int)Math.Clamp(Math.Round(rect.Y * sy), 0, _screen.PixelHeight);
        var right = (int)Math.Clamp(Math.Round(rect.Right * sx), 0, _screen.PixelWidth);
        var bottom = (int)Math.Clamp(Math.Round(rect.Bottom * sy), 0, _screen.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private CroppedBitmap Crop(Int32Rect rect)
    {
        var cropped = new CroppedBitmap(_screen, rect);
        cropped.Freeze();
        return cropped;
    }

    private void Finish(BitmapSource? result)
    {
        if (_result.TrySetResult(result))
        {
            Close();
        }
    }
}
