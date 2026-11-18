using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Infrastructure;

/// <summary>
/// Loads Explorer thumbnails / icons off the UI thread and turns them into frozen WPF bitmaps.
/// All requests run one by one on a single background STA thread: from thread-pool (MTA) threads the shell
/// returned no icon at all for some programs (notepad.exe, calc.exe on Windows 11), so the tiles fell back to
/// a generic glyph. The thread sleeps while there is nothing to load.
/// </summary>
internal static class ShellImageLoader
{
    private static readonly BlockingCollection<Action> Queue = [];

    static ShellImageLoader()
    {
        var worker = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        {
            IsBackground = true,
            Name = "Shell images",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static async Task<BitmapSource?> LoadAsync(string path, int size, bool iconOnly = false)
    {
        var done = new TaskCompletionSource<ShellImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                done.SetResult(ShellImages.Get(path, size, iconOnly));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                done.SetResult(null);
            }
        });

        var image = await done.Task.ConfigureAwait(true);
        if (image is null)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
