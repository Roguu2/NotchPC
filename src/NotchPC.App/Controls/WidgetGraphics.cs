using System.Windows;
using System.Windows.Media;
using NotchPC.Core.Widgets;

namespace NotchPC.App.Controls;

/// <summary>Circular progress (focus timer dial): grey track, white arc from 12 o'clock clockwise.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var t = Thickness;
        var radius = (size - t) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(TrackBrush, t), center, radius, radius);

        var p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.001)
        {
            return;
        }

        var pen = new Pen(Brushes.White, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = (p * 360) - 90;
        var rad = angle * Math.PI / 180;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Cos(rad)), center.Y + (radius * Math.Sin(rad)));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, p > 0.5, SweepDirection.Clockwise, true));
        drawingContext.DrawGeometry(null, pen, new PathGeometry([figure]));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Thin rounded meter for the stats widget (CPU/GPU/RAM).</summary>
public sealed class MiniBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MiniBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(MiniBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)));

    /// <summary>0..100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 : availableSize.Width, 4);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = ActualWidth;
        var h = Math.Min(4, ActualHeight);
        var top = (ActualHeight - h) / 2;
        drawingContext.DrawRoundedRectangle(Track, null, new Rect(0, top, w, h), h / 2, h / 2);
        var fill = w * Math.Clamp(Value / 100, 0, 1);
        if (fill > 0.5)
        {
            drawingContext.DrawRoundedRectangle(Fill, null, new Rect(0, top, Math.Max(fill, h), h), h / 2, h / 2);
        }
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Line chart of the last minute (0..100 %, newest on the right) for the stats widget.</summary>
public sealed class SparkChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(System.Collections.IEnumerable), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Changed on every new sample (the series lists are updated in place).</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(SparkChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int Points = 60;
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)), 1) { DashStyle = DashStyles.Dot });

    /// <summary>Items with a Brush and a list of values (see the stats view model).</summary>
    public System.Collections.IEnumerable? Series
    {
        get => (System.Collections.IEnumerable?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        foreach (var level in new[] { 0.25, 0.5, 0.75 })
        {
            var y = Math.Round(h * (1 - level)) + 0.5;
            drawingContext.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        if (Series is null)
        {
            return;
        }

        var step = w / (Points - 1);
        foreach (var item in Series)
        {
            var brush = (Brush?)item.GetType().GetProperty("Brush")?.GetValue(item);
            if (item.GetType().GetProperty("Values")?.GetValue(item) is not IReadOnlyList<double> values || values.Count < 2 || brush is null)
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var start = Points - values.Count;
                for (var i = 0; i < values.Count; i++)
                {
                    var p = new Point((start + i) * step, h - (Math.Clamp(values[i], 0, 100) / 100 * (h - 2)) - 1);
                    if (i == 0)
                    {
                        ctx.BeginFigure(p, isFilled: false, isClosed: false);
                    }
                    else
                    {
                        ctx.LineTo(p, isStroked: true, isSmoothJoin: true);
                    }
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    private static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }
}

/// <summary>White line weather icons (24×24 design grid), drawn so they match the notch icon style.</summary>
public sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(WeatherKind), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(WeatherKind.Clear, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(
        nameof(IsDay), typeof(bool), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private const string Sun = "M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 Z M12,2.5 L12,4.5 M12,19.5 L12,21.5 M2.5,12 L4.5,12 M19.5,12 L21.5,12 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M5.3,18.7 L6.7,17.3 M17.3,6.7 L18.7,5.3";
    private const string Moon = "M19.5,14.8 A8,8 0 1 1 9.2,4.5 A6.3,6.3 0 0 0 19.5,14.8 Z";
    private const string Cloud = "M7,18.5 L17.5,18.5 A4,4 0 0 0 17.2,10.5 A5.5,5.5 0 0 0 6.6,11.7 A3.4,3.4 0 0 0 7,18.5 Z";
    private const string CloudHigh = "M7,14 L17.5,14 A4,4 0 0 0 17.2,6 A5.5,5.5 0 0 0 6.6,7.2 A3.4,3.4 0 0 0 7,14 Z";
    private const string SmallSun = "M10.5,8 A3,3 0 1 1 4.5,8 A3,3 0 1 1 10.5,8 Z M7.5,2.2 L7.5,3.4 M2,8 L3.2,8 M3.6,4.1 L4.4,4.9 M11.4,4.1 L10.6,4.9";
    private const string CloudRight = "M9.5,19.5 L18.5,19.5 A3.5,3.5 0 0 0 18.2,12.5 A4.8,4.8 0 0 0 9.1,13.5 A3,3 0 0 0 9.5,19.5 Z";
    private const string RainLines = "M8.5,17 L7.5,20.5 M12.5,17 L11.5,20.5 M16.5,17 L15.5,20.5";
    private const string DrizzleDots = "M8.5,18 L8.3,18.8 M12.5,19 L12.3,19.8 M16.5,18 L16.3,18.8";
    private const string SnowDots = "M8.5,17.5 L8.5,17.6 M12.5,20 L12.5,20.1 M16.5,17.5 L16.5,17.6 M10.5,21 L10.5,21.1 M14.5,21 L14.5,21.1";
    private const string Bolt = "M13,15 L10.8,18.5 L13.4,18.5 L11.2,22";
    private const string FogLines = "M4,9 L20,9 M3,13 L21,13 M5,17 L19,17";

    private static readonly Dictionary<string, Geometry> Cache = [];

    public WeatherKind Kind
    {
        get => (WeatherKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsDay
    {
        get => (bool)GetValue(IsDayProperty);
        set => SetValue(IsDayProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var scale = size / 24;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Brushes.White, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var part in PartsFor(Kind, IsDay))
        {
            drawingContext.DrawGeometry(null, pen, Get(part));
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static string[] PartsFor(WeatherKind kind, bool day) => kind switch
    {
        WeatherKind.Clear => [day ? Sun : Moon],
        WeatherKind.PartlyCloudy => day ? [SmallSun, CloudRight] : [Cloud],
        WeatherKind.Cloudy => [Cloud],
        WeatherKind.Fog => [FogLines],
        WeatherKind.Drizzle => [CloudHigh, DrizzleDots],
        WeatherKind.Rain or WeatherKind.Showers or WeatherKind.FreezingRain => [CloudHigh, RainLines],
        WeatherKind.Snow or WeatherKind.SnowShowers => [CloudHigh, SnowDots],
        WeatherKind.Thunderstorm => [CloudHigh, Bolt],
        _ => [Cloud],
    };

    private static Geometry Get(string data)
    {
        if (!Cache.TryGetValue(data, out var g))
        {
            g = Geometry.Parse(data);
            g.Freeze();
            Cache[data] = g;
        }

        return g;
    }
}
