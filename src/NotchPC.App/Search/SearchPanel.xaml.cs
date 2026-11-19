using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NotchPC.App.Search;

/// <summary>Calculator tab of the notch: ↑/↓ choose, Enter copies, Esc clears.</summary>
public partial class SearchPanel : UserControl
{
    public SearchPanel()
    {
        InitializeComponent();
    }

    /// <summary>The text box to give the keyboard to.</summary>
    public Control Input => Box;

    private SearchViewModel? Vm => DataContext as SearchViewModel;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Run();
                e.Handled = true;
                break;
            case Key.Escape when vm.Query.Length > 0:
                vm.Query = "";
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SearchResult r)
        {
            Vm?.Run(r);
        }
    }
}
