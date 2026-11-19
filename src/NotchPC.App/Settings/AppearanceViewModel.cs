using System.Windows.Media;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;

namespace NotchPC.App.Settings;

/// <summary>The "Appearance" page: style, accent colour, opacity and corners of the notch and the widgets.</summary>
internal sealed class AppearanceViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public AppearanceViewModel(SettingsService settings)
    {
        _settings = settings;
        Build();
        Loc.CultureChanged += (_, _) =>
        {
            Build();
            Raise(nameof(StyleOptions));
            Raise(nameof(AccentOptions));
            Raise(nameof(CornerOptions));
        };
    }

    public IReadOnlyList<SelectOption> StyleOptions { get; private set; } = [];

    public IReadOnlyList<ColorOption> AccentOptions { get; private set; } = [];

    public IReadOnlyList<SelectOption> CornerOptions { get; private set; } = [];

    public string Style
    {
        get => T.Style;
        set => Update(t => t.Style = ThemeStyles.Normalize(value));
    }

    public string Accent
    {
        get => T.Accent;
        set => Update(t => t.Accent = ThemeAccents.Normalize(value));
    }

    public string Corners
    {
        get => T.Corners;
        set => Update(t => t.Corners = ThemeCorners.Normalize(value));
    }

    public double Opacity
    {
        get => T.Opacity;
        set
        {
            var v = (int)Math.Round(value);
            if (v != T.Opacity)
            {
                Update(t => t.Opacity = Math.Clamp(v, 60, 100));
            }
        }
    }

    public string OpacityText => $"{T.Opacity}%";

    private ThemeSettings T => _settings.Current.Theme;

    public void Reset() => _settings.Update(s => s.Theme = new ThemeSettings());

    private void Update(Action<ThemeSettings> change)
    {
        _settings.Update(s => change(s.Theme));
        foreach (var name in new[] { nameof(Style), nameof(Accent), nameof(Corners), nameof(Opacity), nameof(OpacityText) })
        {
            Raise(name);
        }
    }

    private void Build()
    {
        StyleOptions = ThemeStyles.All.Select(s => new SelectOption(s, Loc.Get("Theme.Style." + s))).ToList();
        CornerOptions = ThemeCorners.All.Select(c => new SelectOption(c, Loc.Get("Theme.Corners." + c))).ToList();
        AccentOptions = ThemeAccents.All.Select(a => new ColorOption(a, Loc.Get("Theme.Accent." + a.TrimStart('#')), Swatch(a))).ToList();
    }

    private static SolidColorBrush Swatch(string accent)
    {
        var rgb = ThemePalette.ParseAccent(accent);
        var color = rgb is { } v
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Notifications.GlowColorResolver.Resolve(Core.Notifications.GlowColors.Accent);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
