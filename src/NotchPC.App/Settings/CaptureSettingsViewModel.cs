using System.IO;
using NotchPC.App.Apps;
using NotchPC.App.Clipboard;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;

namespace NotchPC.App.Settings;

/// <summary>The "Clipboard and screenshots" section of the settings window.</summary>
internal sealed class CaptureSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ClipboardService _clipboard;
    private readonly CaptureService _capture;

    public CaptureSettingsViewModel(SettingsService settings, ClipboardService clipboard, CaptureService capture)
    {
        _settings = settings;
        _clipboard = clipboard;
        _capture = capture;
        Loc.CultureChanged += (_, _) => Refresh();
    }

    public static IReadOnlyList<int> LimitOptions { get; } = [25, 50, 100, 200, 500];

    public bool ClipboardEnabled
    {
        get => _settings.Current.Clipboard.Enabled;
        set
        {
            _settings.Update(s => s.Clipboard.Enabled = value);
            Raise();
        }
    }

    public int ClipboardLimit
    {
        get => _settings.Current.Clipboard.Limit;
        set
        {
            _settings.Update(s => s.Clipboard.Limit = value);
            Raise();
        }
    }

    public string ScreenshotFolder => _capture.Folder;

    public string ShareXText => _capture.FindShareX() is { } exe
        ? Loc.Format("Settings.Capture.ShareX.Found", exe)
        : Loc.Get("Settings.Capture.ShareX.Missing");

    public void ClearClipboard() => _clipboard.ClearAll();

    public void SetScreenshotFolder(string folder)
    {
        _settings.Update(s => s.Apps.ScreenshotFolder = string.Equals(folder, CaptureService.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : folder);
        Raise(nameof(ScreenshotFolder));
    }

    public void OpenScreenshotFolder() => CaptureService.OpenFolder(_capture.Folder);

    public void SetShareX(string path)
    {
        if (File.Exists(path))
        {
            _settings.Update(s => s.Apps.ShareXPath = path);
            Raise(nameof(ShareXText));
        }
    }

    /// <summary>Called when the window is shown (ShareX may have been started or installed since).</summary>
    public void Refresh()
    {
        Raise(nameof(ShareXText));
        Raise(nameof(ScreenshotFolder));
    }
}
