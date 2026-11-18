using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchPC.App.Infrastructure;
using NotchPC.App.Notifications;
using NotchPC.Core.Localization;
using NotchPC.Core.Notifications;
using NotchPC.Core.Updates;

namespace NotchPC.App.Updates;

internal enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Downloaded: installs on restart (button / notification) or when NotchPC is closed.</summary>
    Ready,
    /// <summary>A newer version exists, but this copy is not installed (portable / built from source): link only.</summary>
    Available,
    Failed,
}

/// <summary>
/// Automatic updates like SpotiTube's: looks at this repository's latest GitHub release a minute after start and
/// then every 6 hours, downloads the new installer in the background (checked against GitHub's SHA-256 when given)
/// and installs it silently when the user restarts NotchPC from the notification / settings, or when NotchPC is
/// closed from the tray. Only for the installed app; a portable copy just says a new version exists.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchPC";

    private readonly SettingsService _settings;
    private readonly NotificationHub _hub;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;
    private string? _setupPath;
    private string? _notifiedVersion;

    public UpdateService(SettingsService settings, NotificationHub hub)
    {
        _settings = settings;
        _hub = hub;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NotchPC/{CurrentVersion}");
        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            if (_settings.Current.Updates.Auto)
            {
                await CheckAsync(userAsked: false).ConfigureAwait(true);
            }
        };
        _hub.InstallUpdateRequested += (_, _) => InstallNow();
    }

    public event EventHandler? Changed;

    public static Version CurrentVersion { get; } =
        Releases.Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public ReleaseInfo? Latest { get; private set; }

    public int Progress { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Installed with the installer (not a portable copy or a developer build): can update itself.</summary>
    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string dir)
            {
                return false;
            }

            var here = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            return string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(Path.Combine(here, "Uninstall.exe"));
        }
    }

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchPC", "updates");

    public void Start()
    {
        CleanOldDownloads();
        _timer.Start();
    }

    public async Task CheckAsync(bool userAsked)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading)
        {
            return;
        }

        if (State == UpdateState.Ready && !userAsked)
        {
            return; // already waiting for a restart
        }

        Set(UpdateState.Checking);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Releases.LatestApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, token).ConfigureAwait(true);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Finish(UpdateState.UpToDate); // no release published yet
                return;
            }

            response.EnsureSuccessStatusCode();
            var release = Releases.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(true));
            _settings.Update(s => s.Updates.LastCheck = DateTimeOffset.Now);
            if (release is null || !Releases.IsNewer(release.Version, CurrentVersion))
            {
                Latest = release;
                Finish(UpdateState.UpToDate);
                return;
            }

            Latest = release;
            if (!IsInstalled || release.Setup is null)
            {
                Finish(UpdateState.Available);
                Notify(release, ready: false);
                return;
            }

            await DownloadAsync(release, release.Setup, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Set(UpdateState.Idle);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = ex.Message;
            AppLog.Info($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            Set(UpdateState.Failed);
        }
    }

    /// <summary>Restarts into the new version (installer runs silently and starts NotchPC again).</summary>
    public void InstallNow()
    {
        if (State != UpdateState.Ready || _setupPath is null || !File.Exists(_setupPath))
        {
            return;
        }

        AppLog.Info($"Installing update {Latest?.Version}");
        if (StartInstaller(relaunch: true))
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>Called when the user closes NotchPC: a downloaded update installs now (NotchPC stays closed).</summary>
    public void InstallOnExit()
    {
        if (State == UpdateState.Ready && _settings.Current.Updates.Auto && _setupPath is not null && File.Exists(_setupPath))
        {
            AppLog.Info($"Installing update {Latest?.Version} on exit");
            StartInstaller(relaunch: false);
        }
    }

    public static void OpenReleasePage(ReleaseInfo? release) =>
        Process.Start(new ProcessStartInfo(release?.PageUrl ?? Releases.LatestPage) { UseShellExecute = true })?.Dispose();

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _http.Dispose();
    }

    private async Task DownloadAsync(ReleaseInfo release, ReleaseAsset asset, CancellationToken token)
    {
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, asset.Name);
        if (File.Exists(target) && await VerifyAsync(target, asset, token).ConfigureAwait(true))
        {
            Ready(release, target);
            return;
        }

        Progress = 0;
        Set(UpdateState.Downloading);
        var partial = target + ".partial";
        using (var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(true))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(true);
            await using var file = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(true)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(true);
                done += read;
                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != Progress)
                {
                    Progress = percent;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        if (!await VerifyAsync(partial, asset, token).ConfigureAwait(true))
        {
            File.Delete(partial);
            throw new InvalidDataException(Loc.Get("Updates.BadDownload"));
        }

        File.Move(partial, target, overwrite: true);
        Ready(release, target);
    }

    /// <summary>The file is the one GitHub lists: same size and, when GitHub gives one, the same SHA-256.</summary>
    private static async Task<bool> VerifyAsync(string path, ReleaseAsset asset, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (asset.Size > 0 && info.Length != asset.Size))
        {
            return false;
        }

        if (asset.Sha256 is null)
        {
            return true;
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(true));
        return string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void Ready(ReleaseInfo release, string path)
    {
        _setupPath = path;
        Finish(UpdateState.Ready);
        Notify(release, ready: true);
    }

    private bool StartInstaller(bool relaunch)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_setupPath!, relaunch ? "--update --relaunch" : "--update") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Error = ex.Message;
            AppLog.Error("Starting the update installer failed", ex);
            Set(UpdateState.Failed);
            return false;
        }
    }

    private void Notify(ReleaseInfo release, bool ready)
    {
        var version = release.Version.ToString(3);
        if (_notifiedVersion == version + ready)
        {
            return;
        }

        _notifiedVersion = version + ready;
        if (ready)
        {
            _hub.Post(Loc.Format("Updates.Ready.Title", version), Loc.Get("Updates.Ready.Body"), "", "NotchPC", NotificationTargets.InstallUpdate);
        }
        else
        {
            _hub.Post(Loc.Format("Updates.Available.Title", version), Loc.Get("Updates.Available.Body"), "", "NotchPC", release.PageUrl);
        }
    }

    private static void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                // Installers of this version or older are not needed any more.
                var name = Path.GetFileName(file);
                var v = Releases.ParseVersion(name.Replace("NotchPC-", "", StringComparison.Ordinal).Split('-')[0]);
                if (v is null || !Releases.IsNewer(v, CurrentVersion))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Finish(UpdateState state)
    {
        Error = null;
        Set(state);
    }

    private void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
