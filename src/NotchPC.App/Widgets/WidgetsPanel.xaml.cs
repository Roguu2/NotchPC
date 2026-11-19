using System.Windows.Controls;
using NotchPC.App.Infrastructure;

namespace NotchPC.App.Widgets;

/// <summary>Widgets tab of the notch: quick on/off for each desktop widget and the position lock.</summary>
public partial class WidgetsPanel : UserControl
{
    public WidgetsPanel()
    {
        InitializeComponent();
    }
}

/// <summary>Thin bindable view over the widget settings for the notch tab.</summary>
internal sealed class WidgetsTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public WidgetsTabViewModel(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) =>
        {
            // The settings window may have changed the same switches.
            Raise(nameof(TimerEnabled));
            Raise(nameof(WeatherEnabled));
            Raise(nameof(CalendarEnabled));
            Raise(nameof(StatsEnabled));
            Raise(nameof(TwitchEnabled));
            Raise(nameof(StickyEnabled));
            Raise(nameof(TodoEnabled));
            Raise(nameof(SystemEnabled));
            Raise(nameof(AppsEnabled));
            Raise(nameof(GamesEnabled));
            Raise(nameof(Locked));
            Raise(nameof(GridMode));
        };
    }

    public bool TimerEnabled
    {
        get => _settings.Current.Widgets.TimerEnabled;
        set => _settings.Update(s => s.Widgets.TimerEnabled = value);
    }

    public bool WeatherEnabled
    {
        get => _settings.Current.Widgets.WeatherEnabled;
        set => _settings.Update(s => s.Widgets.WeatherEnabled = value);
    }

    public bool CalendarEnabled
    {
        get => _settings.Current.Widgets.CalendarEnabled;
        set => _settings.Update(s => s.Widgets.CalendarEnabled = value);
    }

    public bool StatsEnabled
    {
        get => _settings.Current.Widgets.StatsEnabled;
        set => _settings.Update(s => s.Widgets.StatsEnabled = value);
    }

    public bool TwitchEnabled
    {
        get => _settings.Current.Widgets.TwitchEnabled;
        set => _settings.Update(s => s.Widgets.TwitchEnabled = value);
    }

    public bool StickyEnabled
    {
        get => _settings.Current.Widgets.StickyEnabled;
        set => _settings.Update(s => s.Widgets.StickyEnabled = value);
    }

    public bool TodoEnabled
    {
        get => _settings.Current.Widgets.TodoEnabled;
        set => _settings.Update(s => s.Widgets.TodoEnabled = value);
    }

    public bool SystemEnabled
    {
        get => _settings.Current.Widgets.SystemEnabled;
        set => _settings.Update(s => s.Widgets.SystemEnabled = value);
    }

    public bool AppsEnabled
    {
        get => _settings.Current.Widgets.AppsEnabled;
        set => _settings.Update(s => s.Widgets.AppsEnabled = value);
    }

    public bool GamesEnabled
    {
        get => _settings.Current.Widgets.GamesEnabled;
        set => _settings.Update(s => s.Widgets.GamesEnabled = value);
    }

    public bool GridMode
    {
        get => _settings.Current.Widgets.LayoutMode == Core.Settings.WidgetLayoutModes.Grid;
        set => _settings.Update(s => s.Widgets.LayoutMode = value ? Core.Settings.WidgetLayoutModes.Grid : Core.Settings.WidgetLayoutModes.Free);
    }

    public bool Locked
    {
        get => _settings.Current.Widgets.Locked;
        set => _settings.Update(s => s.Widgets.Locked = value);
    }
}
