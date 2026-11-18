using System.Globalization;
using System.IO;
using NotchPC.Core.Settings;

namespace NotchPC.App.Infrastructure;

/// <summary>Tiny append-only log in %AppData%\NotchPC\logs (rotated at 1 MB). Errors only plus lifecycle lines.</summary>
internal static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static readonly string FilePath = Path.Combine(SettingsStore.DefaultDirectory, "logs", "notchpc.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                // Another NotchPC process (e.g. during a restart) may be writing at the same moment: retry briefly.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.AppendAllText(FilePath, line);
                        break;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        Thread.Sleep(15);
                    }
                }
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
