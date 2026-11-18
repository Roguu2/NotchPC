using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Web;

namespace NotchPC.App.Web;

/// <summary>
/// The web panel: a normal (not layered) acrylic window under the notch with WebView2 inside. WebView2 cannot render
/// in the notch's transparent window (airspace), hence a window of its own. Logins and cookies are kept in
/// %LocalAppData%\NotchPC\WebView2.
/// </summary>
public partial class WebPanelWindow
{
    private readonly string _dataFolder;
    private Task? _init;
    private bool _pinned;

    internal WebPanelWindow(string dataFolder)
    {
        _dataFolder = dataFolder;
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (!_pinned && HideOnDeactivate && IsVisible)
            {
                Hide();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_pinned)
            {
                Hide();
            }
        };
    }

    /// <summary>The star button: (title, address) of the page to add as a tile.</summary>
    internal event EventHandler<WebBookmarkRequest>? BookmarkRequested;

    internal bool AllowClose { get; set; }

    internal bool HideOnDeactivate { get; set; } = true;

    internal async Task NavigateAsync(Uri uri)
    {
        Address.Text = uri.AbsoluteUri;
        if (!await EnsureAsync().ConfigureAwait(true))
        {
            return;
        }

        View.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        View.Dispose();
        base.OnClosed(e);
    }

    private Task<bool> EnsureAsync() => EnsureCoreAsync();

    private async Task<bool> EnsureCoreAsync()
    {
        _init ??= InitializeAsync();
        try
        {
            await _init.ConfigureAwait(true);
            return View.CoreWebView2 is not null;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowProblem(Loc.Get("Web.NoRuntime"));
            return false;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Error("WebView2 could not start", ex);
            ShowProblem(Loc.Format("Web.Failed", ex.Message));
            return false;
        }
    }

    private async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder).ConfigureAwait(true);
        await View.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        var core = View.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.SourceChanged += (_, _) =>
        {
            if (!Address.IsKeyboardFocusWithin)
            {
                Address.Text = core.Source;
            }
        };
        core.HistoryChanged += (_, _) =>
        {
            BackButton.IsEnabled = core.CanGoBack;
            ForwardButton.IsEnabled = core.CanGoForward;
        };

        // Links that want a new window open here (a panel has no tabs).
        core.NewWindowRequested += (_, e) =>
        {
            if (WebAddress.IsWeb(e.Uri))
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            }
        };
    }

    private void ShowProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
        View.Visibility = Visibility.Collapsed;
    }

    private void OnToolbarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Toolbar) && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2?.CanGoBack == true)
        {
            View.CoreWebView2.GoBack();
        }
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2?.CanGoForward == true)
        {
            View.CoreWebView2.GoForward();
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => View.CoreWebView2?.Reload();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WebAddress.Resolve(Address.Text) is { } uri)
        {
            e.Handled = true;
            _ = NavigateAsync(uri);
            View.Focus();
        }
    }

    private void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e) => Address.SelectAll();

    private void OnBookmark(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2 is { } core && WebAddress.IsWeb(core.Source))
        {
            BookmarkRequested?.Invoke(this, new WebBookmarkRequest(core.DocumentTitle, core.Source));
        }
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinButton.Appearance = _pinned ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2?.Source is { } source && WebAddress.IsWeb(source))
        {
            Process.Start(new ProcessStartInfo(source) { UseShellExecute = true })?.Dispose();
            Hide();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}

internal sealed record WebBookmarkRequest(string Title, string Url);
