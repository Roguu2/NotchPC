using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Notifications;
using NotchPC.Core.Settings;
using NotchPC.Core.Widgets;
using NotchPC.Platform.Security;

namespace NotchPC.App.Widgets;

/// <summary>
/// Base for desktop widget view models. <see cref="SetOnScreen"/> tells whether the widget can actually be seen
/// (not covered by windows, session not locked); live refreshing only happens while it is.
/// </summary>
internal abstract class WidgetViewModel : ObservableObject, IDisposable
{
    protected WidgetViewModel()
    {
        Loc.CultureChanged += OnCultureChanged;
    }

    /// <summary>Asks the window to re-check whether it is still uncovered (cheap; returns the new state).</summary>
    public Func<bool>? RecheckOnScreen { get; set; }

    public bool IsOnScreen { get; private set; }

    public void SetOnScreen(bool onScreen)
    {
        if (onScreen == IsOnScreen)
        {
            return;
        }

        IsOnScreen = onScreen;
        OnScreenChanged(onScreen);
    }

    public virtual void Dispose() => Loc.CultureChanged -= OnCultureChanged;

    protected static string Duration(int minutes) =>
        minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";

    protected abstract void OnScreenChanged(bool onScreen);

    protected virtual void OnCulture()
    {
    }

    protected void RaiseMany(params string[] names)
    {
        foreach (var n in names)
        {
            Raise(n);
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e) => OnCulture();
}

/// <summary>Focus timer widget (25 + 5 by default) with a daily summary when the card is tall enough.</summary>
internal sealed class FocusTimerViewModel : WidgetViewModel
{
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _phaseEnd;
    private readonly DispatcherTimer _ui;
    private FocusTimer _timer;

