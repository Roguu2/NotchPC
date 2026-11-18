using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchPC.App.Notifications;

/// <summary>Notifications tab of the notch.</summary>
public partial class NotificationsPanel : UserControl
{
    public NotificationsPanel()
    {
        InitializeComponent();
    }

    private NotificationsViewModel? Vm => DataContext as NotificationsViewModel;

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationItem item)
        {
            Vm?.Dismiss(item);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.ClearAll();

    /// <summary>A click on the row (not on its buttons) opens what the notification is about.</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || (sender as FrameworkElement)?.Tag is not NotificationItem item || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        Vm?.Open(item);
    }

    private void OnOpenInWindows(object sender, RoutedEventArgs e) => Vm?.OpenInWindows();

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
