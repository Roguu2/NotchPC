using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotchPC.App.Notch;
using NotchPC.Core.Localization;

namespace NotchPC.App.Clipboard;

/// <summary>Clipboard history list: click = copy, hover buttons = paste / pin / delete, search box filters.</summary>
public partial class ClipboardPanel : UserControl
{
    public ClipboardPanel()
    {
        InitializeComponent();
    }

    private ClipboardViewModel? Vm => DataContext as ClipboardViewModel;

    private NotchWindow? Notch => Window.GetWindow(this) as NotchWindow;

    private static ClipItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as ClipItemViewModel;

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || ItemOf(sender) is not { } item)
        {
            return;
        }

        var ok = vm.Service.Copy(item.Entry);
        Notch?.ShowFlash(ok ? "" : "", Loc.Get(ok ? "Flash.Copied" : "Flash.ClipboardBusy"));
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            _ = PasteAsync(item);
        }
    }

    private async Task PasteAsync(ClipItemViewModel item)
    {
        if (Vm is not { } vm || Notch is not { } notch)
        {
            return;
        }

        var target = notch.PasteTarget;
        notch.CollapseNow();
        if (!await vm.Service.PasteAsync(item.Entry, target))
        {
            notch.ShowFlash("", Loc.Get("Flash.PasteFailed"));
        }
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Vm?.Service.SetPinned(item.Entry.Id, !item.Pinned);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Vm?.Service.Remove(item.Entry.Id);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.Service.ClearUnpinned();

    /// <summary>Enter pastes the first result, Esc clears the search (second Esc gives focus back).</summary>
    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Search.Text.Length > 0)
            {
                Search.Text = "";
            }
            else
            {
                Notch?.ReturnFocus();
            }
        }
        else if (e.Key == Key.Enter && Vm is { Items.Count: > 0 } vm)
        {
            e.Handled = true;
            _ = PasteAsync(vm.Items[0]);
        }
    }
}