    public FocusTimerViewModel(SettingsService settings)
    {
        _settings = settings;
        _timer = NewTimer();
        // One-shot at the end of the running phase: works while the widget is hidden, no polling.
        _phaseEnd = new DispatcherTimer(DispatcherPriority.Normal);
        _phaseEnd.Tick += (_, _) => OnPhaseEnd();
        _ui = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1) };
        _ui.Tick += (_, _) =>
        {
            if (RecheckOnScreen?.Invoke() == false)
            {
                return; // SetOnScreen(false) already stopped us
            }

            Refresh();
        };
        _settings.Changed += OnSettingsChanged;
        Refresh();
    }

    public string TimeText { get; private set; } = "";

    public double Progress { get; private set; }

    public string Title { get; private set; } = "";

    public string LengthsText { get; private set; } = "";

    public bool IsRunning => _timer.State == FocusState.Running;

    /// <summary>Running or paused: the stop button shows.</summary>
    public bool IsActive => _timer.State != FocusState.Idle;

    public string PlayGlyph => IsRunning ? "" : "";

    /// <summary>"Today: 3 × focus · 1 h 15 min".</summary>
    public string TodayText
    {
        get
        {
            var (sessions, minutes) = TodayCounters();
            return Loc.Format("Widget.Timer.Today", sessions, Duration(minutes));
        }
    }

    /// <summary>What comes after the current phase.</summary>
    public string NextText => _timer.Phase == FocusPhase.Focus
        ? Loc.Format("Widget.Timer.NextBreak", (int)_timer.BreakLength.TotalMinutes)
        : Loc.Format("Widget.Timer.NextFocus", (int)_timer.FocusLength.TotalMinutes);

    public void Toggle()
    {
        _timer.Toggle(DateTimeOffset.Now);
        ScheduleEnd();
        UpdateUiTimer();
        Refresh();
    }

    public void Adjust(int sign)
    {
        _timer.Adjust(sign * FocusTimer.Step, DateTimeOffset.Now);
        var focus = (int)_timer.FocusLength.TotalMinutes;
        var pause = (int)_timer.BreakLength.TotalMinutes;
        _settings.Update(s =>
        {
            s.Widgets.FocusMinutes = focus;
            s.Widgets.BreakMinutes = pause;
        });
        ScheduleEnd();
        Refresh();
    }

    public void Reset()
    {
        _timer.Reset();
        ScheduleEnd();
        UpdateUiTimer();
        Refresh();
    }

    public override void Dispose()
    {
        Notifications.LiveActivityHub.Current.Clear(LiveActivity.Timer);
        _phaseEnd.Stop();
        _ui.Stop();
        _settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        UpdateUiTimer();
        if (onScreen)
        {
            Refresh();
        }
    }

    protected override void OnCulture() => Refresh();

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private (int Sessions, int Minutes) TodayCounters()
    {
        var w = _settings.Current.Widgets;
        return w.FocusStatsDay == TodayKey() ? (w.FocusSessionsToday, w.FocusMinutesToday) : (0, 0);
    }

    private FocusTimer NewTimer() => new(
        TimeSpan.FromMinutes(_settings.Current.Widgets.FocusMinutes),
        TimeSpan.FromMinutes(_settings.Current.Widgets.BreakMinutes));

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // New lengths from the settings window apply right away to an idle timer.
        var w = _settings.Current.Widgets;
        if (_timer.State == FocusState.Idle &&
            (w.FocusMinutes != (int)_timer.FocusLength.TotalMinutes || w.BreakMinutes != (int)_timer.BreakLength.TotalMinutes))
        {
            _timer = NewTimer();
            Refresh();
        }
    }

    private void OnPhaseEnd()
    {
        _phaseEnd.Stop();
        var focusMinutes = (int)_timer.FocusLength.TotalMinutes;
        if (_timer.CompleteIfDue(DateTimeOffset.Now) is { } finished)
        {
            // The notification plays NotchPC's own sound (settings → Notifications); no extra system sound.
            Notifications.NotificationHub.Current?.Post(
                Loc.Get(finished == FocusPhase.Focus ? "Notify.Timer.FocusDone" : "Notify.Timer.BreakDone"),
                Loc.Get(finished == FocusPhase.Focus ? "Notify.Timer.FocusDone.Body" : "Notify.Timer.BreakDone.Body"),
                "",
                Loc.Get("Settings.Widgets.Timer"),
                NotificationTargets.Tab(TabIds.Widgets));
            if (finished == FocusPhase.Focus)
            {
                var (sessions, minutes) = TodayCounters();
                var today = TodayKey();
                _settings.Update(s =>
                {
                    s.Widgets.FocusStatsDay = today;
                    s.Widgets.FocusSessionsToday = sessions + 1;
                    s.Widgets.FocusMinutesToday = minutes + focusMinutes;
                });
            }
        }

        ScheduleEnd();
        UpdateUiTimer(); // the break end stops the timer
        Refresh();
    }

    private void ScheduleEnd()
    {
        _phaseEnd.Stop();
        if (_timer.State != FocusState.Running)
        {
            return;
        }

        var wait = _timer.EndsAt - DateTimeOffset.Now;
        _phaseEnd.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _phaseEnd.Start();
    }

    private void UpdateUiTimer()
    {
        var run = IsOnScreen && IsRunning;
        if (run && !_ui.IsEnabled)
        {
            _ui.Start();
        }
        else if (!run && _ui.IsEnabled)
        {
            _ui.Stop();
        }
    }

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        TimeText = FocusTimer.Format(_timer.Remaining(now));
        Progress = _timer.State == FocusState.Idle ? 0 : _timer.Progress(now);
        Title = Loc.Get(_timer.Phase == FocusPhase.Focus ? "Widget.Timer.Focus" : "Widget.Timer.Break");
        LengthsText = Loc.Format("Widget.Timer.Lengths", (int)_timer.FocusLength.TotalMinutes, (int)_timer.BreakLength.TotalMinutes);
        RaiseMany(nameof(TimeText), nameof(Progress), nameof(Title), nameof(LengthsText), nameof(IsRunning), nameof(IsActive),
                  nameof(PlayGlyph), nameof(TodayText), nameof(NextText));
        PublishActivity();
    }

    /// <summary>A running phase shows in the closed pill as a countdown (Live Activity).</summary>
    private void PublishActivity()
    {
        if (_timer.State == FocusState.Running)
        {
            var focus = _timer.Phase == FocusPhase.Focus;
            Notifications.LiveActivityHub.Current.Set(new LiveActivity(
                LiveActivity.Timer, "\uE916", Title, LiveActivityClock.Countdown, _timer.EndsAt, 2, focus ? "#FF9F0A" : "#30D158"));
        }
        else
        {
            Notifications.LiveActivityHub.Current.Clear(LiveActivity.Timer);
        }
    }
}

