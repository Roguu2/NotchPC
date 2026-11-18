using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;

namespace NotchPC.App.Settings;

/// <summary>Settings window. Created once; closing only hides it.</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;

    internal SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Nav.SelectedIndex = 0;
        _viewModel.Settings.Changed += (_, _) => Dispatcher.BeginInvoke(_viewModel.RefreshNotchPosition);
    }

    /// <summary>Opens a category (e.g. from the tray or a notification: "Notify", "Overlay"…).</summary>
    internal void ShowPage(string name)
    {
        foreach (var item in Nav.Items.OfType<System.Windows.Controls.ListBoxItem>())
        {
            if ((string)item.Tag == name)
            {
                Nav.SelectedItem = item;
            }
        }
    }

    private void OnNavChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var name = (Nav.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as string ?? "General";
        foreach (var page in new FrameworkElement[] { PageGeneral, PageNotch, PageMusic, PageWidgets, PageCapture, PageNotify, PageOverlay, PagePhone, PageAbout })
        {
            page.Visibility = page.Name == "Page" + name ? Visibility.Visible : Visibility.Collapsed;
        }

        PageScroll?.ScrollToTop();
    }

    private void OnCenterNotch(object sender, RoutedEventArgs e) => _viewModel.CenterNotch();

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await _viewModel.Updates.CheckAsync();

    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _viewModel.Updates.InstallNow();

    private void OnOpenRelease(object sender, RoutedEventArgs e) => _viewModel.Updates.OpenReleasePage();

    public bool AllowClose { get; set; }

    public void ShowAndActivate()
    {
        _viewModel.RefreshMonitors();
        _viewModel.RefreshLyricsMoved();
        _ = _viewModel.Widgets.RefreshCpuTempAsync();
        _viewModel.Capture.Refresh();
        _viewModel.Notify.Refresh();
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnMonitorDropDownOpened(object? sender, EventArgs e) => _viewModel.RefreshMonitors();

    private void OnResetLyricsPosition(object sender, RoutedEventArgs e) => _viewModel.ResetLyricsPosition();

    private void OnResetWidgetPositions(object sender, RoutedEventArgs e) => _viewModel.Widgets.ResetPositions();

    private async void OnSearchCity(object sender, RoutedEventArgs e) => await _viewModel.Widgets.SearchCityAsync();

    private async void OnCityKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await _viewModel.Widgets.SearchCityAsync();
        }
    }

    private void OnSaveIcs(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Widgets.SaveCalendarLink(IcsBox.Password))
        {
            IcsBox.Clear(); // the secret link is not kept in the window
        }
    }

    private void OnClearIcs(object sender, RoutedEventArgs e) => _viewModel.Widgets.ClearCalendarLink();

    private async void OnEnableCpuTemp(object sender, RoutedEventArgs e) => await _viewModel.Widgets.SetCpuTemperatureAsync(enable: true);

    private async void OnDisableCpuTemp(object sender, RoutedEventArgs e) => await _viewModel.Widgets.SetCpuTemperatureAsync(enable: false);

    private void OnClearClipboard(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this, NotchPC.Core.Localization.Loc.Get("Settings.Capture.Clear.Confirm"),
            NotchPC.Core.Localization.Loc.Get("App.Name"), System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (answer == System.Windows.MessageBoxResult.Yes)
        {
            _viewModel.Capture.ClearClipboard();
        }
    }

    private void OnPickScreenshotFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _viewModel.Capture.ScreenshotFolder };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.Capture.SetScreenshotFolder(dialog.FolderName);
        }
    }

    private void OnOpenScreenshotFolder(object sender, RoutedEventArgs e) => _viewModel.Capture.OpenScreenshotFolder();

    private void OnPickShareX(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ShareX.exe|ShareX.exe|*.exe|*.exe" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.Capture.SetShareX(dialog.FileName);
        }
    }

    private void OnSampleNotification(object sender, RoutedEventArgs e) => _viewModel.Notify.ShowSample();

    private void OnOverlayPreview(object sender, RoutedEventArgs e) => _viewModel.Overlay.Preview();

    private void OnRemoveOverlayGame(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name)
        {
            _viewModel.Overlay.RemoveGame(name);
        }
    }

    private void OnRemoveOverlayIgnored(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name)
        {
            _viewModel.Overlay.RemoveIgnored(name);
        }
    }

    private void OnResetOverlayIgnored(object sender, RoutedEventArgs e) => _viewModel.Overlay.ResetIgnored();

    private void OnUnpairDevice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeviceRow row)
        {
            _viewModel.Remote.Remove(row);
        }
    }

    private void OnPlayNotificationSound(object sender, RoutedEventArgs e) => _viewModel.Notify.PlaySound();

    private async void OnRegisterIdentity(object sender, RoutedEventArgs e) => await _viewModel.Notify.RegisterAsync();

    private async void OnUnregisterIdentity(object sender, RoutedEventArgs e) => await _viewModel.Notify.UnregisterAsync();

    private void OnRestartApp(object sender, RoutedEventArgs e) => ((App)System.Windows.Application.Current).Restart();

    private void OnOpenDeveloperSettings(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:developers") { UseShellExecute = true })?.Dispose();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = _viewModel.SettingsFolder;
        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }
}
