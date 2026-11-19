using System.Windows;
using System.Windows.Media;

namespace NotchPC.App.Controls;

/// <summary>
/// Clips an element to a rounded rectangle of its own size (a Border's CornerRadius does not clip its content).
/// Usage: controls:RoundClip.Radius="8".
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return (double)d.GetValue(RadiusProperty);
    }

    public static void SetRadius(DependencyObject d, double value)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.SetValue(RadiusProperty, value);
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= Update;
        element.SizeChanged += Update;
        Apply(element);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var r = GetRadius(element);
        element.Clip = element.ActualWidth > 0 && element.ActualHeight > 0
            ? new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r)
            : null;
    }
}
