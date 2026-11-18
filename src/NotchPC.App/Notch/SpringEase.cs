using System.Windows;
using System.Windows.Media.Animation;

namespace NotchPC.App.Notch;

/// <summary>
/// Damped-spring easing (slight overshoot, then settles) for the island-style expand animation.
/// Normalised so that progress 1 maps exactly to 1.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.72));

    public static readonly DependencyProperty FrequencyProperty =
        DependencyProperty.Register(nameof(Frequency), typeof(double), typeof(SpringEase), new PropertyMetadata(12.0));

    public SpringEase()
    {
        // EaseIn = use EaseInCore as-is (the spring curve already has the "out" shape).
        EasingMode = EasingMode.EaseIn;
    }

    /// <summary>Damping ratio: below 1 overshoots, 1 = critically damped.</summary>
    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    /// <summary>Natural angular frequency over the whole animation (higher = settles sooner).</summary>
    public double Frequency
    {
        get => (double)GetValue(FrequencyProperty);
        set => SetValue(FrequencyProperty, value);
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1)
        {
            return 1;
        }

        return Curve(normalizedTime) / Curve(1);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();

    private double Curve(double t)
    {
        var zeta = Math.Clamp(Damping, 0.05, 1.0);
        var omega = Frequency;
        if (zeta >= 0.999)
        {
            return 1 - (Math.Exp(-omega * t) * (1 + (omega * t)));
        }

        var omegaD = omega * Math.Sqrt(1 - (zeta * zeta));
        var decay = Math.Exp(-zeta * omega * t);
        return 1 - (decay * (Math.Cos(omegaD * t) + (zeta * omega / omegaD * Math.Sin(omegaD * t))));
    }
}
