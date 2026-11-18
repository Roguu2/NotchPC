using System.IO;
using System.Windows;
using System.Windows.Controls;
using NotchPC.App.Notch;
using NotchPC.Core.Localization;

namespace NotchPC.App.Apps;

/// <summary>Apps tab: Screenshot / Snip / Record and app shortcuts.</summary>
public partial class AppsPanel : UserControl
{
    private bool _busy;

    public AppsPanel()
    {
        InitializeComponent();
    }

    private AppsViewModel? Vm => DataContext as AppsViewModel;

    private NotchWindow? Notch => Window.GetWindow(this) as NotchWindow;

    private async void OnScreenshot(object sender, RoutedEventArgs e) => await CaptureAsync(snip: false);

    private async void OnSnip(object sender, RoutedEventArgs e) => await CaptureAsync(snip: true);

    /// <summary>The notch hides itself first, so it is never in the picture.</summary>
    private async Task CaptureAsync(bool snip)
    {
        if (_busy || Vm is not { } vm || Notch is not { Monitor: { } monitor } notch)
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

            var path = image is null ? null : vm.Capture.Save(image);
            notch.ShowFlash(path is null ? "" : "", Loc.Get(path is null ? "Flash.ScreenshotFailed" : "Flash.ScreenshotSaved"));
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Notch is not { } notch)
        {
            return;
        }

        notch.CollapseNow();
        if (!vm.Capture.ToggleRecording())
        {
            notch.ShowFlash("", Loc.Get("Flash.ShareXMissing"));
        }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppTile tile || Notch is not { } notch)
        {
            return;
        }

        notch.CollapseNow();
        if (!AppsViewModel.Launch(tile))
        {
            notch.ShowFlash("", Loc.Get("Flash.LaunchFailed"));
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppTile tile)
        {
            Vm?.Remove(tile);
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        Notch?.CollapseNow();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Apps.Add.Title"),
            Filter = $"{Loc.Get("Apps.Add.Filter")}|*.exe;*.lnk;*.url;*.appref-ms|{Loc.Get("Apps.Add.AllFiles")}|*.*",
            DereferenceLinks = false,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };
        if (dialog.ShowDialog() == true)
        {
            vm.Add(dialog.FileName);
        }
    }
}
