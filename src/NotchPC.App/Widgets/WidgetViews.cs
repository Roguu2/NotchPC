using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace NotchPC.App.Widgets;

// Each widget decides from its size what to show: the compact layout always, more data when there is room.

public partial class TimerWidget : UserControl
{
    private const double SummaryFrom = 140;
    private const double SummaryHeight = 62;

    public TimerWidget()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
    }

    private FocusTimerViewModel? ViewModel => DataContext as FocusTimerViewModel;

    private void Fit()
    {
        var showSummary = ActualHeight >= SummaryFrom;
        Summary.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
        // The summary is not measured yet when it just became visible: use its fixed height (2 lines + padding + margin).
        var summary = showSummary ? SummaryHeight : 0;
        var dial = Math.Clamp(Math.Min(ActualHeight - summary, ActualWidth * 0.45), 60, 220);
        Dial.Width = dial;
        Dial.Height = dial;
    }

    private void OnToggle(object sender, RoutedEventArgs e) => ViewModel?.Toggle();

    private void OnMinus(object sender, RoutedEventArgs e) => ViewModel?.Adjust(-1);

    private void OnPlus(object sender, RoutedEventArgs e) => ViewModel?.Adjust(+1);

    private void OnDialMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ViewModel?.Reset();
            e.Handled = true;
        }
    }
}

public partial class WeatherWidget : UserControl
{
    private const double TileWidth = 48;
    private const double TileHeight = 70;
    private const double HeaderHeight = 34;

    public WeatherWidget()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
        DataContextChanged += (_, _) => Fit();
    }

    private WeatherViewModel? ViewModel => DataContext as WeatherViewModel;

    /// <summary>As many forecast tiles as fit below the current conditions (columns × rows).</summary>
    private void Fit()
    {
        if (ViewModel is not { } vm || ActualWidth <= 0)
        {
            return;
        }

        var cols = Math.Max(1, (int)(ActualWidth / TileWidth));
        var rows = (int)((ActualHeight - Compact.ActualHeight - HeaderHeight) / TileHeight);
        ForecastGrid.Tag = cols;
        vm.SetCapacity(rows > 0 ? cols * rows : 0);
    }

    private void OnHourly(object sender, RoutedEventArgs e) => ViewModel?.SetDaily(false);

    private void OnDaily(object sender, RoutedEventArgs e) => ViewModel?.SetDaily(true);
}

public partial class CalendarWidget : UserControl
{
    private const double AgendaFrom = 140;
    private const double MonthFromHeight = 330;
    private const double MonthFromWidth = 250;

    public CalendarWidget()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
        DataContextChanged += (_, _) => Fit();
    }

    private CalendarViewModel? ViewModel => DataContext as CalendarViewModel;

    /// <summary>Compact (next event) → agenda (list) → month grid, depending on the card size.</summary>
    private void Fit()
    {
        if (ViewModel is not { } vm || ActualWidth <= 0)
        {
            return;
        }

        var layout = ActualHeight >= MonthFromHeight && ActualWidth >= MonthFromWidth ? CalendarLayout.Month
            : ActualHeight >= AgendaFrom ? CalendarLayout.Agenda
            : CalendarLayout.Compact;
        CompactBody.Visibility = layout == CalendarLayout.Compact ? Visibility.Visible : Visibility.Collapsed;
        AgendaBody.Visibility = layout == CalendarLayout.Agenda ? Visibility.Visible : Visibility.Collapsed;
        MonthBody.Visibility = layout == CalendarLayout.Month ? Visibility.Visible : Visibility.Collapsed;
        vm.SetLayout(layout);
    }

    private void OnPrevMonth(object sender, RoutedEventArgs e) => ViewModel?.ShiftMonth(-1);

    private void OnNextMonth(object sender, RoutedEventArgs e) => ViewModel?.ShiftMonth(+1);

    private void OnDay(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateOnly day })
        {
            ViewModel?.SelectDay(day);
        }
    }
}

public partial class StatsWidget : UserControl
{
    private const double ExtraFrom = 60; // extra room needed (beyond the compact rows) for the chart

    public StatsWidget()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
    }

    private void Fit()
    {
        var show = ActualHeight - Compact.ActualHeight >= ExtraFrom;
        Extra.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Chart.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>true ↔ false for one-way bindings (e.g. "Hours" checked when not daily).</summary>
public sealed class InvertBool : IValueConverter
{
    public static InvertBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public partial class TwitchWidget : UserControl
{
    private bool _atBottom = true;

    public TwitchWidget()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TwitchChatViewModel vm)
            {
                vm.Messages.CollectionChanged += (_, _) =>
                {
                    if (_atBottom)
                    {
                        Dispatcher.BeginInvoke(Scroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
                    }
                };
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TwitchChatViewModel.IsLive))
                    {
                        Dot.Fill = vm.IsLive ? LiveBrush : IdleBrush;
                    }
                };
            }
        };
    }

    private static readonly System.Windows.Media.Brush LiveBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58));

    private static readonly System.Windows.Media.Brush IdleBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    /// <summary>New messages keep the view at the bottom, unless the user scrolled up to read.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            _atBottom = Scroll.VerticalOffset >= Scroll.ScrollableHeight - 4;
        }
    }
}

