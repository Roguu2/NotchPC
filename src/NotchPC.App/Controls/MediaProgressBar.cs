using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaClock = NotchPC.Core.Music.MediaClock;

namespace NotchPC.App.Controls;

/// <summary>
/// Rounded playback progress bar. At rest: a thin track with a white fill. On hover it thickens, shows a knob,
/// a lighter preview up to the cursor and a time bubble. Click/drag seeks, the mouse wheel jumps ±5 s.
/// Drawn in OnRender with a transparent background, so the whole control height is clickable.
/// Nothing animates unless the mouse is on it.
/// </summary>
public sealed class MediaProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MediaProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CanSeekProperty = DependencyProperty.Register(
        nameof(CanSeek), typeof(bool), typeof(MediaProgressBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MediaProgressBar)d).UpdateCursor()));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(TimeSpan), typeof(MediaProgressBar),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty HoverAmountProperty = DependencyProperty.Register(
        "HoverAmount", typeof(double), typeof(MediaProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double RestThickness = 4;
    private const double HoverThickness = 7;
    private const double KnobRadius = 6.5;
    private const double WheelStepSeconds = 5;

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)));
    private static readonly Brush PreviewBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)));
    private static readonly Brush FillBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 255, 255, 255)));
    private static readonly Brush KnobShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)));
    private static readonly Brush BubbleBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x2A)));
    private static readonly Pen BubblePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), 1));
    private static readonly Typeface BubbleFont = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private double _hoverX = double.NaN;
    private int _hoverGeneration;
    private double _dragProgress = -1;

    public MediaProgressBar()
    {
        UpdateCursor();
    }

    /// <summary>User picked a point (0..1) by clicking or dragging.</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>User scrolled the wheel: jump by this many seconds (negative = back).</summary>
    public event EventHandler<double>? SeekByRequested;

    /// <summary>0..1.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool CanSeek
    {
        get => (bool)GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    /// <summary>Track length, used for the time shown in the bubble.</summary>
    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    private double HoverAmount => (double)GetValue(HoverAmountProperty);

    private bool IsDragging => _dragProgress >= 0;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, 18);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0)
        {
            return;
        }

        // Whole area hit-testable (the bar itself is only a few pixels tall).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var hover = HoverAmount;
        var thickness = RestThickness + ((HoverThickness - RestThickness) * hover);
        var radius = thickness / 2;
        var top = (h - thickness) / 2;
        var progress = Math.Clamp(IsDragging ? _dragProgress : Progress, 0, 1);
        var fillX = w * progress;

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, top, w, thickness), radius, radius);

        if (!double.IsNaN(_hoverX) && !IsDragging && _hoverX > fillX)
        {
            dc.PushOpacity(hover);
            dc.DrawRoundedRectangle(PreviewBrush, null, new Rect(0, top, _hoverX, thickness), radius, radius);
            dc.Pop();
        }

        if (fillX > 0.5)
        {
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, top, Math.Max(fillX, thickness), thickness), radius, radius);
        }

        if (hover > 0.01)
        {
            var knob = KnobRadius * hover;
            var center = new Point(Math.Clamp(fillX, knob, w - knob), h / 2);
            dc.DrawEllipse(KnobShadowBrush, null, center, knob + 1.5, knob + 1.5);
            dc.DrawEllipse(Brushes.White, null, center, knob, knob);
        }

        var bubbleX = IsDragging ? fillX : _hoverX;
        if (Duration > TimeSpan.Zero && CanSeek && !double.IsNaN(bubbleX) && hover > 0.5)
        {
            DrawBubble(dc, bubbleX, w, TimeSpan.FromTicks((long)(Duration.Ticks * Math.Clamp(bubbleX / w, 0, 1))));
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (CanSeek)
        {
            AnimateHover(1);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = double.NaN;
        if (!IsDragging)
        {
            AnimateHover(0);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!CanSeek || ActualWidth <= 0)
        {
            return;
        }

        var x = Math.Clamp(e.GetPosition(this).X, 0, ActualWidth);
        _hoverX = x;
        if (IsMouseCaptured)
        {
            _dragProgress = x / ActualWidth;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CanSeek || ActualWidth <= 0)
        {
            return;
        }

        CaptureMouse();
        _dragProgress = Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        var target = _dragProgress;
        ReleaseMouseCapture(); // clears the drag state in OnLostMouseCapture
        if (target >= 0)
        {
            SetCurrentValue(ProgressProperty, target);
            SeekRequested?.Invoke(this, target);
        }

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragProgress = -1;
        if (!IsMouseOver)
        {
            AnimateHover(0);
        }

        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!CanSeek)
        {
            return;
        }

        SeekByRequested?.Invoke(this, e.Delta > 0 ? WheelStepSeconds : -WheelStepSeconds);
        e.Handled = true;
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void DrawBubble(DrawingContext dc, double x, double width, TimeSpan time)
    {
        var text = new FormattedText(
            MediaClock.Format(time),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            BubbleFont,
            11,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        const double padX = 7, padY = 3, gap = 4;
        var bw = text.Width + (2 * padX);
        var bh = text.Height + (2 * padY);
        var left = Math.Clamp(x - (bw / 2), 0, Math.Max(0, width - bw));
        var rect = new Rect(left, -bh - gap + 2, bw, bh);
        dc.DrawRoundedRectangle(BubbleBrush, BubblePen, rect, bh / 2, bh / 2);
        dc.DrawText(text, new Point(rect.Left + padX, rect.Top + padY));
    }

    private void AnimateHover(double to)
    {
        var generation = ++_hoverGeneration;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        a.Completed += (_, _) =>
        {
            if (generation != _hoverGeneration)
            {
                return; // superseded (mouse went in and out quickly)
            }

            // Drop the finished clock so nothing stays on the render loop.
            BeginAnimation(HoverAmountProperty, null);
            SetValue(HoverAmountProperty, to);
        };
        BeginAnimation(HoverAmountProperty, a);
    }

    private void UpdateCursor() => Cursor = CanSeek ? Cursors.Hand : Cursors.Arrow;
}
