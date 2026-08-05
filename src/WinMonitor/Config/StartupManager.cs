using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using WinMonitor.Localization;

namespace WinMonitor.Config;

/// <summary>
/// Registers/unregisters autostart. Two mechanisms:
/// - Elevated, installed (non-portable): Task Scheduler task "WinMonitor"
///   (RunLevel Highest — starting at logon then needs no UAC prompt; native logon delay).
/// - Non-elevated or portable: HKCU\...\Run value; the startup delay is passed as
///   "--delay N" and Program.cs sleeps before building the UI.
/// Apply() is idempotent and always removes the mechanism that is not in use, so
/// toggling elevation or portable mode never leaves a duplicate registration behind.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "WinMonitor";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int ProcessTimeoutMs = 15_000;

    /// <summary>
    /// Sync registration with config. Throws <see cref="InvalidOperationException"/>
    /// with a localized message only when registration was requested and genuinely
    /// failed; removal failures are swallowed (best effort).
    /// </summary>
    public static void Apply(AppConfig config)
    {
        if (!config.StartWithWindows)
        {
            DeleteScheduledTask();
            DeleteRunValue();
            return;
        }

        int delay = Math.Clamp(config.StartupDelaySeconds, 0, 300);
        if (IsElevated() && !ConfigStore.IsPortable)
        {
            // Create the task before removing the Run value: if /Create throws, the
            // existing Run-key registration keeps working.
            RegisterScheduledTask(delay);
            DeleteRunValue();
        }
        else
        {
            // schtasks /Delete fails without elevation if the task exists — swallowed inside.
            DeleteScheduledTask();
            RegisterRunValue(delay);
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(TaskName) is not null) return true;
        }
        catch
        {
            // Registry unreadable; fall through to the scheduled-task check.
        }

        try
        {
            return RunHidden("schtasks.exe", "/Query /TN " + TaskName, out _) == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- scheduled task ----------

    private static void RegisterScheduledTask(int delaySeconds)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), "WinMonitor-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // schtasks expects unicode XML: UTF-16 LE with BOM, matching the declaration.
            File.WriteAllText(xmlPath, BuildTaskXml(delaySeconds), Encoding.Unicode);

            int exit = RunHidden("schtasks.exe", "/Create /TN " + TaskName + " /XML \"" + xmlPath + "\" /F", out string output);
            if (exit != 0)
                throw new InvalidOperationException(Loc.F("startup.register_failed", "schtasks exit " + exit + ": " + output.Trim()));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(Loc.F("startup.register_failed", ex.Message), ex);
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static void DeleteScheduledTask()
    {
        try { RunHidden("schtasks.exe", "/Delete /TN " + TaskName + " /F", out _); }
        catch { }
    }

    // The schtasks /Create CLI cannot express a logon delay, so the task is defined via
    // XML. Element order inside LogonTrigger (Enabled, UserId, Delay) matches the Task
    // Scheduler schema sequence — reordering makes the service reject the definition.
    private static string BuildTaskXml(int delaySeconds)
    {
        (string command, string argsPrefix) = LaunchCommand();
        string exe = XmlEscape(command);
        string arguments = XmlEscape(argsPrefix + "--minimized");
        string user = XmlEscape(CurrentUserName());
        string description = XmlEscape(Loc.T("startup.task_description"));
        string delay = delaySeconds > 0
            ? "\r\n      <Delay>PT" + delaySeconds.ToString(CultureInfo.InvariantCulture) + "S</Delay>"
            : "";

        return
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>{description}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>{delay}
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>{arguments}</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    // ---------- HKCU Run value ----------

    private static void RegisterRunValue(int delaySeconds)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                throw new InvalidOperationException(Loc.F("startup.register_failed", "HKCU\\" + RunKeyPath));

            (string command, string argsPrefix) = LaunchCommand();
            string value = "\"" + command + "\" " + argsPrefix + "--minimized";
            if (delaySeconds > 0)
                value += " --delay " + delaySeconds.ToString(CultureInfo.InvariantCulture);
            key.SetValue(TaskName, value, RegistryValueKind.String);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(Loc.F("startup.register_failed", ex.Message), ex);
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ---------- helpers ----------

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string ExePath()
    {
        string? path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? Application.ExecutablePath : path;
    }

    // Framework-dependent launches ("dotnet WinMonitor.dll") report the shared host as
    // the process path; registering bare dotnet.exe would start nothing at logon. The
    // app assembly must then travel in the arguments, ahead of --minimized/--delay.
    private static (string Command, string ArgumentsPrefix) LaunchCommand()
    {
        string exe = ExePath();
        if (!string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return (exe, "");

        string? dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(dll))
            dll = Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".dll");
        return (exe, "\"" + dll + "\" ");
    }

    private static string CurrentUserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    private static string XmlEscape(string s) => SecurityElement.Escape(s) ?? s;

    /// <summary>Runs a console tool with no window; returns exit code, -1 on start failure/timeout.</summary>
    private static int RunHidden(string fileName, string arguments, out string output)
    {
        output = "";
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return -1;

        // Drain pipes asynchronously so a chatty child can never deadlock WaitForExit.
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ProcessTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return -1;
        }

        try
        {
            Task.WaitAll(new Task[] { stdOut, stdErr }, 1000);
            output = (stdErr.IsCompletedSuccessfully ? stdErr.Result : "")
                   + (stdOut.IsCompletedSuccessfully ? stdOut.Result : "");
        }
        catch
        {
            // Output is only used for error messages; losing it is acceptable.
        }
        return process.ExitCode;
    }
}
