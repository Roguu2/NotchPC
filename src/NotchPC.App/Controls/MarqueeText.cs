using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NotchPC.App.Controls;

/// <summary>
/// "Title · Artist" on one line. When it does not fit and <see cref="IsActive"/> is true, it waits a moment,
/// slowly scrolls to the end, pauses, comes back — three times — and then rests at the start
/// (restarted by a new track or when it becomes active again). Edges fade only while the text overflows.
/// </summary>
public sealed class MarqueeText : FrameworkElement
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MarqueeText), new PropertyMetadata("", (d, _) => ((MarqueeText)d).OnTextChanged()));

    public static readonly DependencyProperty ArtistProperty = DependencyProperty.Register(
        nameof(Artist), typeof(string), typeof(MarqueeText), new PropertyMetadata("", (d, _) => ((MarqueeText)d).OnTextChanged()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MarqueeText), new PropertyMetadata(false, (d, _) => ((MarqueeText)d).Restart()));

    private const double Speed = 30;       // DIP per second, forward
    private const double ReturnSpeed = 60; // DIP per second, back to the start
    private const double FirstPause = 2.5; // seconds before scrolling
    private const double EndPause = 1.8;
    private const double Fade = 10;

    private readonly Canvas _clip = new() { ClipToBounds = true };
    private readonly TextBlock _text = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Run _title = new() { FontWeight = FontWeights.SemiBold };
    private readonly Run _separator = new("  ·  ");
    private readonly Run _artist = new();
    private readonly TranslateTransform _shift = new();
    private readonly LinearGradientBrush _mask;
    private double _overflow;

    public MarqueeText()
    {
        _separator.Foreground = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255));
        _artist.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
        _text.Inlines.Add(_title);
        _text.Inlines.Add(_separator);
        _text.Inlines.Add(_artist);
        _text.RenderTransform = _shift;
        _clip.Children.Add(_text);
        AddVisualChild(_clip);

        // Fade masks sit on this element (parent of the clipping canvas), in absolute units.
        _mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0) };
        IsVisibleChanged += (_, _) => Restart();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Artist
    {
        get => (string)GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }

    /// <summary>Allowed to scroll (pill on screen, mini-player shown, setting on).</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _clip;

    protected override Size MeasureOverride(Size availableSize)
    {
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = double.IsInfinity(availableSize.Width) ? _text.DesiredSize.Width : availableSize.Width;
        return new Size(width, _text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _clip.Arrange(new Rect(finalSize));
        Canvas.SetTop(_text, (finalSize.Height - _text.DesiredSize.Height) / 2);
        _text.Arrange(new Rect(new Point(0, (finalSize.Height - _text.DesiredSize.Height) / 2), _text.DesiredSize));

        var overflow = Math.Max(0, _text.DesiredSize.Width - finalSize.Width);
        if (Math.Abs(overflow - _overflow) > 0.5)
        {
            _overflow = overflow;
            UpdateMask(finalSize.Width);
            Dispatcher.BeginInvoke(Restart, System.Windows.Threading.DispatcherPriority.Background);
        }

        return finalSize;
    }

    private void OnTextChanged()
    {
        _title.Text = Title ?? "";
        _artist.Text = Artist ?? "";
        _separator.Text = string.IsNullOrEmpty(Artist) ? "" : "  ·  ";
        InvalidateMeasure();
        Restart(); // new track: play the intro scroll again
    }

    private void UpdateMask(double width)
    {
        if (_overflow <= 0 || width <= 2 * Fade)
        {
            OpacityMask = null;
            return;
        }

        _mask.EndPoint = new Point(width, 0);
        var f = Fade / width;
        _mask.GradientStops = new GradientStopCollection
        {
            new(Color.FromArgb(0x00, 0, 0, 0), 0),
            new(Color.FromArgb(0xFF, 0, 0, 0), f),
            new(Color.FromArgb(0xFF, 0, 0, 0), 1 - f),
            new(Color.FromArgb(0x00, 0, 0, 0), 1),
        };
        OpacityMask = _mask;
    }

    private void Restart()
    {
        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;
        if (!IsActive || !IsVisible || _overflow <= 0.5)
        {
            return;
        }

        var d = _overflow + 6;
        var t1 = FirstPause;
        var t2 = t1 + (d / Speed);
        var t3 = t2 + EndPause;
        var t4 = t3 + (d / ReturnSpeed);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(t4),
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t1))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-d, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t2)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-d, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t3))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t4)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        Timeline.SetDesiredFrameRate(animation, 30);
        animation.Completed += (_, _) =>
        {
            // Rest at the start; drop the clock so nothing keeps ticking.
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = 0;
        };
        _shift.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
