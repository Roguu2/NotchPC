using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Layout;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Apps;

/// <summary>Screenshots (whole monitor or a selected area), saving and copying them, and ShareX for recording.</summary>
internal sealed class CaptureService(SettingsService settings)
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public string Folder =>
        string.IsNullOrWhiteSpace(settings.Current.Apps.ScreenshotFolder) ? DefaultFolder : settings.Current.Apps.ScreenshotFolder;

    /// <summary>Captures the given monitor (physical pixels) as a frozen bitmap.</summary>
    public static BitmapSource? Capture(MonitorDescriptor monitor)
    {
        var image = ScreenCapture.Capture(monitor.Bounds);
        if (image is null)
        {
            return null;
        }

        var dpi = 96 * monitor.Scale;
        var bitmap = BitmapSource.Create(image.Width, image.Height, dpi, dpi, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Saves as PNG in the screenshot folder and puts the image on the clipboard. Returns the file path.</summary>
    public string? Save(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        string path;
        try
        {
            Directory.CreateDirectory(Folder);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            path = Path.Combine(Folder, $"NotchPC {stamp}.png");
            for (var i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(Folder, $"NotchPC {stamp} ({i}).png");
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Error("Saving the screenshot failed", ex);
            return null;
        }

        try
        {
            System.Windows.Clipboard.SetImage(bitmap);
        }
        catch (COMException ex)
        {
            AppLog.Error("Copying the screenshot failed", ex);
        }

        return path;
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Opening the screenshot folder failed", ex);
        }
    }

    /// <summary>ShareX.exe: the one chosen in settings, a running ShareX, or a normal installation.</summary>
    public string? FindShareX()
    {
        var chosen = settings.Current.Apps.ShareXPath;
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen))
        {
            return chosen;
        }

        foreach (var p in Process.GetProcessesByName("ShareX"))
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName is { } running && File.Exists(running))
                    {
                        return running;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Elevated ShareX: path not readable, try the other places.
                }
            }
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ShareX.exe");
            if (key?.GetValue(null) is string registered && File.Exists(registered.Trim('"')))
            {
                return registered.Trim('"');
            }
        }

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ShareX", "ShareX.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>
    /// Starts or stops ShareX's screen recorder (the same as its "Screen recording" hotkey: ShareX asks for an area
    /// first; a second call stops the recording). Returns false when ShareX was not found.
    /// </summary>
    public bool ToggleRecording()
    {
        if (FindShareX() is not { } exe)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, "-ScreenRecorder") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Starting ShareX failed", ex);
            return false;
        }
    }
}