/// <summary>Fixed compact height for cards whose lists would otherwise grow the card when it is first measured.</summary>
internal static class CompactMeasure
{
    public static Size Limit(Size constraint, double compactHeight) =>
        double.IsInfinity(constraint.Height) ? constraint with { Height = compactHeight } : constraint;
}

public partial class StickyWidget : UserControl
{
    private const double CompactHeight = 140;

    public StickyWidget() => InitializeComponent();

    private StickyNotesViewModel? ViewModel => DataContext as StickyNotesViewModel;

    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(CompactMeasure.Limit(constraint, CompactHeight));
        return double.IsInfinity(constraint.Height) ? size with { Height = CompactHeight } : size;
    }

    private void OnColor(object sender, RoutedEventArgs e) => ViewModel?.CycleColor();

    private void OnPrev(object sender, RoutedEventArgs e) => ViewModel?.Next(-1);

    private void OnNext(object sender, RoutedEventArgs e) => ViewModel?.Next(+1);

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        ViewModel?.Add();
        Editor.Focus();
    }

    private void OnDelete(object sender, RoutedEventArgs e) => ViewModel?.Delete();
}

public partial class TodoWidget : UserControl
{
    private const double CompactHeight = 170;

    public TodoWidget() => InitializeComponent();

    private TodoViewModel? ViewModel => DataContext as TodoViewModel;

    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(CompactMeasure.Limit(constraint, CompactHeight));
        return double.IsInfinity(constraint.Height) ? size with { Height = CompactHeight } : size;
    }

    private void OnNewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.Add();
            e.Handled = true;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TodoItem item)
        {
            ViewModel?.Remove(item);
        }
    }

    private void OnClearDone(object sender, RoutedEventArgs e) => ViewModel?.ClearDone();
}

public partial class SystemWidget : UserControl
{
    public SystemWidget() => InitializeComponent();
}

public partial class AppsWidget : UserControl
{
    private const double CompactHeight = 176;

    public AppsWidget()
    {
        InitializeComponent();
        _ = new Controls.DragReorder<Apps.AppTile>(List, _ => true, (d, t, after) => ViewModel?.Move(d, t, after));
    }

    private Apps.AppsViewModel? ViewModel => DataContext as Apps.AppsViewModel;

    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(CompactMeasure.Limit(constraint, CompactHeight));
        return double.IsInfinity(constraint.Height) ? size with { Height = CompactHeight } : size;
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Apps.AppTile tile)
        {
            Apps.AppsViewModel.Launch(tile);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Apps.AppTile tile)
        {
            ViewModel?.Remove(tile);
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = NotchPC.Core.Localization.Loc.Get("Apps.Add.Title"),
            Filter = $"{NotchPC.Core.Localization.Loc.Get("Apps.Add.Filter")}|*.exe;*.lnk;*.url;*.appref-ms|{NotchPC.Core.Localization.Loc.Get("Apps.Add.AllFiles")}|*.*",
            DereferenceLinks = false,
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };
        if (dialog.ShowDialog() == true)
        {
            vm.Add(dialog.FileName);
        }
    }
}

public partial class GamesWidget : UserControl
{
    private const double CompactHeight = 250;
    private const double TileWidth = 100;

    public GamesWidget()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
        _ = new Controls.DragReorder<Games.GameTile>(List, _ => true, (d, t, after) => ViewModel?.Move(d, t, after));
    }

    private Games.GamesViewModel? ViewModel => DataContext as Games.GamesViewModel;

    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(CompactMeasure.Limit(constraint, CompactHeight));
        return double.IsInfinity(constraint.Height) ? size with { Height = CompactHeight } : size;
    }

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(GamesWidget), new PropertyMetadata(2));

    /// <summary>Columns of game tiles: wider cards get more.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private void Fit()
    {
        if (ActualWidth > 0)
        {
            Columns = Math.Max(2, (int)(ActualWidth / TileWidth));
        }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Games.GameTile tile)
        {
            Games.GamesViewModel.Launch(tile);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = ViewModel?.RefreshAsync(fresh: true);

    private void OnSort(object sender, RoutedEventArgs e) => ViewModel?.CycleSort();

    private void OnShowHidden(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.ShowHidden = !vm.ShowHidden;
        }
    }

    private void OnFavorite(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Games.GameTile tile)
        {
            ViewModel?.ToggleFavorite(tile);
        }
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Games.GameTile tile)
        {
            ViewModel?.ToggleHidden(tile);
        }
    }
}
