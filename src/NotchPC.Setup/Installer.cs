using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace NotchPC.Setup;

/// <summary>
/// Per-user install (no administrator rights): %LocalAppData%\Programs\NotchPC, Start menu (and optional desktop)
/// shortcut, an entry in Settings → Apps for uninstalling. Updating keeps the user's settings.
/// </summary>
internal static class Installer
{
    public const string ExeName = "NotchPC.exe";
    public const string UninstallerName = "Uninstall.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchPC";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string DefaultInstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NotchPC");

    /// <summary>Where the app's package identity manifest lives (fixed by the app, whatever the install folder).</summary>
    private static string IdentityDir => Path.Combine(DefaultInstallDir, "identity");

    /// <summary>The chosen folder: the previous install's (from Windows' app list) or the default one.</summary>
    public static string InstallDir { get; set; } = ExistingInstallDir() ?? DefaultInstallDir;

    /// <summary>Where NotchPC is installed now, or null.</summary>
    public static string? ExistingInstallDir()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
    }

    /// <summary>
    /// A folder the user picked becomes "...\NotchPC" unless it already is one, so installing (and later
    /// uninstalling, which deletes the folder) never mixes NotchPC with other files.
    /// </summary>
    public static string Normalize(string picked)
    {
        var full = Path.GetFullPath(picked.Trim()).TrimEnd('\\');
        return string.Equals(Path.GetFileName(full), "NotchPC", StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, "NotchPC");
    }

    /// <summary>Null when the folder can be written to, else why not (e.g. Program Files needs admin rights).</summary>
    public static string? CheckWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".notchpc-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ex.Message;
        }
    }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NotchPC.lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NotchPC.lnk");

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") is not null;

    public static bool IsInstalled => ExistingInstallDir() is not null;

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    public static IReadOnlyList<Process> RunningApp() =>
        Process.GetProcessesByName("NotchPC").Concat(Process.GetProcessesByName("NotchPC.Sensors")).ToList();

    /// <summary>Closes NotchPC (it lives in the tray, so there is no window to ask nicely).</summary>
    public static void StopApp()
    {
        foreach (var p in RunningApp())
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    public static void Install(bool desktopShortcut, Action<int> progress)
    {
        StopApp();
        Directory.CreateDirectory(InstallDir);
        ClearOldFiles();

        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                             ?? throw new InvalidOperationException("payload missing"))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            var root = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
            for (var i = 0; i < files.Count; i++)
            {
                // The zip holds a "NotchPC\" folder: install its contents.
                var relative = files[i].FullName.Replace('/', '\\');
                var slash = relative.IndexOf('\\');
                relative = slash >= 0 ? relative.Substring(slash + 1) : relative;
                var target = Path.GetFullPath(Path.Combine(InstallDir, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // never write outside the install folder
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                files[i].ExtractToFile(target, overwrite: true);
                progress((i + 1) * 100 / files.Count);
            }
        }

        // The uninstaller is this program (started with --uninstall).
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(InstallDir, UninstallerName), overwrite: true);

        var exe = Path.Combine(InstallDir, ExeName);
        Shortcut(StartMenuShortcut, exe);
        if (desktopShortcut)
        {
            Shortcut(DesktopShortcut, exe);
        }
        else if (File.Exists(DesktopShortcut))
        {
            File.Delete(DesktopShortcut);
        }

        Register(exe);
    }

    /// <summary>Starts NotchPC; the autostart choice (if any) is handed to it (it owns that setting).</summary>
    public static void Launch(bool? autostart) =>
        Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, ExeName), autostart is null ? "" : autostart.Value ? "--autostart on" : "--autostart off")
        {
            UseShellExecute = false,
            WorkingDirectory = InstallDir,
        })?.Dispose();

    /// <summary>Removes the program; with <paramref name="removeData"/> also settings, clipboard history, web logins.</summary>
    public static void Uninstall(bool removeData)
    {
        StopApp();

        // Windows notifications identity (registered from NotchPC's settings) and the optional elevated sensors task.
        RunHidden("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-AppxPackage -Name NotchPC | Remove-AppxPackage\"");
        RunHidden("schtasks.exe", "/delete /tn \"NotchPC Sensors\" /f");
        TryDelete(IdentityDir);

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            run?.DeleteValue("NotchPC", throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        foreach (var link in new[] { StartMenuShortcut, DesktopShortcut })
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
        }

        if (removeData)
        {
            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchPC"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchPC"),
                     })
            {
                TryDelete(dir);
            }
        }

        // This uninstaller runs from the install folder: delete the folder a moment after it exits. Only a folder
        // that really is a NotchPC install (never whatever folder this file happens to be in).
        if (string.Equals(Path.GetFileName(InstallDir.TrimEnd('\\')), "NotchPC", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(InstallDir, ExeName)))
        {
            RunHidden("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"", wait: false);
        }
    }

    private static void ClearOldFiles()
    {
        if (!File.Exists(Path.Combine(InstallDir, ExeName)))
        {
            return; // not a previous install: never delete anything there
        }

        foreach (var entry in new DirectoryInfo(InstallDir).EnumerateFileSystemInfos())
        {
            // "identity" holds the Windows notifications registration: keep it across updates.
            if (string.Equals(entry.Name, "identity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry is DirectoryInfo d)
            {
                TryDelete(d.FullName);
            }
            else
            {
                try
                {
                    entry.Delete();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Shortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell missing");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = "NotchPC";
        link.Save();
    }

    private static void Register(string exe)
    {
        var size = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "NotchPC");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "NotchPC");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{Path.Combine(InstallDir, UninstallerName)}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
    }

    private static void RunHidden(string file, string arguments, bool wait = true)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (wait)
            {
                p?.WaitForExit(15000);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
