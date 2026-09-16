using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace xBackup
{
    public static class GlobalExclusions
    {
        private static readonly object _lock = new object();
        public static HashSet<string> CustomExcludedPaths { get; } = new HashSet<string>();
        public static HashSet<string> SelectedDrives { get; } = new HashSet<string>();

        // Pre-optimized sets for fast lookup
        private static readonly HashSet<string> _excludedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".vs", ".idea", "target", "bin", "obj", "build", "$recycle.bin",
            "system volume information", "prefetch", "softwaredistribution", "installer",
            "inetcache", "webcache", "dxcache", "d3dscache", "crashrpt", "_cache", "cache",
            "cacheddata", "code cache", "gpucache", "assetcache", "windows", "program files",
            "program files (x86)", "recovery", "config.msi", "perflogs", "documents and settings"
        };

        private static readonly string[] _excludedPathSegments = new[]
        {
            @"\appdata\locallow",
            @"\appdata\local\packages",
            @"\programdata\packages",
            @"\programdata\microsoft\windows defender",
            @"\programdata\microsoft\crypto",
            @"\application data",
            @"\appdata\local\google\androidstudio",
            @"\appdata\local\temp",
            @"\appdata\local\microsoft\edge\user data",
            @"\google\chrome\user data\default\cache",
            @"\mozilla\firefox\profiles\",
            @"\appdata\roaming\discord\cache",
            @"\appdata\roaming\spotify\storage",
            @"\microsoft\windows\inetcache",
            @"\programdata\package cache",
            @"\windows\temp",
            @"\microsoft.yourphone",
            @"\mobiledeviceconnect",
            @"\phone-link",
            @"\crossdevice",
            @"\$recycle.bin",
            @"\system volume information"
        };

        public static int RetentionDays { get; set; } = 90;

        private static string GetConfigFilePath(string fileName)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, fileName);
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    // Load Exclusions
                    string exclusionPath = GetConfigFilePath("custom_exclusions.json");
                    if (File.Exists(exclusionPath))
                    {
                        string json = File.ReadAllText(exclusionPath);
                        var list = JsonSerializer.Deserialize<List<string>>(json);
                        CustomExcludedPaths.Clear();
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                if (!string.IsNullOrWhiteSpace(item))
                                {
                                    CustomExcludedPaths.Add(item.Trim().ToLowerInvariant().Replace('/', '\\'));
                                }
                            }
                        }
                    }

                    // Load Selected Drives
                    string drivesPath = GetConfigFilePath("selected_drives.json");
                    SelectedDrives.Clear();
                    if (File.Exists(drivesPath))
                    {
                        string json = File.ReadAllText(drivesPath);
                        var list = JsonSerializer.Deserialize<List<string>>(json);
                        if (list != null)
                        {
                            foreach (var drive in list)
                            {
                                SelectedDrives.Add(drive.ToUpperInvariant());
                            }
                        }
                    }
                    else
                    {
                        // Default to C: if nothing saved
                        SelectedDrives.Add("C:\\");
                    }

                    // Load Retention Days setting
                    string retentionPath = GetConfigFilePath("retention_settings.json");
                    if (File.Exists(retentionPath))
                    {
                        string json = File.ReadAllText(retentionPath);
                        var val = JsonSerializer.Deserialize<int>(json);
                        if (val > 0)
                        {
                            RetentionDays = val;
                        }
                    }
                }
                catch { }
            }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    // Save Exclusions
                    string exclusionPath = GetConfigFilePath("custom_exclusions.json");
                    var exclList = new List<string>(CustomExcludedPaths);
                    string exclJson = JsonSerializer.Serialize(exclList, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(exclusionPath, exclJson);

                    // Save Selected Drives
                    string drivesPath = GetConfigFilePath("selected_drives.json");
                    var driveList = new List<string>(SelectedDrives);
                    string driveJson = JsonSerializer.Serialize(driveList, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(drivesPath, driveJson);

                    // Save Retention Days setting
                    string retentionPath = GetConfigFilePath("retention_settings.json");
                    string rentJson = JsonSerializer.Serialize(RetentionDays);
                    File.WriteAllText(retentionPath, rentJson);
                }
                catch { }
            }
        }

        public static bool IsCustomExcluded(string lowerPath)
        {
            if (string.IsNullOrEmpty(lowerPath)) return false;
            string normalized = lowerPath.ToLowerInvariant().Replace('/', '\\');

            lock (_lock)
            {
                if (CustomExcludedPaths.Contains(normalized)) return true;

                try
                {
                    string? parent = Path.GetDirectoryName(normalized);
                    while (!string.IsNullOrEmpty(parent))
                    {
                        if (CustomExcludedPaths.Contains(parent)) return true;
                        parent = Path.GetDirectoryName(parent);
                    }
                }
                catch { }
            }

            return false;
        }

        public static bool IsDefaultExcluded(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string lower = fullPath.ToLowerInvariant().Replace('/', '\\');

            // 1. Check for specific system files at the end of the path
            if (lower.EndsWith("pagefile.sys") || lower.EndsWith("swapfile.sys") || lower.EndsWith("hiberfil.sys"))
                return true;

            // 2. Fast check for common excluded directory names in the path segments
            string[] segments = lower.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in segments)
            {
                if (_excludedFolderNames.Contains(segment))
                    return true;
            }

            // 3. Check for specific path patterns (ensure we match whole segments)
            foreach (var pattern in _excludedPathSegments)
            {
                int index = lower.IndexOf(pattern);
                if (index != -1)
                {
                    // Check if the match is at the end of the string or followed by a separator
                    int end = index + pattern.Length;
                    if (end == lower.Length || lower[end] == '\\' || lower[end] == '/')
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}