internal enum FeedState
{
    NotSet,
    Loading,
    Ready,
    Error,
}

/// <summary>One tile of the weather forecast grid (an hour or a day).</summary>
internal sealed record ForecastItem(string Label, WeatherKind Kind, bool IsDay, string Temperature, string Secondary, string Precipitation);

/// <summary>Weather widget: refreshed every 30 minutes (network only, no UI timer). Big cards show a forecast grid.</summary>
internal sealed class WeatherViewModel : WidgetViewModel
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settings;
    private readonly OpenMeteoClient _client;
    private readonly DispatcherTimer _refresh;
    private (double, double)? _loadedFor;
    private WeatherNow? _now;
    private int _capacity;

    public WeatherViewModel(SettingsService settings, HttpClient http)
    {
        _settings = settings;
        _client = new OpenMeteoClient(http);
        _refresh = new DispatcherTimer(DispatcherPriority.Background);
        _refresh.Tick += async (_, _) => await LoadAsync();
        _settings.Changed += OnSettingsChanged;
        _ = LoadAsync();
    }

    public FeedState State { get; private set; } = FeedState.NotSet;

    public string TemperatureText => _now is { } n ? $"{Math.Round(n.TemperatureC):0}°" : "—";

    public WeatherKind Kind => _now?.Kind ?? WeatherKind.Cloudy;

    public bool IsDay => _now?.IsDay ?? true;

    public string Summary => State switch
    {
        FeedState.NotSet => Loc.Get("Widget.Weather.NotSet"),
        FeedState.Loading when _now is null => Loc.Get("Widget.Weather.Loading"),
        FeedState.Error when _now is null => Loc.Get("Widget.Weather.Error"),
        _ => $"{Describe(_now!.Kind, _now.IsDay)} · {PlaceShort}",
    };

    public string HighText => _now is { } n ? $"{Math.Round(n.MaxC):0}°" : "";

    public string LowText => _now is { } n ? $"{Math.Round(n.MinC):0}°" : "";

    public bool HasData => _now is not null;

    /// <summary>"hourly" or "daily" (chosen with the toggle inside the widget).</summary>
    public bool IsDaily => _settings.Current.Widgets.WeatherForecast == "daily";

    public ObservableCollection<ForecastItem> Forecast { get; } = [];

    public bool ShowForecast => _capacity > 0 && Forecast.Count > 0;

    private string PlaceShort => (_settings.Current.Widgets.WeatherPlace ?? "").Split(',')[0].Trim();

    /// <summary>How many forecast tiles fit (set by the view when the card is resized).</summary>
    public void SetCapacity(int tiles)
    {
        if (tiles == _capacity)
        {
            return;
        }

        _capacity = tiles;
        RebuildForecast();
    }

    public void SetDaily(bool daily) =>
        _settings.Update(s => s.Widgets.WeatherForecast = daily ? "daily" : "hourly");

    /// <summary>Called after resume from sleep.</summary>
    public Task RefreshNowAsync() => LoadAsync();

    public override void Dispose()
    {
        _refresh.Stop();
        _settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        // Data is refreshed on a 30-minute schedule; if it went stale while hidden, refresh when shown again.
        if (onScreen && _now is { } n && DateTimeOffset.UtcNow - n.FetchedAt > RefreshEvery)
        {
            _ = LoadAsync();
        }
    }

    protected override void OnCulture()
    {
        RebuildForecast();
        RaiseAll();
    }

    private static string Describe(WeatherKind kind, bool isDay) => Loc.Get(kind switch
    {
        WeatherKind.Clear => isDay ? "Weather.Sunny" : "Weather.ClearNight",
        _ => "Weather." + kind,
    });

    private static string Precip(int chance) => chance >= 20 ? $"{chance}%" : "";

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var w = _settings.Current.Widgets;
        var target = w.WeatherLatitude is { } lat && w.WeatherLongitude is { } lon ? (lat, lon) : ((double, double)?)null;
        if (target != _loadedFor)
        {
            _now = null;
            _ = LoadAsync();
        }

        RebuildForecast(); // the hourly/daily toggle may have changed
        Raise(nameof(IsDaily));
    }

    private void RebuildForecast()
    {
        Forecast.Clear();
        if (_now is { } n && _capacity > 0)
        {
            var culture = Loc.Culture;
            var timePattern = culture.DateTimeFormat.ShortTimePattern.Contains('t', StringComparison.Ordinal) ? "h tt" : "HH:mm";
            var items = IsDaily
                ? n.Days.Select(d => new ForecastItem(
                    culture.TextInfo.ToTitleCase(d.Date.ToString("ddd", culture).TrimEnd('.')),
                    d.Kind,
                    true,
                    $"{Math.Round(d.MaxC):0}°",
                    $"{Math.Round(d.MinC):0}°",
                    Precip(d.PrecipitationChance)))
                : n.Hours.Skip(1).Select(h => new ForecastItem(
                    h.Time.ToString(timePattern, culture),
                    h.Kind,
                    h.IsDay,
                    $"{Math.Round(h.TemperatureC):0}°",
                    "",
                    Precip(h.PrecipitationChance)));
            foreach (var item in items.Take(_capacity))
            {
                Forecast.Add(item);
            }
        }

        Raise(nameof(ShowForecast));
    }

    private async Task LoadAsync()
    {
        _refresh.Stop();
        var w = _settings.Current.Widgets;
        if (w.WeatherLatitude is not { } lat || w.WeatherLongitude is not { } lon)
        {
            _loadedFor = null;
            State = FeedState.NotSet;
            RebuildForecast();
            RaiseAll();
            return;
        }

        _loadedFor = (lat, lon);
        State = FeedState.Loading;
        RaiseAll();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _now = await _client.GetCurrentAsync(lat, lon, cts.Token);
            State = FeedState.Ready;
            _refresh.Interval = RefreshEvery;
        }
        catch (WeatherUnavailableException ex)
        {
            AppLog.Error("Weather refresh failed", ex);
            State = FeedState.Error;
            _refresh.Interval = RetryAfter;
        }
        catch (OperationCanceledException)
        {
            State = FeedState.Error;
            _refresh.Interval = RetryAfter;
        }

        _refresh.Start();
        RebuildForecast();
        RaiseAll();
    }

    private void RaiseAll() => RaiseMany(nameof(State), nameof(TemperatureText), nameof(Kind), nameof(IsDay), nameof(Summary),
                                         nameof(HighText), nameof(LowText), nameof(HasData), nameof(IsDaily), nameof(ShowForecast));
}

