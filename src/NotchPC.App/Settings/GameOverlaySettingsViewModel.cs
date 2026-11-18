using System.Collections.ObjectModel;
using NotchPC.App.Games;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Games;
using NotchPC.Core.Localization;

namespace NotchPC.App.Settings;

/// <summary>The "Game overlay" section of the settings window.</summary>
internal sealed class GameOverlaySettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly GameOverlayController _overlay;

    public GameOverlaySettingsViewModel(SettingsService settings, GameOverlayController overlay)
    {
        _settings = settings;
        _overlay = overlay;
        BuildOptions();
        RebuildLists();
        Loc.CultureChanged += (_, _) =>
        {
            BuildOptions();
            Raise(nameof(PositionOptions));
            Raise(nameof(HotkeyOptions));
            RaiseAll();
        };
        _overlay.StatusChanged += (_, _) => RaiseAll();
        _settings.Changed += (_, _) =>
        {
            RebuildLists(); // the shortcut can add a game while this window is open
            RaiseAll();
        };
    }

    public IReadOnlyList<SelectOption> PositionOptions { get; private set; } = [];

    public IReadOnlyList<SelectOption> HotkeyOptions { get; private set; } = [];

    public ObservableCollection<string> Games { get; } = [];

    public ObservableCollection<string> Ignored { get; } = [];

    public bool HasGames => Games.Count > 0;

    public bool Enabled
    {
        get => S.Enabled;
        set => _settings.Update(s => s.GameOverlay.Enabled = value);
    }

    public bool AutoFullscreen
    {
        get => S.AutoFullscreen;
        set => _settings.Update(s => s.GameOverlay.AutoFullscreen = value);
    }

    public bool ShowClock
    {
        get => S.ShowClock;
        set => _settings.Update(s => s.GameOverlay.ShowClock = value);
    }

    public string Position
    {
        get => S.Position;
        set => _settings.Update(s => s.GameOverlay.Position = OverlayPositions.Normalize(value));
    }

    public string Hotkey
    {
        get => S.Hotkey;
        set => _settings.Update(s => s.GameOverlay.Hotkey = OverlayHotkeys.Normalize(value));
    }

    /// <summary>Under the shortcut: how it works, or that another app took the combination.</summary>
    public string HotkeyHint => !_overlay.HotkeyWorks
        ? Loc.Get("Settings.Overlay.Hotkey.Taken")
        : Loc.Get("Settings.Overlay.Hotkey.Hint");

    public string FpsStatus => _overlay.FpsStatus ?? Loc.Get("Settings.Overlay.Fps.Hint");

    private Core.Settings.GameOverlaySettings S => _settings.Current.GameOverlay;

    public void Preview() => _overlay.Preview();

    public void RemoveGame(string name) =>
        _settings.Update(s => s.GameOverlay.Games = s.GameOverlay.Games.Where(g => !string.Equals(g, name, StringComparison.OrdinalIgnoreCase)).ToList());

    public void RemoveIgnored(string name) =>
        _settings.Update(s => s.GameOverlay.Ignored = s.GameOverlay.Ignored.Where(g => !string.Equals(g, name, StringComparison.OrdinalIgnoreCase)).ToList());

    public void ResetIgnored() => _settings.Update(s => s.GameOverlay.Ignored = [.. GameDetection.DefaultIgnored]);

    private void BuildOptions()
    {
        PositionOptions = OverlayPositions.All.Select(p => new SelectOption(p, Loc.Get("Settings.Overlay.Position." + p))).ToList();
        HotkeyOptions = OverlayHotkeys.All
            .Select(h => new SelectOption(h, h == OverlayHotkeys.None ? Loc.Get("Settings.Overlay.Hotkey.None") : OverlayHotkeys.Display(h)))
            .ToList();
    }

    private void RebuildLists()
    {
        Sync(Games, S.Games);
        Sync(Ignored, S.Ignored);
    }

    private static void Sync(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        var sorted = source.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (target.SequenceEqual(sorted))
        {
            return;
        }

        target.Clear();
        foreach (var name in sorted)
        {
            target.Add(name);
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Enabled), nameof(AutoFullscreen), nameof(ShowClock), nameof(Position), nameof(Hotkey),
                     nameof(HotkeyHint), nameof(FpsStatus), nameof(HasGames),
                 })
        {
            Raise(name);
        }
    }
}
