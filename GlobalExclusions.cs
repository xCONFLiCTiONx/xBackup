using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace xBackup
{
    public static class GlobalExclusions
    {
        public static HashSet<string> CustomExcludedPaths { get; } = new HashSet<string>();

        private static string GetConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "custom_exclusions.json");
        }

        public static void Load()
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
                                CustomExcludedPaths.Add(item.Trim().ToLower());
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static void Save()
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

        public static bool IsCustomExcluded(string lowerPath)
        {
            if (CustomExcludedPaths.Contains(lowerPath)) return true;

            try
            {
                string? parent = Path.GetDirectoryName(lowerPath);
                while (!string.IsNullOrEmpty(parent))
                {
                    if (CustomExcludedPaths.Contains(parent.ToLower())) return true;
                    parent = Path.GetDirectoryName(parent);
                }
            }
            catch { }

            return false;
        }

        public static bool IsDefaultExcluded(string fullPath)
        {
            string lower = fullPath.ToLower();

            // Explicitly exclude Phone Link, Microsoft Mobile Features, and phone sync files that trigger wireless/bluetooth/cloud sync on demand
            if (lower.Contains(@"\appdata\local\microsoft\phonelink") ||
                lower.Contains(@"\microsoft.yourphone") ||
                lower.Contains(@"\mobiledeviceconnect") ||
                lower.Contains(@"\.android") ||
                lower.Contains(@"\phone-link") ||
                lower.Contains(@"\crossdevice"))
            {
                return true;
            }

            if (lower.Contains(@"\appdata\local\temp") ||
                lower.Contains(@"\google\chrome\user data\default\cache") ||
                lower.Contains(@"\microsoft\windows\inetcache") ||
                lower.Contains(@"\discord\cache") ||
                lower.Contains(@"\code\cache") ||
                lower.Contains(@"\code\cacheddata") ||
                lower.Contains(@"\node_modules") ||
                lower.Contains(@"\programdata\package cache") ||
                lower.Contains(@"\microsoft\windows\defender\support") ||
                lower.Contains(@"\$recycle.bin") ||
                lower.Contains(@"\system volume information") ||
                lower.Contains(@"\windows\temp") ||
                lower.Contains(@"\windows\prefetch") ||
                lower.Contains(@"\windows\softwaredistribution") ||
                lower.Contains(@"\windows\installer") ||
                lower.EndsWith("pagefile.sys") ||
                lower.EndsWith("swapfile.sys") ||
                lower.EndsWith("hiberfil.sys"))
            {
                return true;
            }

            if (lower.Contains(@"\appdata\local\packages\") && lower.Contains(@"\localcache"))
            {
                return true;
            }

            if (lower.Contains(@"\bin\") || lower.Contains(@"\obj\") || lower.EndsWith(@"\bin") || lower.EndsWith(@"\obj"))
            {
                return true;
            }

            return false;
        }
    }
}
