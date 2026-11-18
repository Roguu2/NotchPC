using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchPC.Core.Layout;
using NotchPC.Core.Localization;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Music;

/// <summary>
/// Floating lyrics card below the notch (ref/Notch.jpg). Stays open while the notch collapses, closed with X.
/// Its visibility is driven by <see cref="Notch.NotchWindow"/>.
/// </summary>
public partial class LyricsWindow : Window
{
    public const double CardWidth = 380;
    public const double CardHeight = 330;

    private readonly MusicViewModel _vm;
    private IntPtr _hwnd;
    private ScreenRect? _pendingRect;

    internal LyricsWindow(MusicViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Width = CardWidth;
        Height = CardHeight;
        _vm.PropertyChanged += OnViewModelChanged;
        Loc.CultureChanged += (_, _) => UpdateState();
        Body.SizeChanged += (_, e) =>
        {
            FadeMask.EndPoint = new Point(0, e.NewSize.Height);
            ScrollToCurrent(animate: false);
        };
        UpdateState();
        UpdateGlow();
    }

    /// <summary>The user dragged the card somewhere (it has already moved).</summary>
    public event EventHandler? MovedByUser;

    /// <summary>The user double-clicked the header: put the card back under the notch.</summary>
    public event EventHandler? ResetRequested;

    /// <summary>Current window rectangle in physical pixels (null before it was shown).</summary>
    public ScreenRect? Bounds => _hwnd == IntPtr.Zero ? null : WindowHelper.GetBounds(_hwnd);

    /// <summary>Places the card (physical pixels) horizontally centred under the notch.</summary>
    public void PlaceBelowNotch(MonitorDescriptor monitor, double topDip, double notchOffsetDip = 0)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var x = (((monitor.Bounds.Width / monitor.Scale) - CardWidth) / 2) + notchOffsetDip;
        Place(NotchLayout.PlaceCard(monitor, x, topDip, CardWidth, CardHeight));
    }

    /// <summary>Places the card where the user left it (DIP offset from the monitor's top-left).</summary>
    public void PlaceAt(MonitorDescriptor monitor, double xDip, double yDip) =>
        Place(NotchLayout.PlaceCard(monitor, xDip, yDip, CardWidth, CardHeight));

    private void Place(ScreenRect rect)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _pendingRect = rect;
            return;
        }

        if (WindowHelper.GetBounds(_hwnd) != rect)
        {
            WindowHelper.PlaceTopmost(_hwnd, rect);
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        var before = Bounds;
        try
        {
            DragMove(); // returns when the button is released
        }
        catch (InvalidOperationException)
        {
            return; // button already released
        }

        if (Bounds != before)
        {
            MovedByUser?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.MakeOverlay(_hwnd, noActivate: true);
        if (_pendingRect is { } r)
        {
            WindowHelper.PlaceTopmost(_hwnd, r);
        }

        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => ScrollToCurrent(animate: false), DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MusicViewModel.LyricsState):
                UpdateState();
                // Containers for the new lines are generated on the next layout pass.
                Dispatcher.BeginInvoke(() => ScrollToCurrent(animate: false), DispatcherPriority.Loaded);
                break;
            case nameof(MusicViewModel.CurrentLine):
                ScrollToCurrent(animate: true);
                break;
            case nameof(MusicViewModel.Accent):
                UpdateGlow();
                break;
        }
    }

    private void UpdateState()
    {
        var state = _vm.LyricsState;
        LinesCanvas.Visibility = state == LyricsState.Synced ? Visibility.Visible : Visibility.Collapsed;
        PlainScroll.Visibility = state == LyricsState.Plain ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = state is LyricsState.Synced or LyricsState.Plain ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = state == LyricsState.Error ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = state is LyricsState.Synced or LyricsState.Plain ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = state switch
        {
            LyricsState.Loading => Loc.Get("Lyrics.Loading"),
            LyricsState.NotFound => Loc.Get("Lyrics.NotFound"),
            LyricsState.Instrumental => Loc.Get("Lyrics.Instrumental"),
            LyricsState.Error => Loc.Get("Lyrics.Error"),
            LyricsState.NoMedia => Loc.Get("Music.NothingPlaying"),
            _ => "",
        };

        if (state == LyricsState.Plain)
        {
            PlainScroll.ScrollToTop();
        }
    }

    private void UpdateGlow()
    {
        if (_vm.Accent is not { } c)
        {
            Glow.Background = null;
            return;
        }

        Glow.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0x70, c.R, c.G, c.B), 0),
                new(Color.FromArgb(0x18, c.R, c.G, c.B), 0.55),
                new(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
            },
            new Point(0, 0),
            new Point(0.4, 1));
    }

    /// <summary>Slides the line list so the current line sits about a third from the top of the body.</summary>
    private void ScrollToCurrent(bool animate)
    {
        if (_vm.LyricsState != LyricsState.Synced || Lines.Items.Count == 0)
        {
            LinesShift.BeginAnimation(TranslateTransform.YProperty, null);
            LinesShift.Y = 0;
            return;
        }

        var anchor = Body.ActualHeight * 0.38;
        double target;
        var index = _vm.CurrentLine;
        if (index < 0 || Lines.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item)
        {
            target = anchor; // before the first line: first line waits at the anchor
        }
        else
        {
            var top = item.TranslatePoint(new Point(0, 0), Lines).Y;
            target = anchor - top - (item.ActualHeight / 2);
        }

        if (!animate || !IsVisible)
        {
            LinesShift.BeginAnimation(TranslateTransform.YProperty, null);
            LinesShift.Y = target;
            return;
        }

        var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(a, 60);
        LinesShift.BeginAnimation(TranslateTransform.YProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnClose(object sender, RoutedEventArgs e) => _vm.LyricsOpen = false;

    private async void OnRetry(object sender, RoutedEventArgs e) => await _vm.RetryLyricsAsync();
}
