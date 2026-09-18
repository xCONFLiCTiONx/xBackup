using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using xBackup.Models;

namespace xBackup
{
    public partial class MainWindow : Window
    {
        // Windows Power Management API P/Invoke definitions to keep PC awake
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

        private string? _currentFilePath;
        private static readonly Brush LinkBrush = (Brush)new BrushConverter().ConvertFromString("#3794C0")!;
        private static readonly Brush DefaultBrush = (Brush)new BrushConverter().ConvertFromString("#D4D4D4")!;
        private string _destinationRoot = @"F:\Backup\Home-PC";
        private bool _isProcessing = false;
        private bool _isVhdxMountedManual = false;
        private bool _isClosingInProgress = false;
        private readonly bool _isSilentMode = false;
        private H.NotifyIcon.TaskbarIcon? _notifyIcon;
        private readonly List<string> _errorDetailsReport = new List<string>();
        private System.Threading.CancellationTokenSource? _cts;

        public class BackupCheckpoint
        {
            public DateTime Timestamp { get; set; }
            public List<string> FilesToProcess { get; set; } = new List<string>();
            public int CurrentIndex { get; set; }
            public string DestinationRoot { get; set; } = string.Empty;
            public long BackedUpCount { get; set; }
            public long UpToDateCount { get; set; }
            public long LockedCount { get; set; }
            public long TotalBytesMirrored { get; set; }
        }

        public class RelayCommand : System.Windows.Input.ICommand
        {
            private readonly Action _execute;
            public RelayCommand(Action execute) => _execute = execute;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _execute();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        public class ScopeDisplayItem
        {
            public string DisplayText { get; set; } = string.Empty;
            public bool IsDrive { get; set; }
        }

        public System.Windows.Input.ICommand ShowWindowCommand { get; }

