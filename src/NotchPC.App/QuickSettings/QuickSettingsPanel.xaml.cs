using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using NotchPC.App.Apps;
using NotchPC.App.Notch;
using NotchPC.Core.Localization;

namespace NotchPC.App.QuickSettings;

/// <summary>Quick settings tab of the notch.</summary>
public partial class QuickSettingsPanel : UserControl
{
    public QuickSettingsPanel()
    {
        InitializeComponent();
    }

    private bool _busy;

    /// <summary>Screenshots, snips and recordings (set by the notch).</summary>
    internal CaptureService? Capture { get; set; }

    private QuickSettingsViewModel? Vm => DataContext as QuickSettingsViewModel;

    private async void OnScreenshot(object sender, RoutedEventArgs e) => await CaptureAsync(snip: false);

    private async void OnSnip(object sender, RoutedEventArgs e) => await CaptureAsync(snip: true);

    /// <summary>The notch hides itself first, so it is never in the picture.</summary>
    private async Task CaptureAsync(bool snip)
    {
        if (_busy || Capture is not { } capture || Notch is not { Monitor: { } monitor } notch)
        {
            return;
        }

        _busy = true;
        try
        {
            System.Windows.Media.Imaging.BitmapSource? image;
            await notch.HideForCaptureAsync();
            try
            {
                image = CaptureService.Capture(monitor);
                if (snip && image is not null)
                {
                    image = await new SnipWindow(image, monitor).PickAsync();
                    if (image is null)
                    {
                        return; // cancelled
                    }
                }
            }
            finally
            {
                notch.EndCapture();
            }

            var path = image is null ? null : capture.Save(image);
            notch.ShowFlash(path is null ? "\uE7BA" : "\uE722", Loc.Get(path is null ? "Flash.ScreenshotFailed" : "Flash.ScreenshotSaved"));
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (Capture is not { } capture || Notch is not { } notch)
        {
            return;
        }

        notch.CollapseNow();
        if (!capture.ToggleRecording())
        {
            notch.ShowFlash("", Loc.Get("Flash.ShareXMissing"));
        }
    }

    private NotchWindow? Notch => Window.GetWindow(this) as NotchWindow;

    private void OnToggleMute(object sender, RoutedEventArgs e) => Vm?.ToggleOutputMute();

    private void OnShowDevices(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.ShowDevices = !vm.ShowDevices;
        }
    }

    private void OnHideDevices(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.ShowDevices = false;
        }
    }

    private void OnSelectDevice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AudioDeviceItem item)
        {
            Vm?.SelectDevice(item);
        }
    }

    /// <summary>The network tile only shows the state; a click opens the Windows network settings.</summary>
    private void OnNetwork(object sender, RoutedEventArgs e)
    {
        // The click flipped the toggle; show the real state again.
        if (sender is ToggleButton tile)
        {
            BindingOperations.GetBindingExpression(tile, ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }

        Notch?.CollapseNow();
        QuickSettingsViewModel.OpenSettings("ms-settings:network");
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string uri)
        {
            Notch?.CollapseNow();
            QuickSettingsViewModel.OpenSettings(uri);
        }
    }

    private void OnLock(object sender, RoutedEventArgs e)
    {
        Notch?.CollapseNow();
        QuickSettingsViewModel.Lock();
    }
}
