using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchPC.App.Web;

/// <summary>Web tab of the notch: address / search box and bookmark tiles; pages open in the web panel window.</summary>
public partial class WebPanel : UserControl
{
    public WebPanel()
    {
        InitializeComponent();
    }

    private WebViewModel? Vm => DataContext as WebViewModel;

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(Address.Text))
        {
            e.Handled = true;
            var text = Address.Text;
            Address.Text = "";
            Vm?.OpenText(text);
        }
        else if (e.Key == Key.Escape)
        {
            Address.Text = "";
        }
    }

    private void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WebTile tile && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            Vm?.Open(tile);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WebTile tile)
        {
            Vm?.Remove(tile);
        }
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
