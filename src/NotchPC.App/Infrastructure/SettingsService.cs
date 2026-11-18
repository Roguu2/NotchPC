using System.IO;
using System.Windows.Threading;
using NotchPC.Core.Settings;

namespace NotchPC.App.Infrastructure;

/// <summary>Owns the live <see cref="AppSettings"/>; changes are announced at once and written to disk shortly after.</summary>
internal sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _saveTimer;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = store.Load();
        // Debounce: dragging a slider fires dozens of changes, only the last one is written.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public event EventHandler? Changed;

    public AppSettings Current { get; }

    public string FilePath => _store.FilePath;

    public void Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            SaveNow();
        }
    }

    private void SaveNow()
    {
        try
        {
            _store.Save(Current);
        }
        catch (IOException ex)
        {
            AppLog.Error("Saving settings failed", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("Saving settings failed", ex);
        }
    }
}