        private static void PreloadDiskpartUtility()
        {
            Task.Run(() =>
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "diskpart.exe",
                        Arguments = "?", // standard help command to warm up process initialization cache
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        process?.WaitForExit();
                    }
                }
                catch { }
            });
        }

        public MainWindow(bool silentMode)
        {
            _isSilentMode = silentMode;
            ShowWindowCommand = new RelayCommand(RestoreFromTray);
            InitializeComponent();
            DataContext = this;

            // Preload diskpart worker subsystems asynchronously on start
            PreloadDiskpartUtility();

            // Load saved coordinates from configuration cache if existing
            LoadWindowPlacementSettings();
            GlobalExclusions.Load();
            RefreshBackupScopesDisplay();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void RefreshBackupScopesDisplay()
        {
            var scopes = new List<ScopeDisplayItem>();

            if (GlobalExclusions.SelectedDrives.Count == 0)
            {
                scopes.Add(new ScopeDisplayItem { DisplayText = "No drives selected for backup.", IsDrive = false });
            }
            else
            {
                foreach (var drive in GlobalExclusions.SelectedDrives)
                {
                    string label = "";
                    try
                    {
                        var di = new System.IO.DriveInfo(drive);
                        label = di.VolumeLabel;
                    }
                    catch { }

                    string cleanDrive = drive.TrimEnd('\\');
                    string displayName = string.IsNullOrEmpty(label) ? $"Local Disk ({cleanDrive})" : $"{label} ({cleanDrive})";
                    scopes.Add(new ScopeDisplayItem { DisplayText = displayName, IsDrive = true });
                }
            }

            ItemsScopes.ItemsSource = scopes;
        }

        private void BtnBrowseDest_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Backup Destination Folder Root",
                InitialDirectory = _destinationRoot
            };

            if (dialog.ShowDialog() == true)
            {
                TxtDestRoot.Text = dialog.FolderName;
                _destinationRoot = dialog.FolderName;
            }
        }

        private void BtnConfigureExclusions_Click(object sender, RoutedEventArgs e)
        {
            var settingsWin = new SettingsWindow { Owner = this };
            settingsWin.ShowDialog();
            RefreshBackupScopesDisplay();
        }

        private class WindowPlacementData
        {
            public double Left { get; set; } = -1;
            public double Top { get; set; } = -1;
            public double Width { get; set; } = 880;
            public double Height { get; set; } = 620;
            public bool IsMaximized { get; set; } = false;
        }

        private string GetConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "window_placement.json");
        }

        private string GetCheckpointFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "backup_checkpoint.json");
        }

        private void SaveCheckpoint(BackupCheckpoint checkpoint)
        {
            try
            {
                string path = GetCheckpointFilePath();
                string json = JsonSerializer.Serialize(checkpoint);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private BackupCheckpoint? LoadCheckpoint()
        {
            try
            {
                string path = GetCheckpointFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var checkpoint = JsonSerializer.Deserialize<BackupCheckpoint>(json);
                    // Only return if it's less than 48 hours old
                    if (checkpoint != null && (DateTime.Now - checkpoint.Timestamp).TotalHours < 48)
                    {
                        return checkpoint;
                    }
                }
            }
            catch { }
            return null;
        }

        private void DeleteCheckpoint()
        {
            try
            {
                string path = GetCheckpointFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private void LoadWindowPlacementSettings()
        {
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var data = JsonSerializer.Deserialize<WindowPlacementData>(json);
                    if (data != null)
                    {
                        if (data.IsMaximized)
                        {
                            WindowState = WindowState.Maximized;
                        }

                        if (data.Left >= 0 && data.Top >= 0 && data.Left + 100 < SystemParameters.VirtualScreenWidth && data.Top + 100 < SystemParameters.VirtualScreenHeight)
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual;
                            Left = data.Left;
                            Top = data.Top;
                            Width = data.Width;
                            Height = data.Height;
                            return;
                        }
                    }
                }
            }
            catch { }
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void SaveWindowPlacementSettings()
        {
            try
            {
                string path = GetConfigFilePath();
                var data = new WindowPlacementData();

                if (WindowState == WindowState.Maximized)
                {
                    data.IsMaximized = true;
                    data.Left = RestoreBounds.Left;
                    data.Top = RestoreBounds.Top;
                    data.Width = RestoreBounds.Width;
                    data.Height = RestoreBounds.Height;
                }
                else
                {
                    data.IsMaximized = false;
                    data.Left = Left;
                    data.Top = Top;
                    data.Width = Width;
                    data.Height = Height;
                }

                string json = JsonSerializer.Serialize(data);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Initialize system tray notify icon
                _notifyIcon = (H.NotifyIcon.TaskbarIcon)FindResource("NotifyIcon");

                // Route the native tray double-click event to open the interface monitor GUI cleanly
                _notifyIcon.TrayMouseDoubleClick += (s, args) => RestoreFromTray();

                _notifyIcon.ForceCreate();
            }
            catch (Exception iconEx)
            {
                MessageBox.Show($"SYSTEM TRAY ICON CRASH: {iconEx.Message}\n\nInner details: {iconEx.InnerException?.Message}", "Tray Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            try
            {
                UpdateTaskSchedulerStatus();
            }
            catch (Exception taskEx)
            {
                MessageBox.Show($"SCHEDULER STATUS UTILITY CRASH: {taskEx.Message}", "Scheduler Check Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Detect if VHDX is already mounted at startup
            string initialVhdxPath = Path.Combine(TxtDestRoot.Text, "BackupDev.vhdx");
            Task.Run(() =>
            {
                string status = CheckVhdxMountStatus(initialVhdxPath);
                if (status != "NotMounted")
                {
                    Dispatcher.Invoke(() =>
                    {
                        _isVhdxMountedManual = true;
                        BtnToggleMount.Content = "Eject Drive";
                        BtnToggleMount.Background = new SolidColorBrush(Color.FromRgb(180, 50, 50));
                        string driveLetter = status == "Mounted" ? "" : status;
                        AppendLog($"Detected existing VHDX mount {(string.IsNullOrEmpty(driveLetter) ? "" : "at drive " + driveLetter)}", Brushes.LightGreen);
                    });
                }
            });
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosingInProgress) return;

            // Save coordinates on exit
            SaveWindowPlacementSettings();

            // If the user clicks close button while background engine processing, minimize to system tray instead of aborting
            if (_isProcessing)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon?.ShowNotification("Engine Active", "The Backup or Restore execution is still running in the background system tray.");
            }
            else
            {
                if (_isVhdxMountedManual)
                {
                    e.Cancel = true;
                    _isClosingInProgress = true;

                    TxtStatus.Text = "Engine Status: Shutting Down...";
                    TxtStatus.Foreground = Brushes.Yellow;
                    PrgWaiting.Visibility = Visibility.Visible;

                    string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");
                    await Task.Run(() =>
                    {
                        try
                        {
                            DismountVhdx(vhdxPath);
                        }
                        catch { }
                    });

                    _notifyIcon?.Dispose();
                    Close();
                }
                else
                {
                    _notifyIcon?.Dispose();
                }
            }
        }

        private void RestoreFromTray()
        {
            Show();

            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var data = JsonSerializer.Deserialize<WindowPlacementData>(json);
                    if (data != null && data.IsMaximized)
                    {
                        WindowState = WindowState.Maximized;
                        Activate();
                        return;
                    }
                }
            }
            catch { }

            WindowState = WindowState.Normal;
            Activate();
        }

        private void MenuShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                var res = MessageBox.Show(
                    "Backup engine is actively processing entries. Are you sure you want to force terminate the application operation?",
                    "Confirm Forced Termination",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );
                if (res != MessageBoxResult.Yes) return;
            }

            if (_isVhdxMountedManual)
            {
                try
                {
                    string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");
                    DismountVhdx(vhdxPath);
                }
                catch { }
            }

            _isProcessing = false;
            SaveWindowPlacementSettings();
            _notifyIcon?.Dispose();

            Environment.Exit(0);
        }

        private void UpdateTaskSchedulerStatus()
        {
            bool exists = TaskSchedulerHelper.DoesTaskExist();
            if (exists)
            {
                BtnScheduleTask.Content = "Unregister Backup Task";
            }
            else
            {
                BtnScheduleTask.Content = "Register Midnight Wake Task";
            }
        }

        private void BtnScheduleTask_Click(object sender, RoutedEventArgs e)
        {
            if (TaskSchedulerHelper.DoesTaskExist())
            {
                TaskSchedulerHelper.DeleteBackupTask();
                UpdateTaskSchedulerStatus();
            }
            else
            {
                if (!TaskSchedulerHelper.IsRunningAsAdmin())
                {
                    MessageBox.Show("Modifying the Windows Task Scheduler requires high privileges. Please re-run the terminal or application window as Administrator.", "Admin Privileges Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool success = TaskSchedulerHelper.CreateDailyBackupTask();
                UpdateTaskSchedulerStatus();

                if (!success)
                {
                    MessageBox.Show("Failed to create the task configuration. Make sure you are running as administrator and executing the actual standalone compiled .exe file.", "Scheduling Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public async void ExecuteSilentScheduledBackup()
        {
            DeleteCheckpoint();

            SetUiState(processing: true);

            _notifyIcon?.ShowNotification("Midnight Backup Started", "The personal incremental backup session has successfully initialized in the system tray.");

            _cts = new System.Threading.CancellationTokenSource();
            try
            {
                await Task.Run(() => RunBackupEngine(_destinationRoot, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                AppendLog("Scheduled backup cancelled by user.", Brushes.Orange);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetUiState(processing: false);
            }

            if (Visibility != Visibility.Visible)
            {
                _notifyIcon?.Dispose();
                Application.Current.Shutdown();
            }
        }

        private async void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;

            // Sync from GUI configurations dynamically
            _destinationRoot = TxtDestRoot.Text;

            BackupCheckpoint? checkpoint = LoadCheckpoint();
            bool resume = false;
            if (checkpoint != null && checkpoint.DestinationRoot == _destinationRoot)
            {
                var result = MessageBox.Show(
                    $"An interrupted backup from {checkpoint.Timestamp:g} was found.\nWould you like to resume from file {checkpoint.CurrentIndex:N0} of {checkpoint.FilesToProcess.Count:N0}?",
                    "Resume Backup?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );
                resume = result == MessageBoxResult.Yes;
            }

            if (!resume)
            {
                DeleteCheckpoint();
                checkpoint = null;
            }

            SetUiState(processing: true);
            if (!resume)
            {
                RtbLog.Document.Blocks.Clear();
                PrgBar.Value = 0;
            }

            _cts = new System.Threading.CancellationTokenSource();

            try
            {
                await Task.Run(() => RunBackupEngine(_destinationRoot, _cts.Token, checkpoint));
            }
            catch (OperationCanceledException)
            {
                AppendLog("Backup operation was cancelled by the user.", Brushes.Orange);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetUiState(processing: false);
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing && _cts != null)
            {
                AppendLog("Issue Stop Signal... Waiting for engine to cycle down.", Brushes.Yellow);
                PrgWaiting.Visibility = Visibility.Visible;
                _cts.Cancel();
                BtnStop.IsEnabled = false;
            }
        }

        private async void BtnToggleMount_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;

            // Sync from GUI configurations dynamically
            _destinationRoot = TxtDestRoot.Text;

            string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");

            if (!_isVhdxMountedManual)
            {
                BtnToggleMount.IsEnabled = false;
                BtnBackup.IsEnabled = false;
                PrgWaiting.Visibility = Visibility.Visible;
                try
                {
                    AppendLog("Checking VHDX container presence...", Brushes.DeepSkyBlue);
                    if (!File.Exists(vhdxPath))
                    {
                        AppendLog("VHDX container not found. Initializing automated setup...", Brushes.Orange);
                        await Task.Run(() => EnsureVhdxExists(vhdxPath));
                        AppendLog("VHDX container created and formatted successfully as ReFS Dev Drive.", Brushes.LightGreen);
                    }

                    AppendLog("Checking if VHDX is already mounted...", Brushes.DeepSkyBlue);
                    string status = await Task.Run(() => CheckVhdxMountStatus(vhdxPath));
                    string driveLetter = "";

                    if (status != "NotMounted")
                    {
                        driveLetter = status == "Mounted" ? "" : status;
                        AppendLog($"VHDX is already mounted {(string.IsNullOrEmpty(driveLetter) ? "" : "at drive " + driveLetter)}", Brushes.LightGreen);
                    }
                    else
                    {
                        AppendLog("Mounting VHDX backup drive...", Brushes.DeepSkyBlue);
                        driveLetter = await Task.Run(() => MountVhdxAndGetLetter(vhdxPath));
                        AppendLog($"VHDX successfully mounted at drive {driveLetter}", Brushes.LightGreen);
                    }

                    _isVhdxMountedManual = true;
                    BtnToggleMount.Content = "Eject Drive";
                    BtnToggleMount.Background = new SolidColorBrush(Color.FromRgb(180, 50, 50));

                    if (!string.IsNullOrEmpty(driveLetter))
                    {
                        Process.Start("explorer.exe", driveLetter);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Mount failure: {ex.Message}", Brushes.Red);
                    MessageBox.Show($"Failed to mount backup drive: {ex.Message}", "Mount Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnToggleMount.IsEnabled = true;
                    PrgWaiting.Visibility = Visibility.Collapsed;
                    // Always re-enable backup button after mount attempt finishes,
                    // since the engine can auto-mount if needed.
                    BtnBackup.IsEnabled = true;
                }
            }
            else
            {
                BtnToggleMount.IsEnabled = false;
                PrgWaiting.Visibility = Visibility.Visible;
                try
                {
                    AppendLog("Dismounting VHDX backup drive...", Brushes.DeepSkyBlue);
                    await Task.Run(() => DismountVhdx(vhdxPath));
                    AppendLog("VHDX drive successfully dismounted and locked away.", Brushes.LightGreen);

                    _isVhdxMountedManual = false;
                    BtnToggleMount.Content = "Mount Backup Drive";
                    BtnToggleMount.Background = new SolidColorBrush(Color.FromRgb(62, 62, 66));
                    BtnBackup.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    AppendLog($"Dismount failure: {ex.Message}", Brushes.Red);
                    MessageBox.Show($"Failed to dismount backup drive: {ex.Message}", "Dismount Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnToggleMount.IsEnabled = true;
                    PrgWaiting.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void SetUiState(bool processing)
        {
            _isProcessing = processing;
            BtnBackup.IsEnabled = !processing;
            BtnRestore.IsEnabled = !processing;
            BtnToggleMount.IsEnabled = !processing;
            BtnStop.IsEnabled = processing;
            TxtStatus.Text = processing ? "Engine Status: Active" : "Engine Status: Ready";
            TxtStatus.Foreground = processing ? new SolidColorBrush(Color.FromRgb(220, 202, 170)) : new SolidColorBrush(Color.FromRgb(78, 201, 176));
        }

        private string GetBackupMountedDrive()
        {
            string vhdxPath = Path.Combine(TxtDestRoot.Text, "BackupDev.vhdx");
            string status = CheckVhdxMountStatus(vhdxPath);
            return (status != "NotMounted" && status != "Mounted") ? status : string.Empty;
        }

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;

            _destinationRoot = TxtDestRoot.Text;
            string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");

            string mountedDrive = "";
            try
            {
                AppendLog("Checking backup drive status for restore...", Brushes.DeepSkyBlue);
                mountedDrive = await Task.Run(() => CheckVhdxMountStatus(vhdxPath));

                if (mountedDrive == "NotMounted")
                {
                    AppendLog("Mounting backup drive for restore...", Brushes.DeepSkyBlue);
                    mountedDrive = await Task.Run(() => MountVhdxAndGetLetter(vhdxPath));
                    _isVhdxMountedManual = true;
                    Dispatcher.Invoke(() =>
                    {
                        BtnToggleMount.Content = "Eject Drive";
                        BtnToggleMount.Background = new SolidColorBrush(Color.FromRgb(180, 50, 50));
                    });

                    // VHDX mounting on ReFS can take several seconds for the filesystem to be fully ready for SQLite
                    // We increase this to 3 seconds to avoid transient I/O errors immediately after mount.
                    await Task.Delay(3000);
                }
                else if (mountedDrive == "Mounted")
                {
                     mountedDrive = await Task.Run(() => MountVhdxAndGetLetter(vhdxPath));
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Mount failure: {ex.Message}", Brushes.Red);
                MessageBox.Show($"Failed to mount backup drive: {ex.Message}", "Mount Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string dbPath = Path.Combine(mountedDrive, "BackupCatalog.db");
            if (!File.Exists(dbPath))
            {
                MessageBox.Show("Backup catalog not found in the storage container.", "Restore Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                using var catalog = new BackupCatalog(dbPath);
                var restoreWindow = new RestoreWindow(catalog, mountedDrive) { Owner = this };

                if (restoreWindow.ShowDialog() != true) return;

                var selectedItems = restoreWindow.SelectedNodes
                    .Select(n => (n.FullPath, n.Version!))
                    .ToList();

                SetUiState(processing: true);
                RtbLog.Document.Blocks.Clear();
                PrgBar.Value = 0;

                _cts = new System.Threading.CancellationTokenSource();

                await Task.Run(() => RunCatalogRestoreEngine(mountedDrive, selectedItems, restoreWindow.TargetPath, restoreWindow.FullRestore, restoreWindow.Overwrite, _cts.Token));
            }
            catch (Exception ex)
            {
                AppendLog($"Restore initialization failed: {ex.Message}", Brushes.Red);
                MessageBox.Show($"Could not initialize restore: {ex.Message}", "Restore Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                SetUiState(processing: false);
            }
        }

        private void RunCatalogRestoreEngine(string mountedDrive, List<(string sourcePath, FileVersion version)> filesToRestore, string? targetPath, bool fullRestore, bool overwrite, System.Threading.CancellationToken cancellationToken)
        {
            var restoreErrors = new List<string>();
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                AppendLog($"Initializing Catalog Restore for {filesToRestore.Count:N0} files...", Brushes.DeepSkyBlue);

                string dbPath = Path.Combine(mountedDrive, "BackupCatalog.db");
                using var catalog = new BackupCatalog(dbPath);
                var engine = new RestoreEngine(catalog, mountedDrive);

                int errorCount = 0;
                engine.RestoreFiles(filesToRestore, targetPath, fullRestore, overwrite, cancellationToken, (file, current, total) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _currentFilePath = file;
                        TxtProgressDetails.Text = $"[{current:N0}/{total:N0}] Restoring: {Path.GetFileName(file)}";
                        TxtProgressDetails.Foreground = LinkBrush;
                        if (current % 100 == 0 || current == total)
                        {
                            PrgBar.Maximum = total;
                            PrgBar.Value = current;
                            LblScanned.Text = total.ToString("N0");
                            LblBackedUp.Text = (current - errorCount).ToString("N0");
                            LblLocked.Text = errorCount.ToString("N0");
                        }
                    });
                }, (error) =>
                {
                    errorCount++;
                    restoreErrors.Add(error);
                });

                if (restoreErrors.Count > 0)
                {
                    AppendLog($"Restore finished with {restoreErrors.Count} errors.", Brushes.Yellow);
                    foreach (var err in restoreErrors.Take(50)) // Don't spam the log too much
                    {
                        AppendLog($"-> {err}", Brushes.Red);
                    }
                }
                else
                {
                    AppendLog("Catalog restore successfully completed.", Brushes.LightGreen);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Restore engine failure: {ex.Message}", Brushes.Red);
            }
            finally
            {
                SetThreadExecutionState(ES_CONTINUOUS);
                Dispatcher.Invoke(() =>
                {
                    TxtProgressDetails.Text = "Restore Operation Finished";
                    _currentFilePath = null;
                    TxtProgressDetails.Foreground = DefaultBrush;
                });
            }
        }

        private void RunLegacyRestoreEngine(string snapshotPath, string targetPath, bool overwrite, System.Threading.CancellationToken cancellationToken)
        {
            List<string> filesToRestore = new List<string>();
            long restoredCount = 0;
            long skippedCount = 0;
            long errorCount = 0;
            long totalBytesRestored = 0;

            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                AppendLog("Initializing Legacy Restore Lifecycle...", Brushes.DeepSkyBlue);

                _errorDetailsReport.Clear();
                long totalScannedCount = 0;

                AppendLog("Scanning legacy snapshot contents...", Brushes.DeepSkyBlue);
                DiscoverFilesForRestore(snapshotPath, filesToRestore, ref totalScannedCount, cancellationToken);

                Dispatcher.Invoke(() =>
                {
                    LblScanned.Text = filesToRestore.Count.ToString("N0");
                    PrgBar.Maximum = filesToRestore.Count;
                    TxtProgressDetails.Text = $"Found {filesToRestore.Count:N0} files to restore.";
                    _currentFilePath = null;
                    TxtProgressDetails.Foreground = DefaultBrush;
                });

                for (int i = 0; i < filesToRestore.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var file = filesToRestore[i];
                    int currentIndex = i + 1;

                    string relativePath = Path.GetRelativePath(snapshotPath, file);
                    string destPath = Path.Combine(targetPath, relativePath);

                    Dispatcher.Invoke(() =>
                    {
                        _currentFilePath = file;
                        TxtProgressDetails.Text = $"[{currentIndex:N0}/{filesToRestore.Count:N0}] Restoring: {Path.GetFileName(file)}";
                        TxtProgressDetails.Foreground = LinkBrush;
                        if (currentIndex % 100 == 0 || currentIndex == filesToRestore.Count)
                        {
                            PrgBar.Value = currentIndex;
                            LblBackedUp.Text = restoredCount.ToString("N0");
                            LblUpToDate.Text = skippedCount.ToString("N0");
                            LblLocked.Text = errorCount.ToString("N0");
                            LblSavings.Text = FormatBytes(totalBytesRestored);
                        }
                    });

                    try
                    {
                        FileInfo sourceFi = new FileInfo(file);
                        if (File.Exists(destPath))
                        {
                            if (!overwrite)
                            {
                                skippedCount++;
                                continue;
                            }
                        }

                        string? parentDir = Path.GetDirectoryName(destPath);
                        if (parentDir != null && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        File.Copy(file, destPath, overwrite: true);
                        File.SetLastWriteTimeUtc(destPath, sourceFi.LastWriteTimeUtc);

                        restoredCount++;
                        totalBytesRestored += sourceFi.Length;

                        if (restoredCount <= 100 || sourceFi.Length > 50 * 1024 * 1024)
                        {
                            AppendLog($"[Restore] {Path.GetFileName(file)} ({FormatBytes(sourceFi.Length)})", Brushes.LightGreen);
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        _errorDetailsReport.Add($"-> {file} | Restore Failed: {ex.Message}");
                        if (errorCount <= 50)
                        {
                            AppendLog($"Restore failed for: {Path.GetFileName(file)} ({ex.Message})", Brushes.Yellow);
                        }
                    }
                }

                AppendLog("----------------------------------------------------------------", Brushes.Gray);
                AppendLog($"Restore execution status updated.", Brushes.DeepSkyBlue);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Restore operation was cancelled by the user.", Brushes.Orange);
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"Restore engine failure: {ex.Message}", Brushes.Red);
            }
            finally
            {
                WriteRestoreReport(snapshotPath, targetPath, overwrite, filesToRestore.Count, restoredCount, skippedCount, errorCount, totalBytesRestored);
                SetThreadExecutionState(ES_CONTINUOUS);
            }
        }

        private void WriteRestoreReport(string snapshotPath, string targetPath, bool overwrite, long totalScannedCount, long restoredCount, long skippedCount, long errorCount, long totalBytesRestored)
        {
            try
            {
                string historyDir = Path.Combine(targetPath, "RestoreLogs");
                if (!Directory.Exists(historyDir)) Directory.CreateDirectory(historyDir);

                string logFileName = $"RestoreReport_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
                string fullLogPath = Path.Combine(historyDir, logFileName);

                using (StreamWriter sw = new StreamWriter(fullLogPath, false, System.Text.Encoding.UTF8))
                {
                    sw.WriteLine("==========================================================================");
                    sw.WriteLine($"PERSONAL BACKUP ENGINE RESTORE EXECUTION REPORT");
                    sw.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sw.WriteLine("==========================================================================");
                    sw.WriteLine($"Snapshot Path: {snapshotPath}");
                    sw.WriteLine($"Restore Path:  {targetPath}");
                    sw.WriteLine($"Overwrite:     {overwrite}");
                    sw.WriteLine("--------------------------------------------------------------------------");
                    sw.WriteLine($"Total Files Scanned in Snapshot: {totalScannedCount:N0}");
                    sw.WriteLine($"Successfully Restored:           {restoredCount:N0}");
                    sw.WriteLine($"Skipped (Existing):              {skippedCount:N0}");
                    sw.WriteLine($"Errors encountered:              {errorCount:N0}");
                    sw.WriteLine($"Total Sizing Restored:           {FormatBytes(totalBytesRestored)}");
                    sw.WriteLine("==========================================================================");

                    if (_errorDetailsReport.Count > 0)
                    {
                        sw.WriteLine();
                        sw.WriteLine("RESTORE ERRORS / BYPASSED FILES DETAILS:");
                        sw.WriteLine("--------------------------------------------------------------------------");
                        foreach (var errItem in _errorDetailsReport)
                        {
                            sw.WriteLine(errItem);
                        }
                    }
                }
                AppendLog($"Restore session report saved: RestoreLogs\\{logFileName}", Brushes.LightSeaGreen);
            }
            catch (Exception historyEx)
            {
                AppendLog($"Warning: Could not compile restore log file ({historyEx.Message})", Brushes.Orange);
            }

            Dispatcher.Invoke(() =>
            {
                TxtProgressDetails.Text = "Restore Operation Finished";
                _currentFilePath = null;
                TxtProgressDetails.Foreground = DefaultBrush;
                LblBackedUp.Text = restoredCount.ToString("N0");
                LblUpToDate.Text = skippedCount.ToString("N0");
                LblLocked.Text = errorCount.ToString("N0");
                LblSavings.Text = FormatBytes(totalBytesRestored);
            });
        }

        private void DiscoverFilesForRestore(string currentDir, List<string> files, ref long scannedCount, System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string[] dirFiles = Directory.GetFiles(currentDir);
                foreach (var f in dirFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedCount++;
                    files.Add(f);
                }

                string[] subDirs = Directory.GetDirectories(currentDir);
                foreach (var d in subDirs)
                {
                    DiscoverFilesForRestore(d, files, ref scannedCount, cancellationToken);
                }
            }
            catch { }
        }

        private void RunBackupEngine(string destRoot, System.Threading.CancellationToken cancellationToken, BackupCheckpoint? checkpoint = null)
        {
            string vhdxPath = Path.Combine(destRoot, "BackupDev.vhdx");
            string mountedDrive = string.Empty;
            bool newlyMounted = false;

            List<string> filesToProcess = new List<string>();
            long backedUpCount = checkpoint?.BackedUpCount ?? 0;
            long upToDateCount = checkpoint?.UpToDateCount ?? 0;
            long lockedCount = checkpoint?.LockedCount ?? 0;
            long totalBytesMirrored = checkpoint?.TotalBytesMirrored ?? 0;
            long purgedFilesCount = 0;

            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                AppendLog(checkpoint == null ? "Initializing Automated Snapshot Lifecycle..." : "Resuming Snapshot Lifecycle...", Brushes.DeepSkyBlue);

                if (!Directory.Exists(destRoot))
                {
                    Directory.CreateDirectory(destRoot);
                }

                if (!File.Exists(vhdxPath))
                {
                    Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Visible);
                    AppendLog("VHDX storage container absent. Building 100 GB dynamic image...", Brushes.Orange);
                    EnsureVhdxExists(vhdxPath);
                    AppendLog("VHDX container created and initialized cleanly.", Brushes.LightGreen);
                }

                AppendLog("Performing strict on-demand VHDX mounting...", Brushes.DeepSkyBlue);
                Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Visible);
                mountedDrive = MountVhdxAndGetLetter(vhdxPath);
                newlyMounted = true;
                AppendLog($"VHDX dynamically attached onto drive {mountedDrive}", Brushes.LightGreen);
                Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Collapsed);

                // Allow filesystem to stabilize (ReFS mounting latency)
                System.Threading.Thread.Sleep(2000);

                // Initialize Catalog
                string dbPath = Path.Combine(mountedDrive, "BackupCatalog.db");
                using (var catalog = new BackupCatalog(dbPath))
                {
                    catalog.MarkAbandonedSnapshotsFailed();

                    // --- Establish Snapshot Target Architecture ---
                    string nowString = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    string snapshotFolderName = $"Snapshot_{nowString}";
                    string activeSnapshotDir = Path.Combine(mountedDrive, snapshotFolderName);

                    var snapshot = catalog.CreateSnapshot(DateTime.Now);
                    Directory.CreateDirectory(activeSnapshotDir);
                    AppendLog($"Created unique daily snapshot target folder: {snapshotFolderName}", Brushes.DeepSkyBlue);

                    _errorDetailsReport.Clear();
                    long totalScannedCount = 0;

                    if (checkpoint != null)
                    {
                        filesToProcess = checkpoint.FilesToProcess;
                        AppendLog($"Resuming discovery: {filesToProcess.Count:N0} files loaded from checkpoint.", Brushes.LightGreen);
                    }
                    else
                    {
                        AppendLog("Commencing deep filesystem discovery phase...", Brushes.DeepSkyBlue);
                        var sourcePaths = new List<string>(GlobalExclusions.SelectedDrives);

                        foreach (var sourceRoot in sourcePaths)
                        {
                            if (!Directory.Exists(sourceRoot))
                            {
                                AppendLog($"Source directory absent, skipping scope: {sourceRoot}", Brushes.Yellow);
                                continue;
                            }
                            AppendLog($"Scanning scope: {sourceRoot}...", Brushes.Gray);
                            DiscoverFilesRecursively(sourceRoot, filesToProcess, ref totalScannedCount, cancellationToken);
                        }
                        AppendLog($"Discovery finished. Total files matched: {filesToProcess.Count}.", Brushes.LightGreen);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        LblScanned.Text = filesToProcess.Count.ToString("N0");
                        PrgBar.Maximum = filesToProcess.Count;
                    });

                    int startIndex = checkpoint?.CurrentIndex ?? 0;
                    var seenFilesIds = new HashSet<int>();

                    for (int i = startIndex; i < filesToProcess.Count; i++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            if (!_isSilentMode)
                            {
                                SaveCheckpoint(new BackupCheckpoint
                                {
                                    Timestamp = DateTime.Now,
                                    FilesToProcess = filesToProcess,
                                    CurrentIndex = i,
                                    DestinationRoot = destRoot,
                                    BackedUpCount = backedUpCount,
                                    UpToDateCount = upToDateCount,
                                    LockedCount = lockedCount,
                                    TotalBytesMirrored = totalBytesMirrored
                                });
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var file = filesToProcess[i];
                        int currentIndex = i + 1;

                        Dispatcher.Invoke(() =>
                        {
                            _currentFilePath = file;
                            TxtProgressDetails.Text = $"[{currentIndex:N0}/{filesToProcess.Count:N0}] Checking: {Path.GetFileName(file)}";
                            TxtProgressDetails.Foreground = LinkBrush;
                            if (currentIndex % 100 == 0 || currentIndex == filesToProcess.Count)
                            {
                                PrgBar.Value = currentIndex;
                                LblBackedUp.Text = backedUpCount.ToString("N0");
                                LblUpToDate.Text = upToDateCount.ToString("N0");
                                LblLocked.Text = lockedCount.ToString("N0");
                                LblSavings.Text = FormatBytes(totalBytesMirrored);
                            }
                        });

                        FileInfo sourceFi;
                        try
                        {
                            sourceFi = new FileInfo(file);
                            if (!sourceFi.Exists) continue;
                        }
                        catch
                        {
                            lockedCount++;
                            continue;
                        }

                        var catalogFile = catalog.GetOrCreateFile(file);
                        seenFilesIds.Add(catalogFile.Id);
                        var latestVersion = catalog.GetLatestVersion(catalogFile.Id);

                        bool needsCopy = true;
                        ChangeType changeType = ChangeType.New;

                        if (latestVersion != null && latestVersion.ChangeType != ChangeType.Deleted)
                        {
                            // Diagnostic logging for the first few files to identify why incremental might be failing
                            if (currentIndex <= 10)
                            {
                                AppendLog($"[DEBUG] Incremental Check: {Path.GetFileName(file)}", Brushes.Cyan);
                                AppendLog($"[DEBUG]   Prior: Snap={latestVersion.SnapshotId}, Size={latestVersion.Size}, Time={latestVersion.LastWriteUtc:O}", Brushes.Gray);
                                AppendLog($"[DEBUG]   Curr:  Size={sourceFi.Length}, Time={sourceFi.LastWriteTimeUtc:O}", Brushes.Gray);
                            }

                            // Robust comparison: Ensure both are treated as UTC and compare ticks to avoid precision issues
                            bool sizeMatch = latestVersion.Size == sourceFi.Length;
                            bool dateMatch = latestVersion.LastWriteUtc.ToUniversalTime().Ticks == sourceFi.LastWriteTimeUtc.Ticks;

                            if (sizeMatch && dateMatch)
                            {
                                needsCopy = false;
                                if (currentIndex <= 10) AppendLog($"[DEBUG]   Result: UNCHANGED", Brushes.LightGreen);
                            }
                            else
                            {
                                changeType = ChangeType.Modified;
                                if (currentIndex <= 10) AppendLog($"[DEBUG]   Result: MODIFIED (SizeMatch={sizeMatch}, DateMatch={dateMatch})", Brushes.Yellow);
                            }
                        }
                        else
                        {
                            if (currentIndex <= 10) AppendLog($"[DEBUG] Incremental Check: {Path.GetFileName(file)} -> RESULT: NEW", Brushes.White);
                        }

                        if (!needsCopy)
                        {
                            upToDateCount++;
                            continue;
                        }

                        string relativeStructurePath = MapToBackupPath(file);
                        string destFilePath = Path.Combine(activeSnapshotDir, relativeStructurePath);
                        string tempFilePath = destFilePath + ".tmp";

                        try
                        {
                            string? parentDir = Path.GetDirectoryName(destFilePath);
                            if (parentDir != null && !Directory.Exists(parentDir))
                            {
                                Directory.CreateDirectory(parentDir);
                            }

                            File.Copy(file, tempFilePath, overwrite: true);
                            var tempFi = new FileInfo(tempFilePath);
                            if (!tempFi.Exists || tempFi.Length != sourceFi.Length)
                            {
                                throw new IOException("Verification failed: Copied file size does not match source file size.");
                            }

                            File.Move(tempFilePath, destFilePath, overwrite: true);
                            File.SetLastWriteTimeUtc(destFilePath, sourceFi.LastWriteTimeUtc);

                            // Record in catalog
                            catalog.AddFileVersion(new FileVersion
                            {
                                FileId = catalogFile.Id,
                                SnapshotId = snapshot.Id,
                                BackupPath = Path.Combine(snapshotFolderName, relativeStructurePath),
                                Size = sourceFi.Length,
                                LastWriteUtc = sourceFi.LastWriteTimeUtc,
                                ChangeType = changeType
                            });

                            totalBytesMirrored += sourceFi.Length;
                            backedUpCount++;
                        }
                        catch (Exception ex)
                        {
                            try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                            lockedCount++;
                            _errorDetailsReport.Add($"-> {file} | Reason: {ex.Message}");
                        }
                    }

                    DeleteCheckpoint();

                    // --- Deletion Tracking Phase ---
                    AppendLog("Commencing scope-aware deletion tracking phase...", Brushes.DeepSkyBlue);
                    var allFiles = catalog.GetAllFiles();
                    foreach (var f in allFiles)
                    {
                        if (!seenFilesIds.Contains(f.Id))
                        {
                            // Check if this file IS in current scope (drives) and NOT excluded
                            bool inScope = GlobalExclusions.SelectedDrives.Any(d => f.SourcePath.StartsWith(d, StringComparison.OrdinalIgnoreCase));
                            if (inScope && !IsPathExcluded(f.SourcePath))
                            {
                                // It's in scope but we didn't see it -> Deleted
                                var lastV = catalog.GetLatestVersion(f.Id);
                                if (lastV != null && lastV.ChangeType != ChangeType.Deleted)
                                {
                                    catalog.AddFileVersion(new FileVersion
                                    {
                                        FileId = f.Id,
                                        SnapshotId = snapshot.Id,
                                        ChangeType = ChangeType.Deleted
                                    });
                                    purgedFilesCount++;
                                }
                            }
                        }
                    }

                    catalog.UpdateSnapshotStatus(snapshot.Id, SnapshotStatus.Complete);
                    AppendLog($"Backup cycle finished. Copied {backedUpCount:N0} files. {upToDateCount:N0} were up-to-date. {purgedFilesCount:N0} deletions recorded.", Brushes.LightGreen);

                    // --- History Retention Window Pruning Step ---
                    try
                    {
                        AppendLog($"Evaluating history retention policy rules ({GlobalExclusions.RetentionDays} days maximum limit)...", Brushes.DeepSkyBlue);
                        var snapshots = catalog.GetCompletedSnapshots();
                        int prunedFoldersCount = 0;
                        foreach (var snap in snapshots)
                        {
                            if ((DateTime.Today - snap.SnapshotDate).TotalDays > GlobalExclusions.RetentionDays)
                            {
                                AppendLog($"Pruning expired historical data snapshot: {snap.SnapshotDate:yyyy-MM-dd}", Brushes.Orange);

                                string datePattern = $"Snapshot_{snap.SnapshotDate:yyyy-MM-dd}*";
                                var dirs = Directory.GetDirectories(mountedDrive, datePattern);
                                foreach (var dir in dirs)
                                {
                                    try { Directory.Delete(dir, true); } catch { }
                                }

                                catalog.DeleteSnapshot(snap.Id);
                                prunedFoldersCount++;
                            }
                        }
                        if (prunedFoldersCount > 0)
                        {
                            AppendLog($"Retention cycle complete. Discarded {prunedFoldersCount} expired snapshots.", Brushes.LightGreen);
                        }
                    }
                    catch (Exception rentEx)
                    {
                        AppendLog($"Warning: History retention encountered issues ({rentEx.Message})", Brushes.Orange);
                    }
                } // Catalog is disposed here explicitly
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"Engine failure: {ex.Message}", Brushes.Red);
            }
            finally
            {
                WriteBackupReport(destRoot, filesToProcess != null ? filesToProcess.Count : 0, backedUpCount, upToDateCount, lockedCount, purgedFilesCount, 0, totalBytesMirrored);

                if (newlyMounted)
                {
                    try
                    {
                        AppendLog("Issuing automated disk auto-dismount...", Brushes.DeepSkyBlue);
                        Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Visible);
                        DismountVhdx(vhdxPath);
                        AppendLog("VHDX safely detached.", Brushes.LightGreen);
                    }
                    catch (Exception dex)
                    {
                        AppendLog($"Auto-dismount alert: {dex.Message}", Brushes.Orange);
                    }
                    finally
                    {
                        Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Collapsed);
                    }
                }
                SetThreadExecutionState(ES_CONTINUOUS);
            }
        }

        private void WriteBackupReport(string destRoot, long totalScannedCount, long backedUpCount, long upToDateCount, long lockedCount, long purgedFilesCount, long purgedDirsCount, long totalBytesMirrored)
        {
            try
            {
                string historyDir = Path.Combine(destRoot, "BackupHistoryLogs");
                if (!Directory.Exists(historyDir)) Directory.CreateDirectory(historyDir);

                string logFileName = $"BackupReport_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
                string fullLogPath = Path.Combine(historyDir, logFileName);

                using (StreamWriter sw = new StreamWriter(fullLogPath, false, System.Text.Encoding.UTF8))
                {
                    sw.WriteLine("==========================================================================");
                    sw.WriteLine($"PERSONAL BACKUP ENGINE HISTORICAL EXECUTION REPORT (SQLITE SNAPSHOT)");
                    sw.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sw.WriteLine("==========================================================================");
                    sw.WriteLine($"Total Files Scanned:               {totalScannedCount:N0}");
                    sw.WriteLine($"Successfully Copied (New/Modified) : {backedUpCount:N0}");
                    sw.WriteLine($"Up-to-Date (Skipped Unchanged)     : {upToDateCount:N0}");
                    sw.WriteLine($"Locked / Bypassed System Files      : {lockedCount:N0}");
                    sw.WriteLine($"Files Marked as Deleted            : {purgedFilesCount:N0}");
                    sw.WriteLine($"Total Sizing Streamed This Session : {FormatBytes(totalBytesMirrored)}");
                    sw.WriteLine("==========================================================================");

                    if (_errorDetailsReport.Count > 0)
                    {
                        sw.WriteLine();
                        sw.WriteLine("BYPASSED FILES REPORT SUMMARY DETAILS (ACCESS-LOCKED / PROTECTED):");
                        sw.WriteLine("--------------------------------------------------------------------------");
                        foreach (var errItem in _errorDetailsReport)
                        {
                            sw.WriteLine(errItem);
                        }
                    }
                    else
                    {
                        sw.WriteLine();
                        sw.WriteLine("Status: High-integrity run. 100% of scanned files processed successfully.");
                    }
                }
                AppendLog($"Historical summary session report saved: BackupHistoryLogs\\{logFileName}", Brushes.LightSeaGreen);
            }
            catch (Exception historyEx)
            {
                AppendLog($"Warning: Could not compile historical log file ({historyEx.Message})", Brushes.Orange);
            }

            Dispatcher.Invoke(() =>
            {
                TxtProgressDetails.Text = "Backup Operation Finished";
                _currentFilePath = null;
                TxtProgressDetails.Foreground = DefaultBrush;
                LblBackedUp.Text = backedUpCount.ToString("N0");
                LblUpToDate.Text = upToDateCount.ToString("N0");
                LblLocked.Text = lockedCount.ToString("N0");
                LblSavings.Text = FormatBytes(totalBytesMirrored);
            });
        }

        private void EnsureVhdxExists(string vhdxPath)
        {
            string? dir = Path.GetDirectoryName(vhdxPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string driveLetterStr = "Z";
            Dispatcher.Invoke(() =>
            {
                if (CboDriveLetter.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                {
                    driveLetterStr = item.Content.ToString() ?? "Z";
                }
            });

            string diskpartSetup = $@"create vdisk file=""{vhdxPath}"" maximum=102400 type=expandable
select vdisk file=""{vhdxPath}""
attach vdisk
create partition primary
assign letter={driveLetterStr}
exit";

            try
            {
                RunDiskpartScript(diskpartSetup);
                try
                {
                    RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem ReFS -DevDrive -NewFileSystemLabel 'BackupReFS' -Confirm:$false");
                    try { RunSystemCommand("fsutil.exe", $"devdrv trust /vol:{driveLetterStr}:"); } catch { }
                }
                catch
                {
                    try
                    {
                        RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem ReFS -NewFileSystemLabel 'BackupReFS' -Confirm:$false");
                    }
                    catch
                    {
                        RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem NTFS -NewFileSystemLabel 'BackupReFS' -Confirm:$false");
                    }
                }
            }
            finally
            {
                string detachScript = $@"select vdisk file=""{vhdxPath}""
detach vdisk
exit";
                try { RunDiskpartScript(detachScript); } catch { }
            }
        }

        private static void RunDiskpartScript(string scriptContent)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"vhdx_op_{Guid.NewGuid()}.txt");
            File.WriteAllText(scriptPath, scriptContent);

            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new Exception("Failed to invoke diskpart utility.");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                try { File.Delete(scriptPath); } catch { }
                if (process.ExitCode != 0) throw new Exception($"Diskpart allocation failed ({process.ExitCode}). Log: {output} Error: {error}");
            }
        }

        private static void RunSystemCommand(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new Exception($"Failed to invoke system utility: {fileName}");
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    throw new Exception($"Utility {fileName} failed with exit code {process.ExitCode}. Error: {error}");
                }
            }
        }

        private string CheckVhdxMountStatus(string vhdxPath)
        {
            try
            {
                string script = $@"
$path = '{vhdxPath.Replace("'", "''")}';
if (Test-Path $path) {{
    $di = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue 2>$null;
    if ($di -and $di.Number -ne $null) {{
        $part = Get-Partition -DiskNumber $di.Number -ErrorAction SilentlyContinue 2>$null | Where-Object {{ $_.DriveLetter }};
        if ($part) {{
            Write-Output $part.DriveLetter;
            return;
        }}
        Write-Output 'Mounted';
        return;
    }}
}}
Write-Output 'NotMounted';";

                string output = RunPowerShell(script).Trim();
                if (output == "Mounted" || output == "NotMounted") return output;
                if (!string.IsNullOrEmpty(output) && char.IsLetter(output[0])) return output[0] + ":";
                return "NotMounted";
            }
            catch { return "NotMounted"; }
        }

        private string MountVhdxAndGetLetter(string vhdxPath)
        {
            string driveLetterStr = "Z";
            Dispatcher.Invoke(() =>
            {
                if (CboDriveLetter.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                {
                    driveLetterStr = item.Content.ToString() ?? "Z";
                }
            });

            string script = $@"
$path = '{vhdxPath.Replace("'", "''")}';
$null = Mount-DiskImage -ImagePath $path -StorageType VHDX -ErrorAction SilentlyContinue;
for ($i = 0; $i -lt 20; $i++) {{
    $di = Get-DiskImage -ImagePath $path;
    if ($di.Number -ne $null) {{
        $disk = Get-Disk -Number $di.Number;
        $part = Get-Partition -DiskNumber $di.Number | Where-Object {{ $_.DriveLetter }}
        if ($part) {{
            if ($part.DriveLetter -ne '{driveLetterStr}') {{
                Set-Partition -DiskNumber $di.Number -PartitionNumber $part.PartitionNumber -NewDriveLetter '{driveLetterStr}' -ErrorAction SilentlyContinue;
            }}
            Write-Output '{driveLetterStr}';
            return;
        }}
    }}
    Start-Sleep -Milliseconds 500;
}}";

            string output = RunPowerShell(script).Trim();
            if (!string.IsNullOrEmpty(output) && char.IsLetter(output[0])) return output[0] + ":";
            throw new Exception("VHDX container mounted successfully, but target letter discovery timed out.");
        }

        private static void DismountVhdx(string vhdxPath)
        {
            RunPowerShell($"Dismount-DiskImage -ImagePath \"{vhdxPath}\"");
        }

        private static string RunPowerShell(string command)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new Exception("Failed to initialize system powershell environment.");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception($"Storage automation cmdlet failed. Error: {error}");
                return output;
            }
        }

        private void PurgeDeletedFilesAndDirs(string destDir, string sourceDir, System.Threading.CancellationToken cancellationToken, ref long purgedFiles, ref long purgedDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string[] destFiles = Directory.GetFiles(destDir);
                foreach (string df in destFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string fileName = Path.GetFileName(df);
                    string sf = Path.Combine(sourceDir, fileName);
                    if (!File.Exists(sf))
                    {
                        try { File.Delete(df); purgedFiles++; } catch (Exception ex) { _errorDetailsReport.Add($"-> Purge Failed (File): {df} | Reason: {ex.Message}"); }
                    }
                }
            }
            catch { }
            try
            {
                string[] destSubDirs = Directory.GetDirectories(destDir);
                foreach (string dd in destSubDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string dirName = Path.GetFileName(dd);
                    string sd = Path.Combine(sourceDir, dirName);
                    if (!Directory.Exists(sd))
                    {
                        try { Directory.Delete(dd, true); purgedDirs++; } catch (Exception ex) { _errorDetailsReport.Add($"-> Purge Failed (Dir): {dd} | Reason: {ex.Message}"); }
                    }
                    else { PurgeDeletedFilesAndDirs(dd, sd, cancellationToken, ref purgedFiles, ref purgedDirs); }
                }
            }
            catch { }
        }

        private void DiscoverFilesRecursively(string currentDir, List<string> files, ref long scannedCount, System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var dirInfo = new DirectoryInfo(currentDir);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            }
            catch { return; }
            if (IsPathExcluded(currentDir)) return;
            try
            {
                string[] dirFiles = Directory.GetFiles(currentDir);
                foreach (var f in dirFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedCount++;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        if (fi.Attributes.HasFlag(FileAttributes.Hidden) || fi.Attributes.HasFlag(FileAttributes.System)) continue;
                    }
                    catch { continue; }
                    if (!IsPathExcluded(f)) files.Add(f);
                }
            }
            catch (Exception dirEx)
            {
                scannedCount++;
                _errorDetailsReport.Add($"-> Scope Folder Lock: {currentDir} | Reason: Access Denied ({dirEx.Message})");
                return;
            }
            try
            {
                string[] subDirs = Directory.GetDirectories(currentDir);
                foreach (var d in subDirs) { DiscoverFilesRecursively(d, files, ref scannedCount, cancellationToken); }
            }
            catch (Exception subDirEx) { _errorDetailsReport.Add($"-> Subfolder Tree Lock: {currentDir} | Reason: Bypassed Subdirectories traversal ({subDirEx.Message})"); }
        }

        private bool IsPathExcluded(string fullPath)
        {
            string lower = fullPath.ToLower();
            if (GlobalExclusions.IsCustomExcluded(lower)) return true;
            if (GlobalExclusions.IsDefaultExcluded(fullPath)) return true;
            return false;
        }

        private string MapToBackupPath(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == '\\')
            {
                char driveLetter = char.ToUpper(fullPath[0]);
                return Path.Combine(driveLetter.ToString(), fullPath.Substring(3));
            }
            return fullPath;
        }

        private string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            double dblBytes = bytes;
            int i = 0;
            while (dblBytes >= 1024 && i < suffix.Length - 1) { i++; dblBytes /= 1024; }
            return $"{dblBytes:F2} {suffix[i]}";
        }

        private void TxtProgressDetails_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentFilePath))
            {
                OpenContainingFolder(_currentFilePath);
            }
        }

        private void OpenContainingFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open folder: {ex.Message}", Brushes.Red);
            }
        }

        private void AppendLog(string message, Brush color)
        {
            Dispatcher.Invoke(() =>
            {
                Run run = new Run($"[{DateTime.Now:HH:mm:ss}] {message}\n") { Foreground = color };
                Paragraph para = new Paragraph(run) { Margin = new Thickness(0), LineHeight = 16 };
                RtbLog.Document.Blocks.Add(para);
                if (RtbLog.Document.Blocks.Count > 1200) RtbLog.Document.Blocks.Remove(RtbLog.Document.Blocks.FirstBlock);
                LogScrollViewer.ScrollToEnd();
            });
        }
    }
}
