using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace NotchPC.Platform.Startup;

public enum CpuTempSetupResult
{
    Ok,
    /// <summary>The UAC prompt was cancelled.</summary>
    Declined,
    /// <summary>winget (Windows Package Manager) is missing: PawnIO has to be installed by hand.</summary>
    NoWinget,
    DriverFailed,
    TaskFailed,
}

/// <summary>
/// CPU temperature in one click. Windows has no API for it: the temperature sits in the CPU's model-specific
/// registers, which only a signed kernel driver may read. LibreHardwareMonitor uses the free, signed PawnIO driver
/// for that, from a process running as administrator. This installs PawnIO from the official Windows package catalog
/// (winget, package namazso.PawnIO, checked by winget) and creates the scheduled task that starts NotchPC's sensors
/// helper as administrator, both in one elevated step (one UAC prompt).
/// </summary>
public static class CpuTempSetup
{
    public const string WingetId = "namazso.PawnIO";
    public const string ManualPage = "https://github.com/namazso/PawnIO.Setup/releases/latest";

    /// <summary>The PawnIO driver service is registered (readable without admin rights).</summary>
    public static bool IsDriverInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO"));
    }

    public static bool HasWinget() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"));

    /// <summary>Installs PawnIO if needed and (re)creates the elevated sensors task. Blocks up to 10 minutes.</summary>
    public static CpuTempSetupResult Enable(string sensorsExePath)
    {
        if (!IsDriverInstalled() && !HasWinget())
        {
            return CpuTempSetupResult.NoWinget;
        }

        var folder = Path.Combine(Path.GetTempPath(), "NotchPC-cputemp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var xml = Path.Combine(folder, "task.xml");
        var script = Path.Combine(folder, "setup.ps1");
        var log = Path.Combine(folder, "setup.log");
        try
        {
            File.WriteAllText(xml, ElevatedSensorsTask.TaskXml(sensorsExePath), Encoding.Unicode);
            File.WriteAllText(script, Script(xml, log), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return CpuTempSetupResult.DriverFailed;
            }

            p.WaitForExit(10 * 60 * 1000);
            var code = p.HasExited ? p.ExitCode : -1;
            return code switch
            {
                0 => CpuTempSetupResult.Ok,
                3 => CpuTempSetupResult.TaskFailed,
                4 => CpuTempSetupResult.NoWinget,
                _ => CpuTempSetupResult.DriverFailed,
            };
        }
        catch (Win32Exception)
        {
            return CpuTempSetupResult.Declined;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The elevated part. Exit codes: 0 ok, 2 driver not installed, 3 task not created, 4 winget missing.</summary>
    private static string Script(string xmlPath, string logPath) => $$"""
        $ErrorActionPreference = 'Continue'
        $log = '{{logPath.Replace("'", "''", StringComparison.Ordinal)}}'
        function Test-PawnIO { (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO') -or (Test-Path (Join-Path $env:ProgramFiles 'PawnIO')) }
        if (-not (Test-PawnIO)) {
            $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
            if (-not (Test-Path $winget)) { $winget = (Get-Command winget -ErrorAction SilentlyContinue).Source }
            if (-not $winget) { exit 4 }
            & $winget install --id {{WingetId}} --exact --source winget --silent --accept-source-agreements --accept-package-agreements --disable-interactivity *>> $log
        }
        if (-not (Test-PawnIO)) { exit 2 }
        & schtasks.exe /create /tn "{{ElevatedSensorsTask.TaskName}}" /xml '{{xmlPath.Replace("'", "''", StringComparison.Ordinal)}}' /f *>> $log
        if ($LASTEXITCODE -ne 0) { exit 3 }
        exit 0
        """;
}
