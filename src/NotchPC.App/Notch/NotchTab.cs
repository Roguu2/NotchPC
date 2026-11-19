using System.ComponentModel;
using System.Runtime.CompilerServices;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;

namespace NotchPC.App.Notch;

/// <summary>One icon in the notch tab bar.</summary>
public sealed class NotchTab : INotifyPropertyChanged
{
    /// <summary>The "more" button: tabs that do not fit the bar are shown as tiles behind it.</summary>
    public const string MoreId = "more";

    private bool _isSelected;

    public NotchTab(string id)
    {
        Id = id;
        Glyph = TabCatalog.GlyphFor(id);
        Stage = TabCatalog.StageFor(id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    /// <summary>Segoe Fluent Icons code point.</summary>
    public string Glyph { get; }

    /// <summary>Stage of PLAN.md in which the tab gets real content.</summary>
    public int Stage { get; }

    public string Label => Loc.Get("Tab." + Id);

    public string ComingSoon => Loc.Format("Placeholder.ComingInStage", Stage);

    /// <summary>The settings gear is separated from the other tabs, as in the reference design.</summary>
    public bool HasSeparatorBefore => Id == TabIds.Settings;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ComingSoon));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Static metadata for tabs (icons and the stage that implements them).</summary>
public static class TabCatalog
{
    public static string GlyphFor(string id) => id switch
    {
        TabIds.Music => "",          // MusicNote
        TabIds.Web => "",            // Globe
        TabIds.Notifications => "",  // Ringer (bell)
        TabIds.Files => "",          // Folder
        TabIds.Clipboard => "",      // Paste (clipboard)
        TabIds.Widgets => "",        // Tiles
        TabIds.QuickSettings => "",  // Equalizer (sliders)
        TabIds.Phone => "",          // CellPhone
        TabIds.Audio => "",          // Headphone
        TabIds.Settings => "",       // Settings (gear)
        TabIds.Search => "",        // Calculator
        NotchTab.MoreId => "",      // More (three dots)
        _ => "",                     // Error
    };

    public static int StageFor(string id) => id switch
    {
        TabIds.Music => 2,
        TabIds.Widgets => 3,
        TabIds.Files or TabIds.Clipboard => 4,
        TabIds.QuickSettings => 5,
        TabIds.Notifications => 6,
        TabIds.Phone => 8,
        _ => 9,
    };
}
