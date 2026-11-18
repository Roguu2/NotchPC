using NotchPC.App.Infrastructure;
using NotchPC.App.Updates;
using NotchPC.Core.Localization;

namespace NotchPC.App.Settings;

/// <summary>The "Updates" part of the settings: on/off, state, check now, install now.</summary>
internal sealed class UpdatesViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly UpdateService _updates;

    public UpdatesViewModel(SettingsService settings, UpdateService updates)
    {
        _settings = settings;
        _updates = updates;
        _updates.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RaiseAll);
        Loc.CultureChanged += (_, _) => RaiseAll();
    }

    public bool Auto
    {
        get => _settings.Current.Updates.Auto;
        set
        {
            _settings.Update(s => s.Updates.Auto = value);
            Raise(nameof(Auto));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Bound from XAML.")]
    public string VersionText => Loc.Format("Updates.Current", UpdateService.CurrentVersion.ToString(3));

    public string StatusText
    {
        get
        {
            var latest = _updates.Latest?.Version.ToString(3) ?? "";
            var checkedAt = _settings.Current.Updates.LastCheck is { } t ? Loc.Format("Updates.CheckedAt", t.ToLocalTime().ToString("g", Loc.Culture)) : "";
            return _updates.State switch
            {
                UpdateState.Checking => Loc.Get("Updates.Checking"),
                UpdateState.Downloading => Loc.Format("Updates.Downloading", latest, _updates.Progress),
                UpdateState.Ready => Loc.Format("Updates.ReadyStatus", latest),
                UpdateState.Available => Loc.Format("Updates.AvailableStatus", latest),
                UpdateState.UpToDate => Loc.Get("Updates.UpToDate") + checkedAt,
                UpdateState.Failed => Loc.Format("Updates.Failed", _updates.Error ?? ""),
                _ => UpdateService.IsInstalled ? Loc.Get("Updates.Idle") + checkedAt : Loc.Get("Updates.NotInstalled"),
            };
        }
    }

    public bool CanCheck => _updates.State is not (UpdateState.Checking or UpdateState.Downloading);

    public bool CanInstall => _updates.State == UpdateState.Ready;

    public bool HasRelease => _updates.Latest is not null;

    public Task CheckAsync() => _updates.CheckAsync(userAsked: true);

    public void InstallNow() => _updates.InstallNow();

    public void OpenReleasePage() => UpdateService.OpenReleasePage(_updates.Latest);

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(Auto), nameof(VersionText), nameof(StatusText), nameof(CanCheck), nameof(CanInstall), nameof(HasRelease) })
        {
            Raise(name);
        }
    }
}
