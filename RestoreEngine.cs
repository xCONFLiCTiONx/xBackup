using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using xBackup.Models;

namespace xBackup
{
    public class RestoreEngine
    {
        private readonly BackupCatalog _catalog;
        private readonly string _backupRoot;

        public RestoreEngine(BackupCatalog catalog, string backupRoot)
        {
            _catalog = catalog;
            _backupRoot = backupRoot;
        }

        public void RestoreSnapshot(int snapshotId, string targetPath, bool overwrite, CancellationToken token, Action<string, int, int> onProgress)
        {
            var allFiles = _catalog.GetAllFiles();
            var filesToRestore = new List<(BackupFile file, FileVersion version)>();

            foreach (var f in allFiles)
            {
                token.ThrowIfCancellationRequested();
                var latest = _catalog.GetLatestVersion(f.Id, snapshotId);
                if (latest != null && latest.ChangeType != ChangeType.Deleted)
                {
                    filesToRestore.Add((f, latest));
                }
            }

            int count = 0;
            int total = filesToRestore.Count;

            foreach (var item in filesToRestore)
            {
                token.ThrowIfCancellationRequested();
                count++;
                onProgress?.Invoke(item.file.SourcePath, count, total);

                if (string.IsNullOrEmpty(item.version.BackupPath)) continue;

                string physicalSourcePath = Path.Combine(_backupRoot, item.version.BackupPath);
                if (!File.Exists(physicalSourcePath)) continue;

                // Reconstruct the original path structure under the target path
                // We use the SourcePath to determine the relative structure, but we need to handle drive letters.
                // The BackupPath itself already encodes the drive letter if we used MapToBackupPath correctly.
                // Actually, let's just use the relative part of SourcePath or reconstruct it.

                string relativePath = GetRelativePathForRestore(item.file.SourcePath);
                string destinationPath = Path.Combine(targetPath, relativePath);

                if (File.Exists(destinationPath) && !overwrite) continue;

                string? parent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(physicalSourcePath, destinationPath, overwrite: true);
                File.SetLastWriteTimeUtc(destinationPath, item.version.LastWriteUtc);
            }
        }

        private string GetRelativePathForRestore(string sourcePath)
        {
            if (sourcePath.Length >= 3 && sourcePath[1] == ':' && sourcePath[2] == '\\')
            {
                char driveLetter = char.ToUpper(sourcePath[0]);
                return Path.Combine(driveLetter.ToString(), sourcePath.Substring(3));
            }
            return sourcePath;
        }
    }
}