internal enum CalendarLayout
{
    Compact,
    Agenda,
    Month,
}

/// <summary>A row of the agenda list: a day header or an event.</summary>
internal abstract record AgendaRow;

internal sealed record AgendaHeaderRow(string Text) : AgendaRow;

internal sealed record AgendaEventRow(string Time, string Title, bool IsNow) : AgendaRow;

/// <summary>A day cell of the month grid.</summary>
internal sealed record MonthDay(DateOnly Date, string Number, bool InMonth, bool IsToday, bool IsSelected, bool HasEvents);

/// <summary>
/// Calendar widget: date, ISO week and the next event from the user's iCal link. Taller cards show an agenda
/// (scrollable), big cards a month grid where any day can be picked.
/// </summary>
internal sealed class CalendarViewModel : WidgetViewModel
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private const int AgendaDays = 60;

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _minute;
    private readonly DispatcherTimer _reminder;
    private readonly HashSet<string> _reminded = [];
    private CalendarEvent? _reminderEvent;
    private readonly Func<DateTime> _clock = () => DateTime.Now;
    private IcsFeed? _feed;
    private IReadOnlyList<CalendarEvent> _events = [];
    private IReadOnlyList<CalendarEvent> _monthEvents = [];
    private string? _loadedFor;
    private CalendarLayout _layout;
    private DateOnly _month;
    private DateOnly _selected;

    public CalendarViewModel(SettingsService settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
        _month = new DateOnly(Now.Year, Now.Month, 1);
        _selected = DateOnly.FromDateTime(Now);
        _refresh = new DispatcherTimer(DispatcherPriority.Background);
        _refresh.Tick += async (_, _) => await LoadAsync();
        // Once a minute while on screen: date rollover, "now" for a running event, day progress.
        _minute = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _minute.Tick += (_, _) => RaiseAll();
        // One timer, set to the next reminder only (no polling).
        _reminder = new DispatcherTimer(DispatcherPriority.Background);
        _reminder.Tick += (_, _) => OnReminder();
        _settings.Changed += OnSettingsChanged;
        _ = LoadAsync();
    }

    public FeedState State { get; private set; } = FeedState.NotSet;

    public CalendarLayout Layout => _layout;

    public string DayNumber => Now.Day.ToString(Loc.Culture);

    public string DayName => Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(Now.DayOfWeek).TrimEnd('.').ToUpper(Loc.Culture);

    public string MonthName => Loc.Culture.DateTimeFormat.GetAbbreviatedMonthName(Now.Month).TrimEnd('.').ToUpper(Loc.Culture);

    public string WeekText => Loc.Format("Widget.Calendar.Week", ISOWeek.GetWeekOfYear(Now));

    /// <summary>Share of the day that has passed (thin line under the event, as in the reference).</summary>
    public double DayProgress => Now.TimeOfDay.TotalMinutes / (24 * 60) * 100;

    public bool HasEvent => NextEvent is not null;

    public string EventTitle => NextEvent?.Title ?? StatusText;

    public string EventTime => NextEvent is { } e ? TimeLabel(e, DateOnly.FromDateTime(Now), withDay: true) : "";

    public ObservableCollection<AgendaRow> Agenda { get; } = [];

    public bool AgendaEmpty => Agenda.Count == 0;

    public string AgendaEmptyText => _layout == CalendarLayout.Month && State == FeedState.Ready
        ? Loc.Get("Widget.Calendar.NoEventsDay")
        : StatusText;

    public string MonthTitle => Loc.Culture.TextInfo.ToTitleCase(_month.ToString("MMMM yyyy", Loc.Culture));

    public IReadOnlyList<string> WeekdayNames { get; private set; } = [];

    public ObservableCollection<MonthDay> MonthDays { get; } = [];

    private DateTime Now => _clock();

    private CalendarEvent? NextEvent => IcsCalendar.Next(_events, Now);

    private string StatusText => State switch
    {
        FeedState.NotSet => Loc.Get("Widget.Calendar.NotSet"),
        FeedState.Error => Loc.Get("Widget.Calendar.Error"),
        FeedState.Loading => Loc.Get("Widget.Weather.Loading"),
        _ => Loc.Get("Widget.Calendar.NoEvents"),
    };

    /// <summary>Set by the view from the card size.</summary>
    public void SetLayout(CalendarLayout layout)
    {
        if (layout == _layout)
        {
            return;
        }

        _layout = layout;
        Raise(nameof(Layout));
        Rebuild();
    }

    public void ShiftMonth(int delta)
    {
        _month = _month.AddMonths(delta);
        ComputeMonthEvents();
        Rebuild();
    }

    public void SelectDay(DateOnly day)
    {
        _selected = day;
        if (day.Year != _month.Year || day.Month != _month.Month)
        {
            _month = new DateOnly(day.Year, day.Month, 1);
            ComputeMonthEvents();
        }

        Rebuild();
    }

    /// <summary>Called after resume from sleep.</summary>
    public Task RefreshNowAsync() => LoadAsync();

    public override void Dispose()
    {
        _meetingTimer?.Stop();
        Notifications.LiveActivityHub.Current.Clear(LiveActivity.Meeting);
        _refresh.Stop();
        _minute.Stop();
        _reminder.Stop();
        _settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        if (onScreen)
        {
            RaiseAll();
            _minute.Start();
        }
        else
        {
            _minute.Stop();
        }
    }

    protected override void OnCulture() => RaiseAll();

    private string DayLabel(DateOnly day)
    {
        var today = DateOnly.FromDateTime(Now);
        return day == today ? Loc.Get("Widget.Calendar.Today")
            : day == today.AddDays(1) ? Loc.Get("Widget.Calendar.Tomorrow")
            : Loc.Culture.TextInfo.ToTitleCase(IcsCalendar.ShortDay(day, Loc.Culture));
    }

    private string TimeLabel(CalendarEvent e, DateOnly day, bool withDay)
    {
        var now = Now;
        if (e.AllDay || (e.StartLocal <= day.ToDateTime(TimeOnly.MinValue) && e.EndLocal >= day.AddDays(1).ToDateTime(TimeOnly.MinValue)))
        {
            return withDay && DateOnly.FromDateTime(e.StartLocal) > DateOnly.FromDateTime(now)
                ? DayLabel(DateOnly.FromDateTime(e.StartLocal))
                : Loc.Get("Widget.Calendar.AllDay");
        }

        if (e.StartLocal <= now && e.EndLocal > now)
        {
            return Loc.Get("Widget.Calendar.Now");
        }

        var time = e.StartLocal.ToString("t", Loc.Culture);
        var startDay = DateOnly.FromDateTime(e.StartLocal);
        return withDay && startDay != DateOnly.FromDateTime(now) ? $"{DayLabel(startDay)} {time}" : time;
    }

    private int _reminderMinutes = -1;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_settings.Current.Widgets.CalendarUrlProtected != _loadedFor)
        {
            _ = LoadAsync();
        }
        else if (_settings.Current.Notifications.CalendarReminderMinutes != _reminderMinutes)
        {
            _reminderMinutes = _settings.Current.Notifications.CalendarReminderMinutes;
            ScheduleReminder();
        }
    }

    private void ComputeMonthEvents()
    {
        if (_feed is null)
        {
            _monthEvents = [];
            return;
        }

        var grid = IcsCalendar.MonthGrid(_month.Year, _month.Month, Loc.Culture.DateTimeFormat.FirstDayOfWeek);
        _monthEvents = _feed.Between(grid[0].ToDateTime(TimeOnly.MinValue), grid[^1].AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local);
    }

    /// <summary>Rebuilds the agenda rows and the month grid for the current layout.</summary>
    private void Rebuild()
    {
        Agenda.Clear();
        MonthDays.Clear();
        var today = DateOnly.FromDateTime(Now);

        if (_layout == CalendarLayout.Agenda)
        {
            foreach (var day in IcsCalendar.Agenda(_events, today, AgendaDays, Now))
            {
                Agenda.Add(new AgendaHeaderRow(DayLabel(day.Day)));
                foreach (var e in day.Events)
                {
                    Agenda.Add(new AgendaEventRow(TimeLabel(e, day.Day, withDay: false), e.Title, e.StartLocal <= Now && e.EndLocal > Now));
                }
            }
        }
        else if (_layout == CalendarLayout.Month)
        {
            var culture = Loc.Culture;
            var first = culture.DateTimeFormat.FirstDayOfWeek;
            WeekdayNames = Enumerable.Range(0, 7)
                .Select(i => culture.DateTimeFormat.GetShortestDayName((DayOfWeek)(((int)first + i) % 7)).ToUpper(culture))
                .ToList();
            var withEvents = _monthEvents.ToList();
            foreach (var d in IcsCalendar.MonthGrid(_month.Year, _month.Month, first))
            {
                MonthDays.Add(new MonthDay(d, d.Day.ToString(culture), d.Month == _month.Month, d == today, d == _selected,
                    withEvents.Exists(e => e.IsOn(d))));
            }

            foreach (var e in withEvents.Where(e => e.IsOn(_selected)).OrderBy(e => !e.AllDay).ThenBy(e => e.StartLocal))
            {
                Agenda.Add(new AgendaEventRow(TimeLabel(e, _selected, withDay: false), e.Title, e.StartLocal <= Now && e.EndLocal > Now));
            }
        }

        RaiseMany(nameof(AgendaEmpty), nameof(AgendaEmptyText), nameof(MonthTitle), nameof(WeekdayNames));
    }

    private async Task LoadAsync()
    {
        _refresh.Stop();
        var protectedUrl = _settings.Current.Widgets.CalendarUrlProtected;
        _loadedFor = protectedUrl;
        var url = IcsCalendar.NormalizeUrl(Dpapi.TryUnprotect(protectedUrl));
        if (url is null)
        {
            _feed = null;
            _events = [];
            _monthEvents = [];
            State = FeedState.NotSet;
            RaiseAll();
            return;
        }

        State = FeedState.Loading;
        RaiseAll();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ics = await _http.GetStringAsync(url, cts.Token);
            var now = DateTime.Now;
            (_feed, _events) = await Task.Run(() =>
            {
                var feed = IcsFeed.Parse(ics);
                return (feed, feed.Between(now.Date, now.Date.AddDays(AgendaDays), TimeZoneInfo.Local));
            }, cts.Token);
            ComputeMonthEvents();
            State = FeedState.Ready;
            _refresh.Interval = RefreshEvery;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or OperationCanceledException)
        {
            // Never log the URL itself: it is a secret link to the user's calendar.
            AppLog.Error($"Calendar refresh failed: {ex.GetType().Name}");
            State = FeedState.Error;
            _refresh.Interval = RetryAfter;
        }

        _refresh.Start();
        ScheduleReminder();
        RaiseAll();
    }

    /// <summary>Arms the timer for the next event that needs a reminder (settings: minutes before, 0 = off).</summary>
    private void ScheduleReminder()
    {
        _reminder.Stop();
        _reminderEvent = null;
        var minutes = _settings.Current.Notifications.CalendarReminderMinutes;
        if (ReminderPlanner.Next(_events, DateTime.Now, minutes, _reminded) is not { } next)
        {
            return;
        }

        _reminderEvent = next.Event;
        var delay = next.FireAt - DateTime.Now;
        _reminder.Interval = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        _reminder.Start();
    }

    private void OnReminder()
    {
        _reminder.Stop();
        if (_reminderEvent is { } e && e.StartLocal > DateTime.Now)
        {
            _reminded.Add(ReminderPlanner.KeyOf(e));
            var left = (int)Math.Ceiling((e.StartLocal - DateTime.Now).TotalMinutes);
            Notifications.NotificationHub.Current?.Post(
                e.Title,
                Loc.Format("Notify.Calendar.Body", e.StartLocal.ToString("t", Loc.Culture), left),
                "",
                Loc.Get("Settings.Widgets.Calendar"),
                CalendarWebLink.For(IcsCalendar.NormalizeUrl(Dpapi.TryUnprotect(_settings.Current.Widgets.CalendarUrlProtected)), e.StartLocal)?.AbsoluteUri
                    ?? NotificationTargets.Tab(TabIds.Widgets));
        }

        ScheduleReminder();
    }

    private void RaiseAll()
    {
        Rebuild();
        RaiseMany(nameof(State), nameof(DayNumber), nameof(DayName), nameof(MonthName), nameof(WeekText), nameof(DayProgress),
                  nameof(HasEvent), nameof(EventTitle), nameof(EventTime));
        PublishMeeting();
    }

    private DispatcherTimer? _meetingTimer;

    /// <summary>
    /// A meeting that starts within 15 minutes (until 5 minutes in) shows in the pill (Live Activity). One timer:
    /// set to "15 minutes before" the next event, then every 30 s while one is on; works with the card hidden.
    /// </summary>
    private void PublishMeeting()
    {
        _meetingTimer ??= CreateMeetingTimer();
        _meetingTimer.Stop();
        var now = Now;
        var soon = _events
            .Where(e => !e.AllDay && LiveActivities.MeetingIsSoon(e.StartLocal, now, TimeSpan.FromMinutes(15)))
            .OrderBy(e => e.StartLocal)
            .FirstOrDefault();
        if (soon is null)
        {
            Notifications.LiveActivityHub.Current.Clear(LiveActivity.Meeting);
            var next = _events.Where(e => !e.AllDay && e.StartLocal > now).OrderBy(e => e.StartLocal).FirstOrDefault();
            if (next is not null)
            {
                var wait = next.StartLocal - TimeSpan.FromMinutes(15) - now;
                _meetingTimer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : wait;
                _meetingTimer.Start();
            }

            return;
        }

        Notifications.LiveActivityHub.Current.Set(new LiveActivity(
            LiveActivity.Meeting, "\uE787", soon.Title, LiveActivityClock.Countdown, new DateTimeOffset(soon.StartLocal), 1, "#4C8DFF"));
        _meetingTimer.Interval = TimeSpan.FromSeconds(30);
        _meetingTimer.Start();
    }

    private DispatcherTimer CreateMeetingTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background);
        t.Tick += (_, _) => PublishMeeting();
        return t;
    }
}

