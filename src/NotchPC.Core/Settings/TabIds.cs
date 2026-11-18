namespace NotchPC.Core.Settings;

/// <summary>Stable identifiers of notch tabs (stored in settings, never rename).</summary>
public static class TabIds
{
    public const string Music = "music";
    public const string Web = "web";
    public const string Notifications = "notifications";
    public const string Files = "files";
    public const string Clipboard = "clipboard";
    public const string Widgets = "widgets";
    public const string QuickSettings = "quick-settings";
    public const string Apps = "apps";
    public const string Phone = "phone";
    public const string Audio = "audio";
    public const string Settings = "settings";

    public static IReadOnlyList<string> DefaultOrder { get; } =
    [
        Music, Web, Notifications, Files, Clipboard, Widgets, QuickSettings, Apps, Phone, Audio, Settings,
    ];
}
