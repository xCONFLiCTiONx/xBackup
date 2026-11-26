using System;
using System.Diagnostics;
using System.Security.Principal;

namespace xBackup
{
    public static class TaskSchedulerHelper
    {
        public const string TaskName = "SmartPersonalBackupEngine";

        public static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool DoesTaskExist()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Query /TN \"{TaskName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit(10000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static string GetTaskStatusSummary()
        {
            try
            {
                string script = $@"
$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue;
if (-not $t) {{ Write-Output 'Not registered'; return; }}
$info = Get-ScheduledTaskInfo -TaskName '{TaskName}' -ErrorAction SilentlyContinue;
$limit = $t.Settings.ExecutionTimeLimit;
$next = if ($info -and $info.NextRunTime) {{ $info.NextRunTime.ToString('g') }} else {{ 'N/A' }};
$last = if ($info -and $info.LastRunTime -and $info.LastRunTime.Year -gt 2000) {{ $info.LastRunTime.ToString('g') }} else {{ 'Never' }};
$code = if ($info) {{ $info.LastTaskResult }} else {{ 0 }};
Write-Output ""Active | Next: $next | Last: $last (code $code) | Limit: $limit"";";
                return VhdxManager.RunPowerShell(script, 15000).Trim();
            }
            catch
            {
                return DoesTaskExist() ? "Registered (Daily at 12:00 AM)" : "Not registered";
            }
        }

        public static bool CreateDailyBackupTask()
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                DeleteBackupTask();

                string tempXmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BackupTaskConfig.xml");
                string author = WindowsIdentity.GetCurrent().Name;

                string xmlConfig = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Date>{DateTime.Now:yyyy-MM-ddTHH:mm:ss}</Date>
    <Author>{author}</Author>
    <Description>xBackup automated daily midnight incremental backup for Home-PC and xServer.</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>{DateTime.Today:yyyy-MM-dd}T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>true</WakeToRun>
    <ExecutionTimeLimit>PT6H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{exePath}""</Command>
      <Arguments>--silent</Arguments>
    </Exec>
  </Actions>
</Task>";

                System.IO.File.WriteAllText(tempXmlPath, xmlConfig, System.Text.Encoding.Unicode);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Create /TN \"{TaskName}\" /XML \"{tempXmlPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                process?.WaitForExit(15000);
                try { System.IO.File.Delete(tempXmlPath); } catch { }
                bool ok = process?.ExitCode == 0;
                AppLogger.Info("SCHEDULER", ok
                    ? $"Registered daily midnight task '{TaskName}' (WakeToRun=true, Limit=PT6H, Exe={exePath})."
                    : $"Failed to register task '{TaskName}' (exit {process?.ExitCode}).");
                return ok;
            }
            catch (Exception ex)
            {
                AppLogger.Error("SCHEDULER", $"CreateDailyBackupTask error: {ex.Message}");
                return false;
            }
        }

        public static void DeleteBackupTask()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Delete /TN \"{TaskName}\" /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit(10000);
            }
            catch { }
        }
    }
}
