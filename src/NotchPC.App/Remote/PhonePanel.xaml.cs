using System.Windows;
using System.Windows.Controls;

namespace NotchPC.App.Remote;

/// <summary>Phone tab of the notch.</summary>
public partial class PhonePanel : UserControl
{
    public PhonePanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Vm?.SetVisible(IsVisible);
    }

    private PhoneViewModel? Vm => DataContext as PhoneViewModel;

    private void OnTurnOn(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.Enabled = true;
        }
    }

    private void OnTurnOff(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.Enabled = false;
        }
    }

    private void OnNewCode(object sender, RoutedEventArgs e) => Vm?.NewCode();
}
