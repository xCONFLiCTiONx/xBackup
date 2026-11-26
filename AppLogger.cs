using System;
using System.IO;
using System.Linq;

namespace xBackup
{
    public static class AppLogger
    {
        private static readonly object _sync = new();
        private static bool _prunedToday = false;

        public static string LogDirectory
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(appData, "SmartBackupEngine", "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string CurrentLogFilePath =>
            Path.Combine(LogDirectory, $"xBackup_{DateTime.Now:yyyy-MM-dd}.log");

        public static void Info(string category, string message) => Write("INFO", category, message);
        public static void Warn(string category, string message) => Write("WARN", category, message);
        public static void Error(string category, string message) => Write("ERROR", category, message);

        public static void Write(string level, string category, string message)
        {
            try
            {
                lock (_sync)
                {
                    if (!_prunedToday)
                    {
                        _prunedToday = true;
                        PruneOldLogs();
                    }

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] [{category}] {message}{Environment.NewLine}";
                    File.AppendAllText(CurrentLogFilePath, line);
                }
            }
            catch
            {
                // Logging must never crash the backup engine
            }
        }

        private static void PruneOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-30);
                foreach (var file in Directory.GetFiles(LogDirectory, "xBackup_*.log"))
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTime < cutoff)
                    {
                        try { fi.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
