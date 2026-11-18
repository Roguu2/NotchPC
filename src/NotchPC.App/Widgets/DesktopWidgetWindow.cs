using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchPC.Core.Layout;
using NotchPC.Core.Localization;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Widgets;

/// <summary>What the widget windows ask their owner while the user moves or resizes them.</summary>
internal interface IWidgetLayoutHost
{
    bool IsLocked { get; }

    /// <summary>Final rectangle (physical px) for a proposed move: snapped in grid mode, kept on screen.</summary>
    WidgetPlacement ConstrainMove(DesktopWidgetWindow window, ScreenRect proposed);

    /// <summary>Final rectangle for a proposed resize (top-left fixed): clamped and snapped in grid mode.</summary>
    WidgetPlacement ConstrainResize(DesktopWidgetWindow window, ScreenRect proposed);

    void InteractionStarted(DesktopWidgetWindow window);

    void InteractionEnded(DesktopWidgetWindow window);
}

/// <summary>Where a widget goes: physical rectangle plus its size in DIPs (for content scaling).</summary>
internal readonly record struct WidgetPlacement(ScreenRect Rect, double WidthDip, double HeightDip);

/// <summary>
/// Transparent, borderless window hosting one widget card on the desktop (below all normal windows, see
/// <see cref="DesktopPinner"/>). Drag the card to move it, drag the corner grip to resize it. A taller card
/// does not scale its content: each widget fills the extra room with more data.
/// </summary>
internal sealed class DesktopWidgetWindow : Window
{
    public const double NaturalWidth = 230;
    private const double CardPadding = 14;
    private const double MoveThreshold = 4;

    private readonly DesktopPinner _pinner;
    private readonly IWidgetLayoutHost _host;
    private readonly Border _card;
    private readonly Grid _scaler;
    private readonly Border _grip;
    private IntPtr _hwnd;
    private WidgetPlacement? _pending;
    private bool _onScreen;

    private Point? _pressAt;
    private bool _pressOnGrip;
    private Interaction _interaction;
    private (int X, int Y) _startCursor;
    private ScreenRect _startRect;

    public DesktopWidgetWindow(string id, FrameworkElement content, WidgetViewModel viewModel, DesktopPinner pinner, IWidgetLayoutHost host)
    {
        Id = id;
        ViewModel = viewModel;
        _pinner = pinner;
        _host = host;

        Title = "NotchPC widget " + id;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)FindResource("UiFont");

        content.DataContext = viewModel;
        _scaler = new Grid
        {
            Margin = new Thickness(0),
            Children = { new Border { Padding = new Thickness(CardPadding, 12, CardPadding, 12), Child = content } },
        };
        NaturalHeight = MeasureNaturalHeight(content);

        _grip = BuildGrip();
        _card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResource("WidgetBackground"),
            BorderBrush = (Brush)FindResource("WidgetBorder"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new Grid { Children = { _scaler, _grip } },
        };
        Content = _card;
        ApplySize(NaturalWidth, NaturalHeight);

