using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace xBackup
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
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
                MessageBox.Show($"FATAL APP DOMAIN CRASH: {ex?.Message}\n\nStack Trace:\n{ex?.StackTrace}", "Fatal Exception Caught", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            Current.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"FATAL UI DISPATCHER CRASH: {args.Exception.Message}\n\nStack Trace:\n{args.Exception.StackTrace}", "Fatal Exception Caught", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            base.OnStartup(e);

            bool silentMode = e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase);

            var mainWindow = new MainWindow(silentMode);
            Current.MainWindow = mainWindow;

            if (!silentMode)
            {
                mainWindow.Show();
            }
            else
            {
                // To keep the app running in the system tray during silent scheduled execution,
                // we instantiate the window, but we DO NOT call .Show(). WPF windows initialized
                // in code behind require a brief invocation loop tick to correctly construct and bind
                // their Resource trees (including the tray NotifyIcon resource block).
                mainWindow.Visibility = Visibility.Hidden;

                // Trigger the window's loaded sequence explicitly to bind resources and populate the system tray icon
                mainWindow.Width = 0;
                mainWindow.Height = 0;
                mainWindow.ShowInTaskbar = false;
                mainWindow.Show();
                mainWindow.Hide();

                // Restore values back to normal state configurations so that it populates cleanly if double clicked later
                mainWindow.Width = 880;
                mainWindow.Height = 620;
                mainWindow.ShowInTaskbar = true;

                // Kick off the automated background engine
                mainWindow.ExecuteSilentScheduledBackup();
            }
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
