using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace xBackup
{
    /// <summary>
    /// Keeps Windows awake across the entire backup + dismount lifecycle using a dedicated
    /// keep-awake thread and periodic heartbeat so SetThreadExecutionState never drops on threadpool switches.
    /// </summary>
    public sealed class KeepAwakeSession : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

        private readonly ManualResetEventSlim _stopEvent = new(false);
        private readonly Thread _thread;
        private bool _disposed;

        public KeepAwakeSession(string reason)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    AppLogger.Info("POWER", $"KeepAwake started ({reason}).");
                    while (!_stopEvent.IsSet)
                    {
                        uint res = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                        if (res == 0)
                        {
                            // Fallback without awaymode on systems that do not support Away Mode
                            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                        }
                        _stopEvent.Wait(TimeSpan.FromSeconds(25));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("POWER", $"KeepAwake thread exception: {ex.Message}");
                }
                finally
                {
                    try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                    AppLogger.Info("POWER", $"KeepAwake released ({reason}).");
                }
            })
            {
                IsBackground = true,
                Name = "xBackup-KeepAwake"
            };
            _thread.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopEvent.Set();
            try { _thread.Join(2000); } catch { }
            _stopEvent.Dispose();
        }
    }

    public static class VhdxManager
    {
        public static void PreloadDiskpart()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = "?",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit(5000);
            }
            catch { }
        }

        public static int GetVhdxVirtualSizeGb(string vhdxPath)
        {
            try
            {
                string script = $@"
$p = '{vhdxPath.Replace("'", "''")}';
if (Test-Path $p) {{
    $di = Get-DiskImage -ImagePath $p -ErrorAction SilentlyContinue 2>$null;
    if ($di -and $di.Size) {{
        Write-Output ([math]::Round($di.Size / 1GB));
        return;
    }}
}}
Write-Output '0';";
                string output = RunPowerShell(script).Trim();
                if (int.TryParse(output, out int gb)) return gb;
                return 0;
            }
            catch { return 0; }
        }

        public static string CheckVhdxMountStatus(string vhdxPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(vhdxPath) || !File.Exists(vhdxPath))
                    return "NotMounted";

                string script = $@"
