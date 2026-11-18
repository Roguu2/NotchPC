using System.Collections.ObjectModel;
using System.Net.Http;
using NotchPC.App.Infrastructure;
using NotchPC.App.Widgets;
using NotchPC.Core.Localization;
using NotchPC.Core.Widgets;
using NotchPC.Platform.Security;
using NotchPC.Platform.Startup;

namespace NotchPC.App.Settings;

/// <summary>"Desktop widgets" section of the settings window.</summary>
internal sealed class WidgetSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly OpenMeteoClient _weather;
    private readonly SensorsClient _sensors;
    private string _cityQuery = "";
    private string _cityStatus = "";
    private string _calendarStatus = "";
    private string _cpuTempStatus = "";
    private bool _busy;
    private WeatherPlace? _selectedPlace;

    public WidgetSettingsViewModel(SettingsService settings, HttpClient http, SensorsClient sensors)
    {
        _settings = settings;
        _weather = new OpenMeteoClient(http);
        _sensors = sensors;
        _cityQuery = (W.WeatherPlace ?? "").Split(',')[0].Trim();
        RefreshTexts();
        Loc.CultureChanged += (_, _) => RefreshTexts();
    }

    public ObservableCollection<WeatherPlace> CityResults { get; } = [];

    public bool TimerEnabled
    {
        get => W.TimerEnabled;
        set => Update(s => s.Widgets.TimerEnabled = value);
    }

    public bool WeatherEnabled
    {
        get => W.WeatherEnabled;
        set => Update(s => s.Widgets.WeatherEnabled = value);
    }

    public bool CalendarEnabled
    {
        get => W.CalendarEnabled;
        set => Update(s => s.Widgets.CalendarEnabled = value);
    }

    public bool StatsEnabled
    {
        get => W.StatsEnabled;
        set => Update(s => s.Widgets.StatsEnabled = value);
    }

    public bool TwitchEnabled
    {
        get => W.TwitchEnabled;
        set => Update(s => s.Widgets.TwitchEnabled = value);
    }

    /// <summary>Accepts a name or a twitch.tv link; saved only when valid.</summary>
    public string TwitchChannel
    {
        get => W.TwitchChannel;
        set
        {
            var channel = Core.Twitch.TwitchIrc.NormalizeChannel(value);
            TwitchChannelError = channel is null && !string.IsNullOrWhiteSpace(value);
            Raise(nameof(TwitchChannelError));
            if (channel is not null && channel != W.TwitchChannel)
            {
                Update(s => s.Widgets.TwitchChannel = channel);
            }
        }
    }

    public bool TwitchChannelError { get; private set; }

    public bool Locked
    {
        get => W.Locked;
        set => Update(s => s.Widgets.Locked = value);
    }

    /// <summary>True = grid layout (snap, no overlaps), false = free layout.</summary>
    public bool GridMode
    {
        get => W.LayoutMode == Core.Settings.WidgetLayoutModes.Grid;
        set
        {
            Update(s => s.Widgets.LayoutMode = value ? Core.Settings.WidgetLayoutModes.Grid : Core.Settings.WidgetLayoutModes.Free);
            Raise(nameof(FreeMode));
        }
    }

    public bool FreeMode
    {
        get => !GridMode;
        set => GridMode = !value;
    }

    public double GridCell
    {
        get => W.GridCell;
        set
        {
            var v = (int)Math.Round(value);
            if (v != W.GridCell)
            {
                Update(s => s.Widgets.GridCell = v);
                Raise(nameof(GridCellText));
            }
        }
    }

    public string GridCellText => $"{W.GridCell} px";

    public double GridMargin
    {
        get => W.GridMargin;
        set
        {
            var v = (int)Math.Round(value);
            if (v != W.GridMargin)
            {
                Update(s => s.Widgets.GridMargin = v);
                Raise(nameof(GridMarginText));
            }
        }
    }

    public string GridMarginText => $"{W.GridMargin} px";

    public bool ShowGridWhileMoving
    {
        get => W.ShowGridWhileMoving;
        set => Update(s => s.Widgets.ShowGridWhileMoving = value);
    }

    public double? FocusMinutes
    {
        get => W.FocusMinutes;
        set => Update(s => s.Widgets.FocusMinutes = Math.Clamp((int)Math.Round(value ?? 25), 5, 120));
    }

    public double? BreakMinutes
    {
        get => W.BreakMinutes;
        set => Update(s => s.Widgets.BreakMinutes = Math.Clamp((int)Math.Round(value ?? 5), 5, 120));
    }

    public string CityQuery
    {
        get => _cityQuery;
        set => Set(ref _cityQuery, value ?? "");
    }

    public string CityStatus
    {
        get => _cityStatus;
        private set => Set(ref _cityStatus, value);
    }

    public bool HasCityResults => CityResults.Count > 1;

    /// <summary>Picking another search result switches the weather to it.</summary>
    public WeatherPlace? SelectedPlace
    {
        get => _selectedPlace;
        set
        {
            if (!Set(ref _selectedPlace, value) || value is null)
            {
                return;
            }

            _settings.Update(s =>
            {
                s.Widgets.WeatherPlace = value.Display;
                s.Widgets.WeatherLatitude = value.Latitude;
                s.Widgets.WeatherLongitude = value.Longitude;
            });
            RefreshTexts();
        }
    }

    public string CalendarStatus
    {
        get => _calendarStatus;
        private set => Set(ref _calendarStatus, value);
    }

    public bool HasCalendar => !string.IsNullOrEmpty(W.CalendarUrlProtected);

    public string CpuTempStatus
    {
        get => _cpuTempStatus;
        private set => Set(ref _cpuTempStatus, value);
    }

    public bool CpuTempTaskExists { get; private set; }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    private Core.Settings.WidgetSettings W => _settings.Current.Widgets;

    public async Task SearchCityAsync()
    {
        if (string.IsNullOrWhiteSpace(CityQuery) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var places = await _weather.SearchAsync(CityQuery, Loc.Culture.TwoLetterISOLanguageName, cts.Token);
            CityResults.Clear();
            foreach (var p in places)
            {
                CityResults.Add(p);
            }

            Raise(nameof(HasCityResults));
            if (places.Count == 0)
            {
                CityStatus = Loc.Get("Settings.Widgets.City.NotFound");
                return;
            }

            SelectedPlace = places[0];
        }
        catch (WeatherUnavailableException)
        {
            CityStatus = Loc.Get("Settings.Widgets.City.Error");
        }
        catch (OperationCanceledException)
        {
            CityStatus = Loc.Get("Settings.Widgets.City.Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <returns>True if the link was accepted (the password box can then be cleared).</returns>
    public bool SaveCalendarLink(string link)
    {
        if (IcsCalendar.NormalizeUrl(link) is null)
        {
            CalendarStatus = Loc.Get("Settings.Widgets.Ics.Invalid");
            return false;
        }

        var protectedLink = Dpapi.Protect(link.Trim());
        _settings.Update(s => s.Widgets.CalendarUrlProtected = protectedLink);
        RefreshTexts();
        return true;
    }

    public void ClearCalendarLink()
    {
        _settings.Update(s => s.Widgets.CalendarUrlProtected = null);
        RefreshTexts();
    }

    /// <summary>Back to the default column, natural sizes.</summary>
    public void ResetPositions() => _settings.Update(s => s.Widgets.Positions.Clear());

    /// <summary>
    /// One click: installs the PawnIO driver (winget) if missing and makes the sensors helper run as administrator,
    /// in one elevated step (one UAC prompt). Turning it off removes only the task (PawnIO stays, remove it in
    /// Windows Settings → Apps if you like).
    /// </summary>
    public async Task SetCpuTemperatureAsync(bool enable)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        CpuTempStatus = Loc.Get(enable ? "Settings.Widgets.CpuTemp.Working" : "Settings.Widgets.CpuTemp.Removing");
        try
        {
            if (enable)
            {
                var result = await Task.Run(() => CpuTempSetup.Enable(System.IO.Path.GetFullPath(SensorsClient.HelperPath)));
                AppLog.Info($"CPU temperature setup: {result}");
                await RefreshCpuTempAsync();
                if (result != CpuTempSetupResult.Ok)
                {
                    CpuTempStatus = Loc.Get("Settings.Widgets.CpuTemp.Result." + result);
                }
            }
            else
            {
                var ok = await Task.Run(ElevatedSensorsTask.Delete);
                await RefreshCpuTempAsync();
                if (!ok)
                {
                    CpuTempStatus = Loc.Get("Settings.Widgets.CpuTemp.Failed");
                }
            }

            _sensors.Restart();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool CpuTempReady { get; private set; }

    public async Task RefreshCpuTempAsync()
    {
        CpuTempTaskExists = await Task.Run(ElevatedSensorsTask.Exists);
        var driver = CpuTempSetup.IsDriverInstalled();
        CpuTempReady = CpuTempTaskExists && driver;
        Raise(nameof(CpuTempTaskExists));
        Raise(nameof(CpuTempReady));
        CpuTempStatus = CpuTempText(driver);
    }

    private string CpuTempText(bool driver) => CpuTempTaskExists && driver
        ? Loc.Get("Settings.Widgets.CpuTemp.On")
        : Loc.Format("Settings.Widgets.CpuTemp.State",
            Loc.Get(driver ? "Settings.Widgets.CpuTemp.Yes" : "Settings.Widgets.CpuTemp.No"),
            Loc.Get(CpuTempTaskExists ? "Settings.Widgets.CpuTemp.Yes" : "Settings.Widgets.CpuTemp.No"));

    private void Update(Action<Core.Settings.AppSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _settings.Update(change);
        Raise(name);
    }

    private void RefreshTexts()
    {
        CityStatus = string.IsNullOrEmpty(W.WeatherPlace) ? "" : Loc.Format("Settings.Widgets.City.Current", W.WeatherPlace);
        CalendarStatus = HasCalendar ? Loc.Get("Settings.Widgets.Ics.Saved") : "";
        Raise(nameof(HasCalendar));
        if (!string.IsNullOrEmpty(CpuTempStatus) && !IsBusy)
        {
            CpuTempStatus = CpuTempText(CpuTempSetup.IsDriverInstalled());
        }
    }
}
