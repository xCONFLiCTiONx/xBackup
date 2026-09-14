using System;
using System.Diagnostics;
using System.Security.Principal;

namespace BackupTool
{
    public static class TaskSchedulerHelper
    {
        private const string TaskName = "SmartPersonalBackupEngine";

        public static bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
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
                using (var process = Process.Start(startInfo))
                {
                    process?.WaitForExit();
                    return process?.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool CreateDailyBackupTask()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // If running via dotnet run, MainModule might point to dotnet.exe instead of our built app
                    // schtasks needs a permanent absolute executable path
                    return false;
                }

                // Delete if exists first to renew configuration cleanly
                DeleteBackupTask();

                // Build XML specification to natively support the "WakeToRun" condition feature which is impossible via basic schtasks CLI switches alone.
                string tempXmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BackupTaskConfig.xml");
                string author = WindowsIdentity.GetCurrent().Name;

                string xmlConfig = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Date>{DateTime.Now:yyyy-MM-ddTHH:mm:ss}</Date>
    <Author>{author}</Author>
    <Description>Smart automated midnight incremental backup for personal files on C drive.</Description>
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
    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
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

                using (var process = Process.Start(startInfo))
                {
                    process?.WaitForExit();
                    try { System.IO.File.Delete(tempXmlPath); } catch { }
                    return process?.ExitCode == 0;
                }
            }
            catch
            {
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
                using (var process = Process.Start(startInfo))
                {
                    process?.WaitForExit();
                }
            }
            catch { }
        }
    }
}
