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

        // Pre-optimized sets for fast lookup
        private static readonly HashSet<string> _excludedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".vs", ".idea", "target", "bin", "obj", "build", "$recycle.bin",
            "system volume information", "prefetch", "softwaredistribution", "installer",
            "inetcache", "webcache", "dxcache", "d3dscache", "crashrpt", "_cache", "cache",
            "cacheddata", "code cache", "gpucache", "assetcache"
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
            @"\crossdevice"
        };

        private static string GetConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "custom_exclusions.json");
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    string path = GetConfigFilePath();
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
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
                    string path = GetConfigFilePath();
                    var list = new List<string>(CustomExcludedPaths);
                    string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(path, json);
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

            // 3. Check for specific path patterns (prefixes/substrings)
            foreach (var pattern in _excludedPathSegments)
            {
                if (lower.Contains(pattern))
                    return true;
            }

            return false;
        }
    }
}