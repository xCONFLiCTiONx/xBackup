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

        public void RestoreFiles(List<(string sourcePath, FileVersion version)> filesToRestore, string? targetPath, bool fullRestore, bool overwrite, CancellationToken token, Action<string, int, int> onProgress, Action<string>? onError = null)
        {
            int count = 0;
            int total = filesToRestore.Count;

            foreach (var item in filesToRestore)
            {
                token.ThrowIfCancellationRequested();
                count++;
                onProgress?.Invoke(item.sourcePath, count, total);

                if (string.IsNullOrEmpty(item.version.BackupPath)) continue;

                string physicalSourcePath = Path.Combine(_backupRoot, item.version.BackupPath);
                if (!File.Exists(physicalSourcePath))
                {
                    onError?.Invoke($"Missing physical backup file: {item.version.BackupPath}");
                    continue;
                }

                string destinationPath;
                if (fullRestore)
                {
                    destinationPath = item.sourcePath;
                }
                else
                {
                    string relativePath = GetRelativePathForRestore(item.sourcePath);
                    destinationPath = Path.Combine(targetPath ?? string.Empty, relativePath);
                }

                if (File.Exists(destinationPath) && !overwrite) continue;

                try
                {
                    string? parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    File.Copy(physicalSourcePath, destinationPath, overwrite: true);
                    File.SetLastWriteTimeUtc(destinationPath, item.version.LastWriteUtc);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Failed to restore {item.sourcePath}: {ex.Message}");
                }
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
