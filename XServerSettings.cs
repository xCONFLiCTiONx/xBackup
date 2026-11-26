using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace xBackup
{
    public class XServerSettings
    {
        public bool Enabled { get; set; } = false;
        public string Host { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public int SshPort { get; set; } = 22;
        public string DestinationRoot { get; set; } = string.Empty;
        public string VhdxFileName { get; set; } = "xServerBackup.vhdx";
        public int ContainerSizeGb { get; set; } = 100;
        public string MountDriveLetter { get; set; } = "Y";
        public bool MirrorDeletions { get; set; } = true;

        public List<string> IncludePaths { get; set; } =
        [
            "/etc",
            "/opt",
            "/home",
            "/var/lib",
            "/var/backups",
            "/srv",
            "/root",
            "/usr/local",
            "/boot"
        ];

        public List<string> ExcludePatterns { get; set; } =
        [
            ".cache",
            "node_modules",
            "__pycache__",
            ".ollama/models",
            "/var/lib/docker",
            "/var/lib/containerd",
            "/var/lib/apt/lists",
            "/var/lib/snapd",
            "/var/lib/clamav",
            ".vscode-server",
            "*.sock",
            "*.pid",
            "*.lock",
            "swap.img"
        ];

        // Telemetry from last run
        public DateTime? LastBackupUtc { get; set; }
        public string LastStatus { get; set; } = "Never run";
        public long LastFilesScanned { get; set; }
        public long LastFilesUpdated { get; set; }
        public long LastFilesUnchanged { get; set; }
        public long LastFilesDeleted { get; set; }
        public long LastBytesTransferred { get; set; }

        public string VhdxFullPath => string.IsNullOrWhiteSpace(DestinationRoot)
            ? string.Empty
            : Path.Combine(DestinationRoot, string.IsNullOrWhiteSpace(VhdxFileName) ? "xServerBackup.vhdx" : VhdxFileName);

        private static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "xserver_settings.json");
        }

        public static XServerSettings Load()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<XServerSettings>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("XSERVER", $"Failed to load xServer settings: {ex.Message}");
            }
            return new XServerSettings();
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("XSERVER", $"Failed to save xServer settings: {ex.Message}");
            }
        }
    }
}