/// <summary>One line of the stats chart (values 0..100, oldest first).</summary>
internal sealed record ChartSeries(System.Windows.Media.Brush Brush, IReadOnlyList<double> Values);

/// <summary>CPU/GPU/RAM/network widget fed by the sensors helper while on screen; big cards add a 60 s chart.</summary>
internal sealed class StatsViewModel : WidgetViewModel
{
    private const int HistoryLength = 60;

    private readonly SensorsClient _sensors;
    private readonly List<double> _cpu = [];
    private readonly List<double> _gpu = [];
    private readonly List<double> _ram = [];
    private SensorSnapshot? _last;

    public StatsViewModel(SensorsClient sensors)
    {
        _sensors = sensors;
        _sensors.SnapshotReceived += OnSnapshot;
        Series =
        [
            new ChartSeries((System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("StatsRamBrush"), _ram),
            new ChartSeries((System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("StatsGpuBrush"), _gpu),
            new ChartSeries((System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("StatsCpuBrush"), _cpu),
        ];
    }

    public double CpuLoad => _last?.CpuLoad ?? 0;

    public double GpuLoad => _last?.GpuLoad ?? 0;

    public double RamLoad => _last?.RamLoad ?? 0;

    public string CpuText => $"{CpuLoad,3:0}%";

    public string GpuText => _last?.GpuLoad is { } g ? $"{g,3:0}%" : "  —";

    public string RamText => $"{RamLoad,3:0}%";

    public string CpuTempText => Temp(_last?.CpuTemp);

    public string GpuTempText => Temp(_last?.GpuTemp);

    public string DownText => "↓ " + SensorProtocol.FormatRate(_last?.NetDown ?? 0);

    public string UpText => "↑ " + SensorProtocol.FormatRate(_last?.NetUp ?? 0);

    /// <summary>Tooltip on the CPU temperature: why it shows a dash (null = no tooltip).</summary>
    public string? CpuTempHint => _last is { CpuTemp: null } ? Loc.Get("Widget.Stats.CpuTempHint") : null;

    /// <summary>"i9-9900K · RTX 2070 · 17/32 GB".</summary>
    public string HardwareText => _last is not { } s ? "" : string.Join(" · ", new[]
    {
        Short(s.CpuName), Short(s.GpuName), s.RamTotalGb > 0 ? $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB" : null,
    }.Where(x => !string.IsNullOrEmpty(x)));

    public IReadOnlyList<ChartSeries> Series { get; }

    /// <summary>Bumped on every sample so the chart redraws.</summary>
    public int ChartVersion { get; private set; }

    /// <summary>False while paused (covered): values are dimmed.</summary>
    public bool IsLive => IsOnScreen && _last is not null;

    public override void Dispose()
    {
        _sensors.SnapshotReceived -= OnSnapshot;
        _sensors.Release(this);
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        if (onScreen)
        {
            _sensors.Acquire(this);
        }
        else
        {
            _sensors.Release(this);
            // A gap in the history would be misleading: start the chart again when visible.
            _cpu.Clear();
            _gpu.Clear();
            _ram.Clear();
        }

        Raise(nameof(IsLive));
    }

    private static string Temp(double? t) => t is { } v ? $"{v,3:0}°" : "  —";

    /// <summary>"Intel Core i9-9900K" → "i9-9900K", "NVIDIA GeForce RTX 2070" → "RTX 2070".</summary>
    private static string? Short(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var prefix in new[] { "Intel Core ", "AMD Ryzen ", "NVIDIA GeForce ", "AMD Radeon ", "Intel " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (prefix.StartsWith("AMD Ryzen", StringComparison.Ordinal) ? "Ryzen " : "") + name[prefix.Length..];
            }
        }

        return name;
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength)
        {
            list.RemoveAt(0);
        }
    }

    private void OnSnapshot(object? sender, SensorSnapshot s)
    {
        // Piggy-back the "still visible?" check on the 1 Hz data: stops the helper as soon as we are covered.
        if (RecheckOnScreen?.Invoke() == false)
        {
            return;
        }

        _last = s;
        Push(_cpu, s.CpuLoad);
        Push(_gpu, s.GpuLoad ?? 0);
        Push(_ram, s.RamLoad);
        ChartVersion++;
        RaiseMany(nameof(CpuLoad), nameof(GpuLoad), nameof(RamLoad), nameof(CpuText), nameof(GpuText), nameof(RamText),
                  nameof(CpuTempText), nameof(GpuTempText), nameof(DownText), nameof(UpText), nameof(CpuTempHint),
                  nameof(IsLive), nameof(HardwareText), nameof(ChartVersion));
    }
}
