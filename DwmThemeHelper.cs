using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace xBackup
{
    /// <summary>
    /// Applies the exact xPrivacyGuard Antigravity Dark Gray DWM titlebar chrome (#151518 caption, #2C2C33 border, #F4F4F5 text).
    /// </summary>
    public static class DwmThemeHelper
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyDarkWindowChrome(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == 0) return;

                int dark = 1;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

                int captionColor = 0x00181515; // #151518
                int borderColor = 0x00332C2C;  // #2C2C33
                int textColor = 0x00F5F4F4;    // #F4F4F5
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
            }
            catch { }
        }
    }
}
