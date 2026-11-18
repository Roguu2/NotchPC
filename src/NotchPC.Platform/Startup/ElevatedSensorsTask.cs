using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace NotchPC.Platform.Startup;

/// <summary>
/// Optional Windows scheduled task that starts NotchPC.Sensors with administrator rights (needed, together with
/// the PawnIO driver, for CPU temperature). Creating/removing it shows one UAC prompt; running it later does not.
/// The task has no triggers: it only runs when NotchPC asks for it.
/// </summary>
public static class ElevatedSensorsTask
{
    public const string TaskName = "NotchPC Sensors";

    public static bool Exists() => RunSchtasks($"/query /tn \"{TaskName}\"", elevate: false) == 0;

    /// <summary>Starts the helper through the task (no UAC prompt). False if the task is missing.</summary>
    public static bool TryRun() => RunSchtasks($"/run /tn \"{TaskName}\"", elevate: false) == 0;

    /// <summary>Creates the task (UAC prompt). Returns false if the user declined or it failed.</summary>
    public static bool Create(string sensorsExePath)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"notchpc-sensors-task-{Guid.NewGuid():N}.xml");
        try
        {
            // Task Scheduler XML must be UTF-16.
            File.WriteAllText(xmlPath, TaskXml(sensorsExePath), Encoding.Unicode);
            return RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f", elevate: true) == 0 && Exists();
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Removes the task (UAC prompt).</summary>
    public static bool Delete() => RunSchtasks($"/delete /tn \"{TaskName}\" /f", elevate: true) == 0;

    private static string TaskXml(string exePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>NotchPC hardware sensors (CPU temperature). Started on demand by NotchPC; no triggers.</Description>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
            <Hidden>true</Hidden>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;

    private static int RunSchtasks(string arguments, bool elevate)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : "",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            if (!elevate)
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
            }

            p.WaitForExit(30_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Win32Exception)
        {
            return -1; // UAC declined
        }
    }
}
