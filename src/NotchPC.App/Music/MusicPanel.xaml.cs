using System.Windows;
using System.Windows.Controls;

namespace NotchPC.App.Music;

/// <summary>Music tab of the expanded notch (layout follows ref/Notch.jpg).</summary>
public partial class MusicPanel : UserControl
{
    public MusicPanel()
    {
        InitializeComponent();
    }

    private MusicViewModel? ViewModel => DataContext as MusicViewModel;

    private async void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.PlayPauseAsync();
        }
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.NextAsync();
        }
    }

    private async void OnPrevious(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.PreviousAsync();
        }
    }

    private async void OnSeek(object? sender, double fraction)
    {
        if (ViewModel is { } vm)
        {
            await vm.SeekAsync(fraction);
        }
    }

    private async void OnSeekBy(object? sender, double seconds)
    {
        if (ViewModel is { } vm)
        {
            await vm.SeekByAsync(TimeSpan.FromSeconds(seconds));
        }
    }

    private void OnMic(object sender, RoutedEventArgs e) => ViewModel?.ToggleMic();

    private void OnMuteOutput(object sender, RoutedEventArgs e) => ViewModel?.ToggleOutputMute();

    private void OnLyrics(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.LyricsOpen = !vm.LyricsOpen;
        }
    }
}