        viewModel.RecheckOnScreen = () => CheckOnScreen(sessionLocked: false);
        MouseEnter += (_, _) => UpdateGrip();
        MouseLeave += (_, _) => UpdateGrip();
        UpdateHints();
        Loc.CultureChanged += (_, _) => UpdateHints();
    }

    private enum Interaction
    {
        None,
        Move,
        Resize,
    }

    public string Id { get; }

    public WidgetViewModel ViewModel { get; }

    /// <summary>Height of the card at its natural width (DIPs); the content scale is height / natural height.</summary>
    public double NaturalHeight { get; }

    public double WidthDip { get; private set; } = NaturalWidth;

    public double HeightDip { get; private set; }

    public ScreenRect? Bounds => _hwnd == IntPtr.Zero ? null : WindowHelper.GetBounds(_hwnd);

    public bool IsInteracting => _interaction != Interaction.None;

    public void Place(WidgetPlacement placement)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _pending = placement;
            return;
        }

        ApplySize(placement.WidthDip, placement.HeightDip);
        if (WindowHelper.GetBounds(_hwnd) != placement.Rect)
        {
            WindowHelper.Place(_hwnd, placement.Rect);
        }
    }

    /// <summary>Re-evaluates whether the card can be seen and tells the view model. Returns the new state.</summary>
    public bool CheckOnScreen(bool sessionLocked)
    {
        var on = !sessionLocked && _hwnd != IntPtr.Zero && IsVisible && DesktopPinner.IsUncovered(_hwnd);
        if (on != _onScreen)
        {
            _onScreen = on;
            ViewModel.SetOnScreen(on);
        }

        return on;
    }

    public void UpdateHints()
    {
        ToolTip = _host.IsLocked ? null : Loc.Get("Widget.DragHint.Full");
        UpdateGrip();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _pinner.Register(_hwnd);
        if (_pending is { } p)
        {
            _pending = null;
            Place(p);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || _host.IsLocked)
        {
            return; // a button or the timer dial took the click
        }

        // Start moving/resizing only after a small drag, so clicks and double-clicks keep working.
        _pressAt = e.GetPosition(this);
        _pressOnGrip = IsOverGrip(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_interaction == Interaction.None)
        {
            if (_pressAt is not { } press || e.LeftButton != MouseButtonState.Pressed)
            {
                _pressAt = null;
                return;
            }

            if ((e.GetPosition(this) - press).Length < MoveThreshold)
            {
                return;
            }

            if (Bounds is not { } start)
            {
                return;
            }

            _interaction = _pressOnGrip ? Interaction.Resize : Interaction.Move;
            _startRect = start;
            _startCursor = WindowHelper.CursorPosition();
            CaptureMouse();
            _host.InteractionStarted(this);
        }

        var (cx, cy) = WindowHelper.CursorPosition();
        var dx = cx - _startCursor.X;
        var dy = cy - _startCursor.Y;
        var placement = _interaction == Interaction.Move
            ? _host.ConstrainMove(this, ScreenRect.FromSize(_startRect.Left + dx, _startRect.Top + dy, _startRect.Width, _startRect.Height))
            : _host.ConstrainResize(this, ScreenRect.FromSize(_startRect.Left, _startRect.Top, Math.Max(1, _startRect.Width + dx), Math.Max(1, _startRect.Height + dy)));
        Place(placement);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _pressAt = null;
        if (_interaction != Interaction.None)
        {
            ReleaseMouseCapture(); // ends the interaction in OnLostMouseCapture
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_interaction == Interaction.None)
        {
            return;
        }

        _interaction = Interaction.None;
        _host.InteractionEnded(this);
        UpdateGrip();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pinner.Unregister(_hwnd);
        ViewModel.SetOnScreen(false);
        ViewModel.RecheckOnScreen = null;
        base.OnClosed(e);
    }

    private static double MeasureNaturalHeight(FrameworkElement content)
    {
        // Content height at the natural card width, plus vertical padding (12 + 12) and the 1 px border.
        content.Measure(new Size(NaturalWidth - (2 * CardPadding) - 2, double.PositiveInfinity));
        return Math.Ceiling(content.DesiredSize.Height + 24 + 2);
    }

    /// <summary>
    /// Sizes the card. The content is not scaled: it gets the whole area and shows more data when there is room
    /// (forecast, agenda, charts …), see the individual widgets.
    /// </summary>
    private void ApplySize(double widthDip, double heightDip)
    {
        WidthDip = widthDip;
        HeightDip = heightDip;
        _scaler.Width = widthDip;
        _scaler.Height = heightDip;
        _card.Width = widthDip;
        _card.Height = heightDip;
        Width = widthDip;
        Height = heightDip;
    }

    private static Border BuildGrip()
    {
        // Three dots in the bottom-right corner; shown on hover when the widget can be resized.
        var dots = new Canvas { Width = 12, Height = 12 };
        foreach (var (x, y) in new[] { (9.0, 9.0), (5.0, 9.0), (9.0, 5.0) })
        {
            var dot = new Ellipse { Width = 2.4, Height = 2.4, Fill = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)) };
            Canvas.SetLeft(dot, x - 1.2);
            Canvas.SetTop(dot, y - 1.2);
            dots.Children.Add(dot);
        }

        return new Border
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNWSE,
            Padding = new Thickness(6, 6, 4, 4),
            Child = dots,
            Visibility = Visibility.Hidden,
        };
    }

    private bool IsOverGrip(MouseEventArgs e)
    {
        var p = e.GetPosition(_grip);
        return p.X >= 0 && p.Y >= 0 && p.X <= _grip.ActualWidth && p.Y <= _grip.ActualHeight;
    }

    private void UpdateGrip() =>
        _grip.Visibility = !_host.IsLocked && (IsMouseOver || _interaction == Interaction.Resize) ? Visibility.Visible : Visibility.Hidden;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _pinner.HandleMessage(hwnd, msg, lParam);
        return IntPtr.Zero;
    }
}
