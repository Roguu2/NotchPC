using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NotchPC.Core.Games;
using NotchPC.Core.Layout;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Games;

/// <summary>
/// The small stats bar over a game: FPS, CPU, GPU, RAM, clock. Click-through, never takes focus, hidden from
/// Alt+Tab. Built in code (it has no interaction, just a row of labels).
/// </summary>
internal sealed class GameOverlayWindow : Window
{
    private const double MarginDip = 14;

    private static readonly Brush Good = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush Fair = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly Brush Low = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush Value = Frozen(Color.FromRgb(0xF5, 0xF5, 0xF7));
    private static readonly Brush Label = Frozen(Color.FromArgb(0x99, 0xF5, 0xF5, 0xF7));
    private static readonly Brush Separator = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    private readonly TextBlock _fps;
    private readonly TextBlock _cpu;
    private readonly TextBlock _gpu;
    private readonly TextBlock _ram;
    private readonly TextBlock _clock;
    private readonly FrameworkElement _clockPart;
    private readonly StackPanel _stats;
    private readonly TextBlock _message;
    private IntPtr _hwnd;
    private MonitorDescriptor? _monitor;
    private string _position = OverlayPositions.TopLeft;

    public GameOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "NotchPC overlay";
        if (Application.Current?.TryFindResource("UiFont") is FontFamily font)
        {
            FontFamily = font;
        }

        _fps = Number(34, FontWeights.Bold);
        _cpu = Number(64);
        _gpu = Number(64);
        _ram = Number(34);
        _clock = Number(38);
        _clockPart = Part(null, _clock);
        _stats = new StackPanel { Orientation = Orientation.Horizontal };
        _stats.Children.Add(Part("FPS", _fps));
        _stats.Children.Add(Bar());
        _stats.Children.Add(Part("CPU", _cpu));
        _stats.Children.Add(Bar());
        _stats.Children.Add(Part("GPU", _gpu));
        _stats.Children.Add(Bar());
        _stats.Children.Add(Part("RAM", _ram));
        _stats.Children.Add(Bar());
        _stats.Children.Add(_clockPart);
        _message = new TextBlock { Foreground = Value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 0, 2, 0) };

        var row = new Grid();
        row.Children.Add(_stats);
        row.Children.Add(_message);
        Content = new Border
        {
            Background = Frozen(Color.FromArgb(0xD9, 0x0E, 0x0E, 0x10)),
            BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 5, 12, 5),
            Child = row,
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowHelper.MakeOverlay(_hwnd, noActivate: true);
            WindowHelper.MakeClickThrough(_hwnd);
        };
        SizeChanged += (_, _) => Reposition();
    }

    public void SetClockVisible(bool visible)
    {
        _clockPart.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ((UIElement)_stats.Children[^2]).Visibility = _clockPart.Visibility; // its separator
    }

    public void ShowStats(double? fps, string cpu, string gpu, string ram, string clock)
    {
        _message.Visibility = Visibility.Collapsed;
        _stats.Visibility = Visibility.Visible;
        _fps.Text = OverlayFormat.Fps(fps);
        _fps.Foreground = OverlayFormat.Level(fps) switch
        {
            FpsLevel.Good => Good,
            FpsLevel.Fair => Fair,
            FpsLevel.Low => Low,
            _ => Value,
        };
        _cpu.Text = cpu;
        _gpu.Text = gpu;
        _ram.Text = ram;
        _clock.Text = clock;
    }

    /// <summary>One line instead of the stats (e.g. after the shortcut hid the overlay for this game).</summary>
    public void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        _stats.Visibility = Visibility.Collapsed;
    }

    /// <summary>Puts the bar into a corner of the monitor (physical pixels) and keeps it above the game.</summary>
    public void Place(MonitorDescriptor monitor, string position)
    {
        _monitor = monitor;
        _position = position;
        Reposition();
    }

    /// <summary>Topmost again: a game that is topmost itself may have been raised above us.</summary>
    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowHelper.BringToTopmost(_hwnd);
        }
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _monitor is not { } m || ActualWidth <= 0)
        {
            return;
        }

        var width = (int)Math.Ceiling(ActualWidth * m.Scale);
        var height = (int)Math.Ceiling(ActualHeight * m.Scale);
        var (x, y) = OverlayPositions.Place(_position, m.Bounds, width, height, (int)(MarginDip * m.Scale));
        WindowHelper.PlaceTopmost(_hwnd, ScreenRect.FromSize(x, y, width, height));
    }

    private static TextBlock Number(double minWidth, FontWeight? weight = null) => new()
    {
        Foreground = Value,
        FontSize = 13,
        FontWeight = weight ?? FontWeights.SemiBold,
        MinWidth = minWidth,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel Part(string? label, TextBlock value)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (label is not null)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = Label, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        panel.Children.Add(value);
        return panel;
    }

    private static Border Bar() => new() { Width = 1, Height = 14, Background = Separator, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
