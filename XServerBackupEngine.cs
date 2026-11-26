using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace xBackup
{
    public class XServerBackupProgress
    {
        public string Stage { get; set; } = string.Empty;
        public string CurrentItem { get; set; } = string.Empty;
        public long Scanned { get; set; }
        public long Updated { get; set; }
        public long Unchanged { get; set; }
        public long Deleted { get; set; }
        public long Errors { get; set; }
        public long BytesTransferred { get; set; }
        public double Percent { get; set; }
    }

    public class XServerBackupResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; } = string.Empty;
        public long Scanned { get; set; }
        public long Updated { get; set; }
        public long Unchanged { get; set; }
        public long Deleted { get; set; }
        public long Errors { get; set; }
        public long BytesTransferred { get; set; }
        public TimeSpan Duration { get; set; }
        public bool VhdxVerifiedUnmounted { get; set; }
    }

    public sealed class XServerBackupEngine
    {
        private sealed class ManifestEntry
        {
            public long Size { get; set; }
            public long MtimeNs { get; set; }
            public string LocalRelPath { get; set; } = string.Empty;
        }

        private sealed class RemoteFileInfo
        {
            public string RelPath { get; init; } = string.Empty;
            public long Size { get; init; }
            public long MtimeNs { get; init; }
        }

        private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static (bool ok, string message) TestConnection(XServerSettings settings)
        {
            try
            {
                var (exitCode, stdout, stderr) = RunSshCommand(
                    settings,
                    "sudo -n true && echo OK_SUDO || echo OK_NOSUDO",
                    stdinText: null,
                    timeoutMs: 15000,
                    CancellationToken.None);

                if (exitCode == 0 && stdout.Contains("OK_SUDO"))
                    return (true, $"Connected to {settings.Username}@{settings.Host} (passwordless sudo verified).");
                if (exitCode == 0 && stdout.Contains("OK_NOSUDO"))
                    return (false, $"SSH connected to {settings.Host}, but passwordless sudo is not enabled for '{settings.Username}'.");

                return (false, $"SSH failed (exit {exitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            }
            catch (Exception ex)
            {
                return (false, $"SSH error: {ex.Message}");
            }
        }

        public XServerBackupResult RunIncrementalClone(
            XServerSettings settings,
            bool forceDismountOnFinish,
            CancellationToken ct,
            Action<string, string>? log = null,
            Action<XServerBackupProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new XServerBackupResult();
            var errorDetails = new List<string>();
            var prog = new XServerBackupProgress();

            void Log(string level, string msg)
            {
                AppLogger.Write(level, "XSERVER", msg);
                log?.Invoke(level, msg);
            }

            void ReportProgress(string stage, string item, double percent)
            {
                prog.Stage = stage;
                prog.CurrentItem = item;
                prog.Percent = percent;
                progress?.Invoke(prog);
            }

            string vhdxPath = settings.VhdxFullPath;
            string mountedDrive = string.Empty;
            bool mountedByUs = false;

            using var keepAwake = new KeepAwakeSession("xServer Incremental Clone");

            try
            {
                ct.ThrowIfCancellationRequested();
                Log("INFO", $"Starting xServer incremental clone ({settings.Username}@{settings.Host} -> {vhdxPath})...");
                ReportProgress("Connecting", $"Testing SSH to {settings.Host}...", 2);

                var (connOk, connMsg) = TestConnection(settings);
                if (!connOk)
                {
                    throw new Exception(connMsg);
                }
                Log("INFO", connMsg);

                // Ensure destination directory and ReFS VHDX container exist
                if (!Directory.Exists(settings.DestinationRoot))
                {
                    Directory.CreateDirectory(settings.DestinationRoot);
                }

                if (!File.Exists(vhdxPath))
                {
                    Log("INFO", $"Creating xServer ReFS VHDX container ({settings.ContainerSizeGb} GB dynamic)...");
                    ReportProgress("Container", "Creating ReFS VHDX container...", 5);
                    VhdxManager.EnsureVhdxExists(vhdxPath, settings.ContainerSizeGb, settings.MountDriveLetter, "xServerReFS");
                    Log("INFO", "xServer ReFS VHDX container created successfully.");
                }

                // Mount VHDX
                string preStatus = VhdxManager.CheckVhdxMountStatus(vhdxPath);
                ReportProgress("Container", "Mounting xServer ReFS container...", 8);
                mountedDrive = VhdxManager.MountVhdxAndGetLetter(vhdxPath, settings.MountDriveLetter);
                mountedByUs = (preStatus == "NotMounted");
                Log("INFO", $"xServer ReFS VHDX mounted at {mountedDrive} (mountedByUs={mountedByUs})");

                // Let ReFS stabilize
                Thread.Sleep(1500);
                ct.ThrowIfCancellationRequested();

                string cloneRoot = Path.Combine(mountedDrive + "\\", "xServerClone");
                string metadataDir = Path.Combine(cloneRoot, "_metadata");
                string manifestPath = Path.Combine(mountedDrive + "\\", "xServerManifest.json");
                Directory.CreateDirectory(cloneRoot);
                Directory.CreateDirectory(metadataDir);

                // Step 1: Capture fresh server metadata for disaster recovery
                ReportProgress("Metadata", "Capturing xServer system metadata...", 12);
                CaptureRemoteMetadata(settings, metadataDir, ct, Log);

                // Step 2: Walk remote filesystem for incremental diff
                ReportProgress("Scanning", "Scanning remote files on xServer...", 18);
                Log("INFO", $"Scanning {settings.IncludePaths.Count} remote paths on {settings.Host}...");
                var (remoteFiles, symlinks) = ScanRemoteFiles(settings, ct);
                prog.Scanned = remoteFiles.Count;
                Log("INFO", $"Remote scan complete: {remoteFiles.Count:N0} files, {symlinks.Count:N0} symlinks.");

                // Save symlink map
                try
                {
                    File.WriteAllLines(Path.Combine(metadataDir, "symlinks.tsv"), symlinks, Encoding.UTF8);
                }
                catch { }

                // Step 3: Load previous manifest & compute delta
                ReportProgress("Diffing", "Comparing against local ReFS clone...", 25);
                var manifest = LoadManifest(manifestPath);
                var remoteLookup = new Dictionary<string, RemoteFileInfo>(remoteFiles.Count, StringComparer.Ordinal);
                var toTransfer = new List<RemoteFileInfo>();

                foreach (var rf in remoteFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    remoteLookup[rf.RelPath] = rf;

                    if (manifest.TryGetValue(rf.RelPath, out var existing) &&
                        existing.Size == rf.Size &&
                        existing.MtimeNs == rf.MtimeNs)
                    {
                        string localFull = ToLongPath(Path.Combine(cloneRoot, existing.LocalRelPath));
                        if (File.Exists(localFull))
                        {
                            prog.Unchanged++;
                            continue;
                        }
                    }

                    toTransfer.Add(rf);
                }

                // Identify deleted files
                var toDelete = new List<string>();
                if (settings.MirrorDeletions)
                {
                    foreach (var key in manifest.Keys)
                    {
                        if (!remoteLookup.ContainsKey(key))
                        {
                            toDelete.Add(key);
                        }
                    }
                }

                Log("INFO", $"Incremental diff: {toTransfer.Count:N0} changed/new, {prog.Unchanged:N0} unchanged, {toDelete.Count:N0} deleted.");

                // Step 4: Stream only changed/new files over SSH in compressed tar batches
                if (toTransfer.Count > 0)
                {
                    var batches = BuildTransferBatches(toTransfer, maxFilesPerBatch: 1500, maxBytesPerBatch: 256L * 1024 * 1024);
                    Log("INFO", $"Streaming {toTransfer.Count:N0} changed files across {batches.Count} compressed tar batch(es)...");

                    int processedChanged = 0;
                    for (int b = 0; b < batches.Count; b++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var batch = batches[b];
                        Log("INFO", $"Transferring batch {b + 1}/{batches.Count} ({batch.Count:N0} files)...");

                        StreamTarBatch(
                            settings,
                            batch,
                            remoteLookup,
                            cloneRoot,
                            manifest,
                            ct,
                            onFileExtracted: (relPath, bytes) =>
                            {
                                processedChanged++;
                                prog.Updated++;
                                prog.BytesTransferred += bytes;
                                if (processedChanged % 25 == 0 || processedChanged == toTransfer.Count)
                                {
                                    double pct = 28.0 + (62.0 * processedChanged / Math.Max(1, toTransfer.Count));
                                    ReportProgress("Syncing", relPath, pct);
                                }
                            },
                            onFileError: err =>
                            {
                                prog.Errors++;
                                errorDetails.Add(err);
                            });
                    }
                }
                else
                {
                    Log("INFO", "xServer clone is already 100% up-to-date. Zero files needed transfer.");
                }

                // Step 5: Mirror deletions
                if (toDelete.Count > 0)
                {
                    ReportProgress("Pruning", $"Removing {toDelete.Count:N0} deleted files from clone...", 92);
                    int deletedCount = 0;
                    foreach (var relPath in toDelete)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (manifest.TryGetValue(relPath, out var entry))
                        {
                            string localFull = ToLongPath(Path.Combine(cloneRoot, entry.LocalRelPath));
                            try
                            {
                                if (File.Exists(localFull))
                                {
                                    File.Delete(localFull);
                                    CleanupEmptyParents(Path.GetDirectoryName(localFull), cloneRoot);
                                }
                                manifest.Remove(relPath);
                                deletedCount++;
                                prog.Deleted++;
                            }
                            catch (Exception ex)
                            {
                                prog.Errors++;
                                errorDetails.Add($"Delete failed: {relPath} ({ex.Message})");
                            }
                        }
                    }
                    Log("INFO", $"Pruned {deletedCount:N0} deleted files from local ReFS clone.");
                }

                // Step 6: Save manifest atomically
                ReportProgress("Finalizing", "Saving incremental manifest...", 97);
                SaveManifestAtomic(manifestPath, manifest);

                sw.Stop();
                result.Success = true;
                result.Scanned = prog.Scanned;
                result.Updated = prog.Updated;
                result.Unchanged = prog.Unchanged;
                result.Deleted = prog.Deleted;
                result.Errors = prog.Errors;
                result.BytesTransferred = prog.BytesTransferred;
                result.Duration = sw.Elapsed;
                result.Summary = $"Updated {prog.Updated:N0}, Unchanged {prog.Unchanged:N0}, Deleted {prog.Deleted:N0} ({FormatBytes(prog.BytesTransferred)}) in {sw.Elapsed:mm\\:ss}";

                // Persist telemetry in settings
                settings.LastBackupUtc = DateTime.UtcNow;
                settings.LastStatus = prog.Errors == 0 ? "Success" : $"Completed with {prog.Errors} warnings";
                settings.LastFilesScanned = prog.Scanned;
                settings.LastFilesUpdated = prog.Updated;
                settings.LastFilesUnchanged = prog.Unchanged;
                settings.LastFilesDeleted = prog.Deleted;
                settings.LastBytesTransferred = prog.BytesTransferred;
                settings.Save();

                WriteReport(settings.DestinationRoot, result, errorDetails);
                Log("INFO", $"xServer incremental clone finished: {result.Summary}");
                ReportProgress("Complete", result.Summary, 100);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                result.Success = false;
                result.Duration = sw.Elapsed;
                result.Summary = "Cancelled by user.";
                settings.LastStatus = "Cancelled";
                settings.Save();
                Log("WARN", "xServer incremental clone cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.Duration = sw.Elapsed;
                result.Summary = $"Failed: {ex.Message}";
                settings.LastStatus = $"Failed: {ex.Message}";
                settings.Save();
                Log("ERROR", $"xServer incremental clone failed: {ex.Message}");
            }
            finally
            {
                if (mountedByUs || forceDismountOnFinish)
                {
                    ReportProgress("Dismounting", "Verifying xServer ReFS container dismount...", 99);
                    result.VhdxVerifiedUnmounted = VhdxManager.DismountVhdxVerified(
                        vhdxPath,
                        "xServer",
                        (msg, isWarn) => log?.Invoke(isWarn ? "WARN" : "INFO", msg));
                }
                else
                {
                    result.VhdxVerifiedUnmounted = VhdxManager.CheckVhdxMountStatus(vhdxPath) == "NotMounted";
                }
            }

            return result;
        }

        private static void CaptureRemoteMetadata(XServerSettings settings, string metadataDir, CancellationToken ct, Action<string, string> log)
        {
            string cmd = @"sudo bash -c '
echo ""===DATE===""; date -u +%Y-%m-%dT%H:%M:%SZ
echo ""===UNAME===""; uname -a
echo ""===DF===""; df -h
echo ""===LSBLK===""; lsblk -f
echo ""===FSTAB===""; cat /etc/fstab 2>/dev/null
echo ""===SYSTEMD===""; systemctl list-unit-files --no-pager 2>/dev/null
echo ""===DPKG===""; dpkg --get-selections 2>/dev/null
'";
            try
            {
                var (code, stdout, _) = RunSshCommand(settings, cmd, null, 30000, ct);
                if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    File.WriteAllText(Path.Combine(metadataDir, "system-info.txt"), stdout, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                log("WARN", $"Metadata capture warning: {ex.Message}");
            }
        }

        private static (List<RemoteFileInfo> files, List<string> symlinks) ScanRemoteFiles(XServerSettings settings, CancellationToken ct)
        {
            string includesJson = JsonSerializer.Serialize(settings.IncludePaths);
            string excludesJson = JsonSerializer.Serialize(settings.ExcludePatterns);

            string pyScript = $@"
import os, sys, json, fnmatch

includes = json.loads({JsonSerializer.Serialize(includesJson)})
excludes = json.loads({JsonSerializer.Serialize(excludesJson)})

def is_excluded(full_path, name):
    for ex in excludes:
        if not ex:
            continue
        if ex.startswith('/'):
            if full_path == ex or full_path.startswith(ex + '/'):
                return True
        elif '/' in ex:
            if ex in full_path:
                return True
        else:
            if name == ex or fnmatch.fnmatch(name, ex):
                return True
    return False

out = sys.stdout.buffer
for root_path in includes:
    root_path = root_path.rstrip('/')
    if not root_path or not os.path.exists(root_path):
        continue
    if os.path.isfile(root_path) and not os.path.islink(root_path):
        try:
            st = os.lstat(root_path)
            rel = root_path.lstrip('/')
            out.write(f'F\t{{rel}}\t{{st.st_size}}\t{{st.st_mtime_ns}}\n'.encode('utf-8', 'surrogateescape'))
        except Exception:
            pass
        continue
    for dirpath, dirnames, filenames in os.walk(root_path, topdown=True, followlinks=False):
        dirnames[:] = [d for d in dirnames if not is_excluded(os.path.join(dirpath, d), d)]
        for fn in filenames:
            fp = os.path.join(dirpath, fn)
            if is_excluded(fp, fn):
                continue
            try:
                st = os.lstat(fp)
                mode = st.st_mode
                rel = fp.lstrip('/')
                if os.path.islink(fp):
                    target = os.readlink(fp)
                    out.write(f'L\t{{rel}}\t{{target}}\n'.encode('utf-8', 'surrogateescape'))
                elif (mode & 0o170000) == 0o100000:
                    out.write(f'F\t{{rel}}\t{{st.st_size}}\t{{st.st_mtime_ns}}\n'.encode('utf-8', 'surrogateescape'))
            except Exception:
                pass
out.flush()
";

            var (exitCode, stdout, stderr) = RunSshCommand(settings, "sudo python3 -", pyScript, 180000, ct);
            if (exitCode != 0)
            {
                throw new Exception($"Remote file scan failed (exit {exitCode}): {stderr.Trim()}");
            }

            var files = new List<RemoteFileInfo>();
            var symlinks = new List<string>();

            using var reader = new StringReader(stdout);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 3) continue;
                var parts = line.Split('\t');
                if (parts[0] == "F" && parts.Length >= 4 &&
                    long.TryParse(parts[2], out long size) &&
                    long.TryParse(parts[3], out long mtimeNs))
                {
                    files.Add(new RemoteFileInfo
                    {
                        RelPath = parts[1],
                        Size = size,
                        MtimeNs = mtimeNs
                    });
                }
                else if (parts[0] == "L" && parts.Length >= 3)
                {
                    symlinks.Add($"{parts[1]}\t{parts[2]}");
                }
            }

            return (files, symlinks);
        }

        private static List<List<RemoteFileInfo>> BuildTransferBatches(List<RemoteFileInfo> items, int maxFilesPerBatch, long maxBytesPerBatch)
        {
            var batches = new List<List<RemoteFileInfo>>();
            var current = new List<RemoteFileInfo>();
            long currentBytes = 0;

            foreach (var item in items)
            {
                if (current.Count > 0 && (current.Count >= maxFilesPerBatch || currentBytes + item.Size > maxBytesPerBatch))
                {
                    batches.Add(current);
                    current = [];
                    currentBytes = 0;
                }
                current.Add(item);
                currentBytes += item.Size;
            }

            if (current.Count > 0) batches.Add(current);
            return batches;
        }

        private static void StreamTarBatch(
            XServerSettings settings,
            List<RemoteFileInfo> batch,
            Dictionary<string, RemoteFileInfo> remoteLookup,
            string cloneRoot,
            Dictionary<string, ManifestEntry> manifest,
            CancellationToken ct,
            Action<string, long> onFileExtracted,
            Action<string> onFileError)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh.exe",
                Arguments = $"-o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=20 -p {settings.SshPort} {settings.Username}@{settings.Host} \"sudo tar --ignore-failed-read --no-recursion --null -T - -czf -\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi) ?? throw new Exception("Failed to launch ssh.exe for tar transfer.");

            using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });

            // Feed NUL-delimited file paths to tar stdin on a background task
            var feedTask = Task.Run(() =>
            {
                try
                {
                    using var stdin = process.StandardInput.BaseStream;
                    foreach (var rf in batch)
                    {
                        byte[] pathBytes = Encoding.UTF8.GetBytes("/" + rf.RelPath);
                        stdin.Write(pathBytes, 0, pathBytes.Length);
                        stdin.WriteByte(0);
                    }
                    stdin.Flush();
                }
                catch { }
                finally
                {
                    try { process.StandardInput.Close(); } catch { }
                }
            }, ct);

            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                using var gzip = new GZipStream(process.StandardOutput.BaseStream, CompressionMode.Decompress, leaveOpen: true);
                using var tarReader = new TarReader(gzip, leaveOpen: true);

                TarEntry? entry;
                while ((entry = tarReader.GetNextEntry(copyData: false)) != null)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                        continue;

                    string relPath = entry.Name.TrimStart('/');
                    if (relPath.StartsWith("./", StringComparison.Ordinal)) relPath = relPath[2..];

                    string localRelPath = SanitizeLinuxPathForWindows(relPath);
                    string destPath = ToLongPath(Path.Combine(cloneRoot, localRelPath));
                    string tmpPath = destPath + ".xtmp";

                    try
                    {
                        string? parent = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        long bytesWritten = 0;
                        if (entry.DataStream != null)
                        {
                            using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                            entry.DataStream.CopyTo(fs);
                            bytesWritten = fs.Length;
                        }
                        else
                        {
                            using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        }

                        File.Move(tmpPath, destPath, overwrite: true);

                        DateTime utcTime = entry.ModificationTime.UtcDateTime;
                        if (utcTime.Year >= 1980 && utcTime.Year <= 2100)
                        {
                            File.SetLastWriteTimeUtc(destPath, utcTime);
                        }

                        if (remoteLookup.TryGetValue(relPath, out var rf))
                        {
                            manifest[relPath] = new ManifestEntry
                            {
                                Size = rf.Size,
                                MtimeNs = rf.MtimeNs,
                                LocalRelPath = localRelPath
                            };
                        }
                        else
                        {
                            manifest[relPath] = new ManifestEntry
                            {
                                Size = bytesWritten,
                                MtimeNs = new DateTimeOffset(utcTime).ToUnixTimeMilliseconds() * 1_000_000L,
                                LocalRelPath = localRelPath
                            };
                        }

                        onFileExtracted(relPath, bytesWritten);
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        onFileError($"Extract failed for '{relPath}': {ex.Message}");
                    }
                }
            }
            finally
            {
                try { feedTask.Wait(5000); } catch { }
                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(true); } catch { }
                }
                string stderr = "";
                try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
                // tar exit code 1 = "file changed as we read it" (normal on live Linux servers)
                if (process.ExitCode > 1 && !ct.IsCancellationRequested)
                {
                    AppLogger.Warn("XSERVER", $"tar batch exited with code {process.ExitCode}: {stderr.Trim()}");
                }
            }
        }

        private static (int exitCode, string stdout, string stderr) RunSshCommand(
            XServerSettings settings,
            string remoteCommand,
            string? stdinText,
            int timeoutMs,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh.exe",
                Arguments = $"-o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 -p {settings.SshPort} {settings.Username}@{settings.Host} \"{remoteCommand.Replace("\"", "\\\"")}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = stdinText != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi) ?? throw new Exception("Failed to launch ssh.exe.");
            using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });

            if (stdinText != null)
            {
                process.StandardInput.Write(stdinText);
                process.StandardInput.Close();
            }

            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException($"SSH command timed out after {timeoutMs / 1000}s.");
            }

            return (process.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        }

        private static Dictionary<string, ManifestEntry> LoadManifest(string manifestPath)
        {
            try
            {
                if (File.Exists(manifestPath))
                {
                    string json = File.ReadAllText(manifestPath, Encoding.UTF8);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(json);
                    if (dict != null) return dict;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("XSERVER", $"Could not read previous manifest (starting fresh index): {ex.Message}");
            }
            return new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        }

        private static void SaveManifestAtomic(string manifestPath, Dictionary<string, ManifestEntry> manifest)
        {
            string tmp = manifestPath + ".tmp";
            string json = JsonSerializer.Serialize(manifest);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, manifestPath, overwrite: true);
        }

        private static string SanitizeLinuxPathForWindows(string linuxRelPath)
        {
            var segments = linuxRelPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i];
                var sb = new StringBuilder(seg.Length);
                foreach (char c in seg)
                {
                    if (c < 32 || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    {
                        sb.Append($"_x{(int)c:X2}_");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                seg = sb.ToString().TrimEnd(' ', '.');
                if (string.IsNullOrEmpty(seg)) seg = "_";
                string baseName = Path.GetFileNameWithoutExtension(seg);
                if (WindowsReservedNames.Contains(baseName))
                {
                    seg = "_" + seg;
                }
                segments[i] = seg;
            }
            return string.Join(Path.DirectorySeparatorChar, segments);
        }

        private static string ToLongPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.Length >= 240) return @"\\?\" + path;
            return path;
        }

        private static void CleanupEmptyParents(string? dir, string stopRoot)
        {
            try
            {
                string normStop = stopRoot.TrimEnd('\\');
                while (!string.IsNullOrEmpty(dir))
                {
                    string cleanDir = dir.StartsWith(@"\\?\", StringComparison.Ordinal) ? dir[4..] : dir;
                    if (string.Equals(cleanDir.TrimEnd('\\'), normStop, StringComparison.OrdinalIgnoreCase)) break;
                    if (!Directory.Exists(dir)) break;
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    Directory.Delete(dir, false);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
        }

        private static void WriteReport(string destRoot, XServerBackupResult result, List<string> errors)
        {
            try
            {
                string dir = Path.Combine(destRoot, "BackupHistoryLogs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"xServerReport_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
                using var sw = new StreamWriter(file, false, Encoding.UTF8);
                sw.WriteLine("==========================================================================");
                sw.WriteLine("xBACKUP - xSERVER INCREMENTAL REFS CLONE REPORT");
                sw.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine("==========================================================================");
                sw.WriteLine($"Duration:                 {result.Duration:hh\\:mm\\:ss}");
                sw.WriteLine($"Remote Files Scanned:     {result.Scanned:N0}");
                sw.WriteLine($"Updated / New Files:      {result.Updated:N0}");
                sw.WriteLine($"Unchanged (Skipped):      {result.Unchanged:N0}");
                sw.WriteLine($"Deleted (Mirrored):       {result.Deleted:N0}");
                sw.WriteLine($"Warnings / Errors:        {result.Errors:N0}");
                sw.WriteLine($"Bytes Transferred:        {FormatBytes(result.BytesTransferred)}");
                sw.WriteLine("==========================================================================");
                if (errors.Count > 0)
                {
                    sw.WriteLine();
                    sw.WriteLine("WARNINGS / ERRORS:");
                    foreach (var err in errors.Take(200))
                    {
                        sw.WriteLine("-> " + err);
                    }
                }
            }
            catch { }
        }

        public static string FormatBytes(long bytes)
        {
            string[] suffix = ["B", "KB", "MB", "GB", "TB"];
            double dbl = bytes;
            int i = 0;
            while (dbl >= 1024 && i < suffix.Length - 1) { i++; dbl /= 1024; }
            return $"{dbl:F2} {suffix[i]}";
        }
    }
}
