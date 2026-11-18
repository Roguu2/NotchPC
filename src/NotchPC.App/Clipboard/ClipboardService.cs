using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Clipboard;
using NotchPC.Platform.Security;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Clipboard;

/// <summary>
/// Clipboard history: listens for clipboard changes (WM_CLIPBOARDUPDATE, no polling), keeps the history encrypted
/// on disk (DPAPI, current Windows user), skips content that password managers mark as private, and can put an
/// entry back on the clipboard or paste it into the previous app.
/// </summary>
internal sealed class ClipboardService : IDisposable
{
    private const int MaxTextLength = 1_000_000;
    private const int MaxImageBytes = 15 * 1024 * 1024;

    // Formats set by password managers / apps that ask clipboard tools not to keep the content.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    private readonly SettingsService _settings;
    private readonly ClipboardHistory _history;
    private readonly ClipboardStore _store;
    private readonly HwndSource _window;
    private readonly DispatcherTimer _read;
    private readonly DispatcherTimer _save;
    private int _readAttempts;
    private bool _listening;

    // Our own Copy() also raises WM_CLIPBOARDUPDATE: that entry is only moved to the top, not read again
    // (re-encoding an image would give a different fingerprint and a duplicate).
    private string? _selfCopyHash;
    private DateTimeOffset _selfCopyUntil;

    public ClipboardService(SettingsService settings, string directory)
    {
        _settings = settings;
        _history = new ClipboardHistory(settings.Current.Clipboard.Limit);
        _store = new ClipboardStore(directory, Dpapi.ProtectBytes, Dpapi.UnprotectBytes);
        _history.Load(_store.LoadIndex());

        // Message-only window that receives WM_CLIPBOARDUPDATE.
        _window = new HwndSource(new HwndSourceParameters("NotchPC clipboard") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);

        // The clipboard can be briefly locked by the app that just wrote it: read a moment later, retry a few times.
        _read = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _read.Tick += (_, _) => ReadClipboard();
        _save = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            SaveIndex();
        };

