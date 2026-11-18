using System.Windows;
using System.Windows.Controls;

namespace NotchPC.App.Audio;

/// <summary>Audio tab of the notch: output device and the per-app volume mixer.</summary>
public partial class AudioPanel : UserControl
{
    public AudioPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Vm?.SetVisible(IsVisible);
    }

    private AudioMixerViewModel? Vm => DataContext as AudioMixerViewModel;

    private void OnSelectDevice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OutputDeviceRow row)
        {
            Vm?.SelectDevice(row);
        }
    }

    private void OnToggleMute(object sender, RoutedEventArgs e) => Vm?.ToggleMute();

    private void OnToggleAppMute(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MixerApp app)
        {
            Vm?.ToggleAppMute(app);
        }
    }
}