$path = '{vhdxPath.Replace("'", "''")}';
if (Test-Path $path) {{
    $di = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue 2>$null;
    if ($di -and $di.Attached) {{
        $diskNum = $di.Number;
        if ($null -eq $diskNum -and $di.DevicePath -match 'PhysicalDrive(\d+)') {{
            $diskNum = [int]$matches[1];
        }}
        if ($null -ne $diskNum) {{
            $part = Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue 2>$null | Where-Object {{ $_.DriveLetter }} | Select-Object -First 1;
            if ($part -and $part.DriveLetter) {{
                Write-Output $part.DriveLetter;
                return;
            }}
        }}
        Write-Output 'Mounted';
        return;
    }}
}}
Write-Output 'NotMounted';";

                string output = RunPowerShell(script).Trim();
                if (output == "Mounted" || output == "NotMounted") return output;
                if (!string.IsNullOrEmpty(output) && char.IsLetter(output[0])) return output[0] + ":";
                return "NotMounted";
            }
            catch (Exception ex)
            {
                AppLogger.Warn("VHDX", $"CheckVhdxMountStatus error for '{vhdxPath}': {ex.Message}");
                return "NotMounted";
            }
        }

        public static void EnsureVhdxExists(string vhdxPath, int sizeGb, string driveLetterStr, string volumeLabel = "BackupReFS")
        {
            string? dir = Path.GetDirectoryName(vhdxPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (sizeGb < 10) sizeGb = 100;
            driveLetterStr = string.IsNullOrWhiteSpace(driveLetterStr) ? "Z" : driveLetterStr.Trim().TrimEnd(':').ToUpperInvariant();

            AppLogger.Info("VHDX", $"Creating dynamic VHDX '{vhdxPath}' ({sizeGb} GB, label={volumeLabel}, letter={driveLetterStr})");

            int sizeMb = sizeGb * 1024;
            string diskpartSetup = $@"create vdisk file=""{vhdxPath}"" maximum={sizeMb} type=expandable
select vdisk file=""{vhdxPath}""
attach vdisk
create partition primary
assign letter={driveLetterStr}
exit";

            try
            {
                RunDiskpartScript(diskpartSetup);
                try
                {
                    RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem ReFS -DevDrive -NewFileSystemLabel '{volumeLabel}' -Confirm:$false");
                    try { RunSystemCommand("fsutil.exe", $"devdrv trust /vol:{driveLetterStr}:"); } catch { }
                    AppLogger.Info("VHDX", $"Formatted '{vhdxPath}' as ReFS Dev Drive ({volumeLabel}).");
                }
                catch
                {
                    try
                    {
                        RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem ReFS -NewFileSystemLabel '{volumeLabel}' -Confirm:$false");
                        AppLogger.Info("VHDX", $"Formatted '{vhdxPath}' as standard ReFS ({volumeLabel}).");
                    }
                    catch
                    {
                        RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem NTFS -NewFileSystemLabel '{volumeLabel}' -Confirm:$false");
                        AppLogger.Warn("VHDX", $"ReFS unavailable; formatted '{vhdxPath}' as NTFS ({volumeLabel}).");
                    }
                }
            }
            finally
            {
                DismountVhdxVerified(vhdxPath, volumeLabel, null);
            }
        }

        public static string MountVhdxAndGetLetter(string vhdxPath, string preferredDriveLetter)
        {
            preferredDriveLetter = string.IsNullOrWhiteSpace(preferredDriveLetter)
                ? "Z"
                : preferredDriveLetter.Trim().TrimEnd(':').ToUpperInvariant();

            string currentStatus = CheckVhdxMountStatus(vhdxPath);
            if (currentStatus != "NotMounted" && currentStatus != "Mounted" && !string.IsNullOrEmpty(currentStatus) && currentStatus.Length <= 3)
            {
                AppLogger.Info("VHDX", $"VHDX '{vhdxPath}' already mounted at {currentStatus}; reusing.");
                return currentStatus;
            }

            AppLogger.Info("VHDX", $"Mounting VHDX '{vhdxPath}' to target drive letter {preferredDriveLetter}:");

            string script = $@"
$path = '{vhdxPath.Replace("'", "''")}';
$null = Mount-DiskImage -ImagePath $path -StorageType VHDX -ErrorAction SilentlyContinue;
for ($i = 0; $i -lt 20; $i++) {{
    $di = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue 2>$null;
    if ($di -and $di.Attached) {{
        $diskNum = $di.Number;
        if ($null -eq $diskNum -and $di.DevicePath -match 'PhysicalDrive(\d+)') {{
            $diskNum = [int]$matches[1];
        }}
        if ($null -ne $diskNum) {{
            $disk = Get-Disk -Number $diskNum -ErrorAction SilentlyContinue 2>$null;
            if ($disk) {{
                if ($disk.IsOffline) {{ $null = Set-Disk -Number $diskNum -IsOffline $false -ErrorAction SilentlyContinue 2>$null; }}
                if ($disk.IsReadOnly) {{ $null = Set-Disk -Number $diskNum -IsReadOnly $false -ErrorAction SilentlyContinue 2>$null; }}
            }}
            $part = Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue 2>$null | Where-Object {{ $_.Type -ne 'Reserved' -and $_.Type -ne 'System' }} | Sort-Object Size -Descending | Select-Object -First 1;
            if ($part) {{
                $curLetter = if ($part.DriveLetter -and [char]::IsLetter([char]$part.DriveLetter)) {{ [string]$part.DriveLetter }} else {{ '' }};
                if ($curLetter -ne '{preferredDriveLetter}') {{
                    $null = Set-Partition -DiskNumber $diskNum -PartitionNumber $part.PartitionNumber -NewDriveLetter '{preferredDriveLetter}' -Confirm:$false -ErrorAction SilentlyContinue 2>$null;
                }}
                $up = Get-Partition -DiskNumber $diskNum -PartitionNumber $part.PartitionNumber -ErrorAction SilentlyContinue 2>$null;
                if ($up -and $up.DriveLetter -and [char]::IsLetter([char]$up.DriveLetter)) {{
                    Write-Output $up.DriveLetter;
                    return;
                }}
            }}
        }}
    }}
    Start-Sleep -Milliseconds 500;
}}";

            string output = RunPowerShell(script).Trim();
            if (!string.IsNullOrEmpty(output) && char.IsLetter(output[0]))
            {
                string letter = output[0] + ":";
                AppLogger.Info("VHDX", $"Mounted '{vhdxPath}' successfully at {letter}");
                return letter;
            }

            // Fallback: use diskpart
            try
            {
                AppLogger.Warn("VHDX", $"PowerShell mount did not return a drive letter for '{vhdxPath}'; trying diskpart fallback...");
                string attachScript = $@"select vdisk file=""{vhdxPath}""
attach vdisk noerr
online disk noerr
attributes disk clear readonly noerr
select partition 1
assign letter={preferredDriveLetter} noerr
exit";
                RunDiskpartScript(attachScript);
                Thread.Sleep(500);

                string postStatus = CheckVhdxMountStatus(vhdxPath);
                if (postStatus != "NotMounted" && postStatus != "Mounted" && !string.IsNullOrEmpty(postStatus) && char.IsLetter(postStatus[0]))
                {
                    AppLogger.Info("VHDX", $"Mounted '{vhdxPath}' via diskpart at {postStatus}");
                    return postStatus;
                }
                if (Directory.Exists($"{preferredDriveLetter}:\\"))
                {
                    AppLogger.Info("VHDX", $"Mounted '{vhdxPath}' via diskpart at {preferredDriveLetter}:");
                    return $"{preferredDriveLetter}:";
                }
            }
            catch (Exception dpEx)
            {
                AppLogger.Error("VHDX", $"VHDX mount failed for '{vhdxPath}': {dpEx.Message}");
                throw new Exception($"VHDX mount failed: {dpEx.Message}", dpEx);
            }

            AppLogger.Error("VHDX", $"VHDX mount timed out discovering drive letter for '{vhdxPath}'.");
            throw new Exception("VHDX container mounted, but target drive letter discovery timed out.");
        }

        /// <summary>
        /// Aggressively dismounts a VHDX container with retries, SQLite handle release,
        /// disk-offline fallback, full persistent logging, and post-dismount verification.
        /// Returns true ONLY when Get-DiskImage confirms the VHDX is no longer attached.
        /// </summary>
        public static bool DismountVhdxVerified(string vhdxPath, string label, Action<string, bool>? uiLog = null)
        {
            if (string.IsNullOrWhiteSpace(vhdxPath) || !File.Exists(vhdxPath))
            {
                AppLogger.Info("DISMOUNT", $"[{label}] VHDX path does not exist or is empty ('{vhdxPath}'); skipping dismount.");
                return true;
            }

            string initialStatus = CheckVhdxMountStatus(vhdxPath);
            if (initialStatus == "NotMounted")
            {
                AppLogger.Info("DISMOUNT", $"[{label}] Verified already NotMounted: '{vhdxPath}'.");
                return true;
            }

            AppLogger.Info("DISMOUNT", $"[{label}] Starting verified dismount for '{vhdxPath}' (current status: {initialStatus})...");
            uiLog?.Invoke($"Dismounting {label} container ({initialStatus})...", false);

            int[] delaysMs = [800, 1500, 2500, 4000, 5000];

            for (int attempt = 1; attempt <= delaysMs.Length; attempt++)
            {
                // Release managed & SQLite file handles before every attempt
                try
                {
                    SqliteConnection.ClearAllPools();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch { }

                try
                {
                    if (attempt == 1)
                    {
                        AppLogger.Info("DISMOUNT", $"[{label}] Attempt {attempt}: PowerShell Dismount-DiskImage...");
                        string script = $@"$path = '{vhdxPath.Replace("'", "''")}'; Dismount-DiskImage -ImagePath $path -ErrorAction Stop;";
                        RunPowerShell(script, 25000);
                    }
                    else if (attempt == 2)
                    {
                        AppLogger.Info("DISMOUNT", $"[{label}] Attempt {attempt}: diskpart detach vdisk...");
                        string detachScript = $@"select vdisk file=""{vhdxPath}""
detach vdisk
exit";
                        RunDiskpartScript(detachScript);
                    }
                    else
                    {
                        AppLogger.Warn("DISMOUNT", $"[{label}] Attempt {attempt}: Taking virtual disk offline then detaching...");
                        string offlineAndDetach = $@"
$path = '{vhdxPath.Replace("'", "''")}';
$di = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue;
if ($di -and $di.Attached) {{
    $diskNum = $di.Number;
    if ($null -eq $diskNum -and $di.DevicePath -match 'PhysicalDrive(\d+)') {{
        $diskNum = [int]$matches[1];
    }}
    if ($null -ne $diskNum) {{
        $null = Set-Disk -Number $diskNum -IsOffline $true -ErrorAction SilentlyContinue;
        Start-Sleep -Milliseconds 500;
    }}
    Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue;
}}";
                        RunPowerShell(offlineAndDetach, 25000);

                        if (CheckVhdxMountStatus(vhdxPath) != "NotMounted")
                        {
                            string detachScript = $@"select vdisk file=""{vhdxPath}""
detach vdisk noerr
exit";
                            RunDiskpartScript(detachScript);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("DISMOUNT", $"[{label}] Attempt {attempt} error: {ex.Message}");
                }

                Thread.Sleep(delaysMs[attempt - 1]);

                string postStatus = CheckVhdxMountStatus(vhdxPath);
                if (postStatus == "NotMounted")
                {
                    AppLogger.Info("DISMOUNT", $"[{label}] VERIFIED UNMOUNTED on attempt {attempt}: '{vhdxPath}'.");
                    uiLog?.Invoke($"{label} container verified unmounted.", false);
                    return true;
                }

                AppLogger.Warn("DISMOUNT", $"[{label}] Still mounted after attempt {attempt} (status={postStatus}). Retrying...");
            }

            string finalStatus = CheckVhdxMountStatus(vhdxPath);
            bool unmounted = finalStatus == "NotMounted";
            if (!unmounted)
            {
                AppLogger.Error("DISMOUNT", $"[{label}] FAILED TO DISMOUNT after {delaysMs.Length} attempts! Final status={finalStatus}, path='{vhdxPath}'.");
                uiLog?.Invoke($"WARNING: {label} container is still mounted ({finalStatus}) after retries. Check log.", true);
            }
            return unmounted;
        }

        public static void ExpandVhdx(string vhdxPath, int newSizeGb)
        {
            int newSizeMb = newSizeGb * 1024;
            AppLogger.Info("VHDX", $"Expanding VHDX '{vhdxPath}' to {newSizeGb} GB...");

            string expandScript = $@"select vdisk file=""{vhdxPath}""
expand vdisk maximum={newSizeMb}
exit";
            RunDiskpartScript(expandScript);

            string extendScript = $@"select vdisk file=""{vhdxPath}""
attach vdisk
select partition 1
extend
detach vdisk
exit";
            RunDiskpartScript(extendScript);
            AppLogger.Info("VHDX", $"Expanded VHDX '{vhdxPath}' to {newSizeGb} GB successfully.");
        }

        public static void RunDiskpartScript(string scriptContent)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"vhdx_op_{Guid.NewGuid()}.txt");
            File.WriteAllText(scriptPath, scriptContent);

            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new Exception("Failed to invoke diskpart utility.");
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45000))
            {
                try { process.Kill(true); } catch { }
                try { File.Delete(scriptPath); } catch { }
                throw new TimeoutException("diskpart timed out after 45 seconds.");
            }
            string output = outTask.GetAwaiter().GetResult();
            string error = errTask.GetAwaiter().GetResult();
            try { File.Delete(scriptPath); } catch { }
            if (process.ExitCode != 0)
            {
                throw new Exception($"Diskpart failed ({process.ExitCode}). Output: {output.Trim()} Error: {error.Trim()}");
            }
        }

        public static void RunSystemCommand(string fileName, string arguments, int timeoutMs = 30000)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new Exception($"Failed to invoke system utility: {fileName}");
            var errTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException($"Utility {fileName} timed out.");
            }
            if (process.ExitCode != 0)
            {
                string error = errTask.GetAwaiter().GetResult();
                throw new Exception($"Utility {fileName} failed with exit code {process.ExitCode}. Error: {error}");
            }
        }

        public static string RunPowerShell(string command, int timeoutMs = 30000)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new Exception("Failed to initialize PowerShell.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException($"PowerShell command timed out after {timeoutMs / 1000} seconds.");
            }
            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new Exception($"PowerShell command failed (exit {process.ExitCode}): {error.Trim()}");
            }
            return output;
        }
    }
}