        _settings.Changed += (_, _) => Apply();
        Apply();
    }

    public event EventHandler? Changed;

    public bool Enabled => _settings.Current.Clipboard.Enabled;

    public IReadOnlyList<ClipEntry> View(string? query) => _history.View(query);

    public byte[]? LoadImage(Guid id) => _store.LoadImage(id);

    public void SetPinned(Guid id, bool pinned)
    {
        _history.SetPinned(id, pinned);
        Persist();
    }

    public void Remove(Guid id)
    {
        if (_history.Remove(id) is { Kind: ClipKind.Image } e)
        {
            _store.DeleteImage(e.Id);
        }

        Persist();
    }

    /// <summary>Forgets everything, pinned entries included, and deletes the files.</summary>
    public void ClearAll()
    {
        _history.Load([]);
        _save.Stop();
        try
        {
            _store.DeleteAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Deleting clipboard history failed", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearUnpinned()
    {
        foreach (var e in _history.ClearUnpinned().Where(e => e.Kind == ClipKind.Image))
        {
            _store.DeleteImage(e.Id);
        }

        Persist();
    }

    /// <summary>Puts the entry on the clipboard. Returns false if the clipboard was busy or the image is gone.</summary>
    public bool Copy(ClipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _selfCopyHash = entry.Hash;
        _selfCopyUntil = DateTimeOffset.Now.AddSeconds(2);
        try
        {
            switch (entry.Kind)
            {
                case ClipKind.Text:
                    System.Windows.Clipboard.SetText(entry.Text ?? "");
                    break;
                case ClipKind.Files:
                    var list = new StringCollection();
                    list.AddRange((entry.Files ?? []).ToArray());
                    System.Windows.Clipboard.SetFileDropList(list);
                    break;
                case ClipKind.Image:
                    if (_store.LoadImage(entry.Id) is not { } png)
                    {
                        return false;
                    }

                    using (var ms = new MemoryStream(png))
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = ms;
                        image.EndInit();
                        image.Freeze();
                        System.Windows.Clipboard.SetImage(image);
                    }

                    break;
            }

            return true;
        }
        catch (COMException ex)
        {
            AppLog.Error("Clipboard is busy", ex);
            return false;
        }
    }

    /// <summary>Copies the entry and presses Ctrl+V in <paramref name="target"/> (the app the user was in).</summary>
    public async Task<bool> PasteAsync(ClipEntry entry, IntPtr target)
    {
        if (!Copy(entry))
        {
            return false;
        }

        if (target != IntPtr.Zero && target != WindowHelper.Foreground())
        {
            WindowHelper.SetForeground(target);
            await Task.Delay(120);
        }

        await Task.Delay(40);
        return InputHelper.SendCtrlV();
    }

    public void Dispose()
    {
        if (_listening)
        {
            ClipboardListener.Stop(_window.Handle);
        }

        _read.Stop();
        if (_save.IsEnabled)
        {
            _save.Stop();
            SaveIndex();
        }

        _window.Dispose();
    }

    private static bool HasFormat(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format);
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>True if the copying app asked clipboard managers not to keep this (e.g. a password).</summary>
    private static bool IsPrivate(IDataObject data)
    {
        if (HasFormat(data, ExcludeFormat) || HasFormat(data, ViewerIgnoreFormat))
        {
            return true;
        }

        if (HasFormat(data, HistoryFormat) && data.GetData(HistoryFormat) is MemoryStream ms && ms.Length >= 4)
        {
            var bytes = ms.ToArray();
            return BitConverter.ToInt32(bytes, 0) == 0;
        }

        return false;
    }

    private void Apply()
    {
        _history.Limit = _settings.Current.Clipboard.Limit;
        var want = _settings.Current.Clipboard.Enabled;
        if (want && !_listening)
        {
            _listening = ClipboardListener.Start(_window.Handle);
        }
        else if (!want && _listening)
        {
            ClipboardListener.Stop(_window.Handle);
            _listening = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ClipboardListener.IsClipboardUpdate(msg) && _listening)
        {
            _readAttempts = 0;
            _read.Stop();
            _read.Start();
        }

        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        _read.Stop();
        if (_selfCopyHash is { } own && DateTimeOffset.Now < _selfCopyUntil)
        {
            _selfCopyHash = null;
            if (_history.Items.Any(e => e.Hash == own))
            {
                _history.Add(new ClipEntry { Hash = own, At = DateTimeOffset.Now });
                Persist();
            }

            return;
        }

        ClipEntry? entry;
        byte[]? png = null;
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null || IsPrivate(data))
            {
                return;
            }

            var now = DateTimeOffset.Now;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                entry = new ClipEntry { Kind = ClipKind.Files, Files = files, At = now, Hash = ClipEntry.HashOf(ClipKind.Files, null, files, []) };
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                if (text.Length > MaxTextLength)
                {
                    text = text[..MaxTextLength];
                }

                entry = new ClipEntry { Kind = ClipKind.Text, Text = text, At = now, Hash = ClipEntry.HashOf(ClipKind.Text, text, null, []) };
            }
            else if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                if (ms.Length > MaxImageBytes)
                {
                    return;
                }

                png = ms.ToArray();
                entry = new ClipEntry
                {
                    Kind = ClipKind.Image,
                    ImageWidth = image.PixelWidth,
                    ImageHeight = image.PixelHeight,
                    At = now,
                    Hash = ClipEntry.HashOf(ClipKind.Image, null, null, png),
                };
            }
            else
            {
                return;
            }
        }
        catch (COMException)
        {
            if (++_readAttempts < 4)
            {
                _read.Start(); // clipboard still locked by the other app
            }

            return;
        }
        catch (Exception ex) when (ex is ExternalException or NotSupportedException or InvalidOperationException or OutOfMemoryException)
        {
            AppLog.Error("Reading the clipboard failed", ex);
            return;
        }

        var removed = _history.Add(entry);
        if (png is not null && _history.Items[0].Id == entry.Id)
        {
            _store.SaveImage(entry.Id, png);
        }

        foreach (var r in removed.Where(r => r.Kind == ClipKind.Image))
        {
            _store.DeleteImage(r.Id);
        }

        Persist();
    }

    private void Persist()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        _save.Stop();
        _save.Start();
    }

    private void SaveIndex()
    {
        try
        {
            _store.SaveIndex(_history.Items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("Saving clipboard history failed", ex);
        }
    }
}
