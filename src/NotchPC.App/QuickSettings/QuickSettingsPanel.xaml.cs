using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using NotchPC.App.Notch;

namespace NotchPC.App.QuickSettings;

/// <summary>Quick settings tab of the notch.</summary>
public partial class QuickSettingsPanel : UserControl
{
    public QuickSettingsPanel()
    {
        InitializeComponent();
    }

    private QuickSettingsViewModel? Vm => DataContext as QuickSettingsViewModel;

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
