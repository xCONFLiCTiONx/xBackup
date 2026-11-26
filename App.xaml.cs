using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace xBackup
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Ensure the process is running with full Administrator privileges
            // (protects against parent processes setting __COMPAT_LAYER=RunAsInvoker)
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    try
                    {
                        Environment.SetEnvironmentVariable("__COMPAT_LAYER", null);
                        string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = string.Join(" ", e.Args.Select(a => $"\"{a}\"")),
                                UseShellExecute = true,
                                Verb = "runas"
                            };
                            Process.Start(psi);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"xBackup requires Administrator privileges to mount and manage VHDX backup containers.\n\n{ex.Message}", "Administrator Privileges Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    Current.Shutdown();
                    return;
                }
            }

            // Enforce Single Instance Configuration
            _mutex = new Mutex(true, "SmartPersonalBackupEngineMutexUnique", out bool isNewInstance);
            if (!isNewInstance)
            {
                // Already active instance on system
                MessageBox.Show("Another instance of the Personal Backup Engine is already running.", "Engine Active", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                LogCrash("AppDomain", ex);
                MessageBox.Show($"FATAL APP DOMAIN CRASH: {ex?.Message}\n\nStack Trace:\n{ex?.StackTrace}", "Fatal Exception Caught", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            Current.DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("Dispatcher", args.Exception);
                MessageBox.Show($"FATAL UI DISPATCHER CRASH: {args.Exception.Message}\n\nStack Trace:\n{args.Exception.StackTrace}", "Fatal Exception Caught", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            base.OnStartup(e);

            bool silentMode = e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
            AppLogger.Info("APP", $"xBackup starting (silentMode={silentMode}, args='{string.Join(" ", e.Args)}').");

            var mainWindow = new MainWindow(silentMode);
            Current.MainWindow = mainWindow;

            if (!silentMode)
            {
                mainWindow.Show();
            }
            else
            {
                mainWindow.Visibility = Visibility.Hidden;
                mainWindow.Width = 0;
                mainWindow.Height = 0;
                mainWindow.ShowInTaskbar = false;
                mainWindow.Show();
                mainWindow.Hide();

                mainWindow.Width = 1040;
                mainWindow.Height = 680;
                mainWindow.ShowInTaskbar = true;

                mainWindow.ExecuteSilentScheduledBackup();
            }
        }

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string folder = Path.Combine(appData, "SmartBackupEngine");
                Directory.CreateDirectory(folder);
                string logFile = Path.Combine(folder, "crash_log.txt");
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex?.Message}\nStack Trace:\n{ex?.StackTrace}\n----------------------------------------\n";
                File.AppendAllText(logFile, logEntry);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
