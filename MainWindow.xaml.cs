using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace xBackup
{
    public partial class MainWindow : Window
    {
        // Windows Power Management API P/Invoke definitions to keep PC awake
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

        private readonly string[] _sourcePaths = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"C:\ProgramData" };
        private string _destinationRoot = @"F:\Backup\Home-PC";
        private bool _isProcessing = false;
        private bool _isVhdxMountedManual = false;
        private readonly bool _isSilentMode = false;
        private H.NotifyIcon.TaskbarIcon? _notifyIcon;
        private readonly List<string> _errorDetailsReport = new List<string>();
        private System.Threading.CancellationTokenSource? _cts;

        public class RelayCommand : System.Windows.Input.ICommand
        {
            private readonly Action _execute;
            public RelayCommand(Action execute) => _execute = execute;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _execute();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        public System.Windows.Input.ICommand ShowWindowCommand { get; }

        public MainWindow(bool silentMode)
        {
            _isSilentMode = silentMode;
            ShowWindowCommand = new RelayCommand(RestoreFromTray);
            InitializeComponent();
            UserProfileTextBlock.Text = "• " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            DataContext = this;

            // Load saved coordinates from configuration cache if existing
            LoadWindowPlacementSettings();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
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
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Save coordinates on exit
            SaveWindowPlacementSettings();

            // If the user clicks close button while background engine processing, minimize to system tray instead of aborting
            if (_isProcessing)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon?.ShowNotification("Backup Active", "The Backup execution is still running in the background system tray.");
            }
            else
            {
                if (_isVhdxMountedManual)
                {
                    try
                    {
                        string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");
                        DismountVhdx(vhdxPath);
                    }
                    catch { }
                }
                _notifyIcon?.Dispose();
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
                MessageBox.Show("Automated daily backup task removed completely from Windows Task Scheduler.", "Task Removed", MessageBoxButton.OK, MessageBoxImage.Information);
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

                if (success)
                {
                    MessageBox.Show("Successfully created a high-integrity daily backup task triggered at 12:00 AM Midnight. The system settings include WakeToRun to wake your device from sleep mode.", "Task Registered", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Failed to create the task configuration. Make sure you are running as administrator and executing the actual standalone compiled .exe file.", "Scheduling Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public async void ExecuteSilentScheduledBackup()
        {
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

            SetUiState(processing: true);
            RtbLog.Document.Blocks.Clear();
            PrgBar.Value = 0;

            _cts = new System.Threading.CancellationTokenSource();

            try
            {
                await Task.Run(() => RunBackupEngine(_destinationRoot, _cts.Token));
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

                    AppendLog("Mounting VHDX backup drive...", Brushes.DeepSkyBlue);
                    string driveLetter = await Task.Run(() => MountVhdxAndGetLetter(vhdxPath));
                    AppendLog($"VHDX successfully mounted at drive {driveLetter}", Brushes.LightGreen);

                    _isVhdxMountedManual = true;
                    BtnToggleMount.Content = "Eject Drive";
                    BtnToggleMount.Background = new SolidColorBrush(Color.FromRgb(180, 50, 50));

                    Process.Start("explorer.exe", driveLetter);
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
                    if (!_isVhdxMountedManual)
                    {
                        BtnBackup.IsEnabled = true;
                    }
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
            BtnBackup.IsEnabled = !processing && !_isVhdxMountedManual;
            BtnToggleMount.IsEnabled = !processing;
            BtnStop.IsEnabled = processing;
            TxtStatus.Text = processing ? "Engine Status: Active" : "Engine Status: Ready";
            TxtStatus.Foreground = processing ? new SolidColorBrush(Color.FromRgb(220, 202, 170)) : new SolidColorBrush(Color.FromRgb(78, 201, 176));
        }

        private void RunBackupEngine(string destRoot, System.Threading.CancellationToken cancellationToken)
        {
            string vhdxPath = Path.Combine(destRoot, "BackupDev.vhdx");
            string mountedDrive = string.Empty;
            bool newlyMounted = false;

            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                AppendLog("Initializing Automated Backup Lifecycle...", Brushes.DeepSkyBlue);

                if (!Directory.Exists(destRoot))
                {
                    Directory.CreateDirectory(destRoot);
                }

                if (!File.Exists(vhdxPath))
                {
                    Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Visible);
                    AppendLog("VHDX storage container absent. Building 100 GB dynamic image with ReFS Dev Drive layout...", Brushes.Orange);
                    EnsureVhdxExists(vhdxPath);
                    AppendLog("VHDX container created and initialized cleanly.", Brushes.LightGreen);
                }

                AppendLog("Performing strict on-demand VHDX mounting...", Brushes.DeepSkyBlue);
                Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Visible);
                mountedDrive = MountVhdxAndGetLetter(vhdxPath);
                newlyMounted = true;
                AppendLog($"VHDX dynamically attached onto drive {mountedDrive}", Brushes.LightGreen);
                Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Collapsed);

                _errorDetailsReport.Clear();

                AppendLog("Commencing deep filesystem discovery phase...", Brushes.DeepSkyBlue);
                List<string> filesToProcess = new List<string>();
                long totalScannedCount = 0;

                foreach (var sourceRoot in _sourcePaths)
                {
                    if (!Directory.Exists(sourceRoot))
                    {
                        AppendLog($"Source directory absent, skipping scope: {sourceRoot}", Brushes.Yellow);
                        continue;
                    }

                    AppendLog($"Scanning scope: {sourceRoot}...", Brushes.Gray);
                    DiscoverFilesRecursively(sourceRoot, filesToProcess, ref totalScannedCount, cancellationToken);
                }

                AppendLog($"Discovery finished. Total files matched on drive: {filesToProcess.Count} (Filtered out {totalScannedCount - filesToProcess.Count} junk/temp files).", Brushes.LightGreen);

                Dispatcher.Invoke(() =>
                {
                    LblScanned.Text = filesToProcess.Count.ToString("N0");
                    PrgBar.Maximum = filesToProcess.Count;
                });

                long backedUpCount = 0;
                long upToDateCount = 0;
                long lockedCount = 0;
                long totalBytesMirrored = 0;

                int currentIndex = 0;

                foreach (var file in filesToProcess)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    currentIndex++;

                    // Always show the current file in the status text so user knows exactly what the engine is working on
                    Dispatcher.Invoke(() =>
                    {
                        TxtProgressDetails.Text = $"[{currentIndex:N0}/{filesToProcess.Count:N0}] Processing: {file}";

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

                    string relativeStructurePath = MapToBackupPath(file);
                    string destFilePath = Path.Combine(mountedDrive, relativeStructurePath);

                    bool needsCopy = true;
                    try
                    {
                        if (File.Exists(destFilePath))
                        {
                            var destFi = new FileInfo(destFilePath);
                            if (destFi.Length == sourceFi.Length && destFi.LastWriteTimeUtc == sourceFi.LastWriteTimeUtc)
                            {
                                needsCopy = false;
                            }
                        }
                    }
                    catch
                    {
                        needsCopy = true;
                    }

                    if (!needsCopy)
                    {
                        upToDateCount++;
                        continue;
                    }

                    string tempFilePath = destFilePath + ".tmp";
                    try
                    {
                        AppendLog($"[Copying] {sourceFi.Name} ({FormatBytes(sourceFi.Length)})...", Brushes.Gray);

                        string? parentDir = Path.GetDirectoryName(destFilePath);
                        if (parentDir != null && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        // Copy to a temporary file first to protect against partial copies/power outages
                        File.Copy(file, tempFilePath, overwrite: true);

                        // Verify that the temporary file was written completely and correctly
                        var tempFi = new FileInfo(tempFilePath);
                        if (!tempFi.Exists || tempFi.Length != sourceFi.Length)
                        {
                            throw new IOException("Verification failed: Copied file size does not match source file size.");
                        }

                        // Atomically replace/move to the final destination path
                        File.Move(tempFilePath, destFilePath, overwrite: true);
                        File.SetLastWriteTimeUtc(destFilePath, sourceFi.LastWriteTimeUtc);

                        totalBytesMirrored += sourceFi.Length;
                        backedUpCount++;

                        if (backedUpCount <= 100 || sourceFi.Length > 50 * 1024 * 1024)
                        {
                            AppendLog($"[Mirror] Copied & Verified: {sourceFi.Name} ({FormatBytes(sourceFi.Length)})", Brushes.LightGreen);
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            if (File.Exists(tempFilePath))
                            {
                                File.Delete(tempFilePath);
                            }
                        }
                        catch { /* Ignore cleanup errors to retain original exception context */ }

                        lockedCount++;
                        _errorDetailsReport.Add($"-> {file} | Reason: {ex.Message}");
                        if (lockedCount <= 50)
                        {
                            AppendLog($"Bypassed locked file: {sourceFi.Name} ({ex.Message})", Brushes.Yellow);
                        }
                    }
                }

                // --- Purge Phase (File Deletions) ---
                long purgedFilesCount = 0;
                long purgedDirsCount = 0;
                try
                {
                    AppendLog("Commencing deletion/purge phase for removed files...", Brushes.DeepSkyBlue);
                    foreach (var sourceRoot in _sourcePaths)
                    {
                        string driveFolder = MapToBackupPath(sourceRoot);
                        string destRootFolder = Path.Combine(mountedDrive, driveFolder);

                        if (Directory.Exists(destRootFolder))
                        {
                            PurgeDeletedFilesAndDirs(destRootFolder, sourceRoot, cancellationToken, ref purgedFilesCount, ref purgedDirsCount);
                        }
                    }
                    if (purgedFilesCount > 0 || purgedDirsCount > 0)
                    {
                        AppendLog($"Purge complete. Removed {purgedFilesCount:N0} files and {purgedDirsCount:N0} directories from backup that no longer exist in source.", Brushes.LightGreen);
                    }
                    else
                    {
                        AppendLog("Purge phase complete. Destination is fully synchronized (no orphan files found).", Brushes.Gray);
                    }
                }
                catch (Exception purgeEx)
                {
                    AppendLog($"Warning: Purge phase encountered an error ({purgeEx.Message})", Brushes.Orange);
                }

                try
                {
                    string historyDir = Path.Combine(destRoot, "BackupHistoryLogs");
                    if (!Directory.Exists(historyDir)) Directory.CreateDirectory(historyDir);

                    string logFileName = $"BackupReport_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
                    string fullLogPath = Path.Combine(historyDir, logFileName);

                    using (StreamWriter sw = new StreamWriter(fullLogPath, false, System.Text.Encoding.UTF8))
                    {
                        sw.WriteLine("==========================================================================");
                        sw.WriteLine($"PERSONAL BACKUP ENGINE HISTORICAL EXECUTION REPORT (VHDX MIRROR)");
                        sw.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        sw.WriteLine("==========================================================================");
                        sw.WriteLine($"Total Files Scanned on C Drive Scope: {filesToProcess.Count:N0}");
                        sw.WriteLine($"Successfully Mirrored / Copied     : {backedUpCount:N0}");
                        sw.WriteLine($"Up-to-Date (Skipped Unchanged)     : {upToDateCount:N0}");
                        sw.WriteLine($"Locked / Bypassed System Files      : {lockedCount:N0}");
                        sw.WriteLine($"Files Purged / Cleaned Up          : {purgedFilesCount:N0}");
                        sw.WriteLine($"Directories Purged / Cleaned Up    : {purgedDirsCount:N0}");
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
                            sw.WriteLine("Status: High-integrity run. 100% of scanned files backed up without locks.");
                        }
                    }
                    AppendLog($"Historical summary session report saved: BackupHistoryLogs\\{logFileName}", Brushes.LightSeaGreen);
                }
                catch (Exception historyEx)
                {
                    AppendLog($"Warning: Could not compile historical log file ({historyEx.Message})", Brushes.Orange);
                }

                AppendLog("----------------------------------------------------------------", Brushes.Gray);
                AppendLog($"Backup execution completed cleanly.", Brushes.DeepSkyBlue);
                AppendLog($"Successfully Mirrored: {backedUpCount:N0} files.", Brushes.LightGreen);
                AppendLog($"Unchanged files kept: {upToDateCount:N0} files.", Brushes.Gray);
                AppendLog($"Locked files bypassed: {lockedCount:N0} files.", Brushes.Yellow);
                if (purgedFilesCount > 0 || purgedDirsCount > 0)
                {
                    AppendLog($"Purged/Deleted from Backup: {purgedFilesCount:N0} files and {purgedDirsCount:N0} dirs.", Brushes.LightGreen);
                }

                Dispatcher.Invoke(() =>
                {
                    TxtProgressDetails.Text = "Backup Complete!";
                    LblBackedUp.Text = backedUpCount.ToString("N0");
                    LblUpToDate.Text = upToDateCount.ToString("N0");
                    LblLocked.Text = lockedCount.ToString("N0");
                    LblSavings.Text = FormatBytes(totalBytesMirrored);
                });
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
                if (newlyMounted)
                {
                    try
                    {
                        AppendLog("Issuing automated full closed-lifecycle disk auto-dismount...", Brushes.DeepSkyBlue);
                        Dispatcher.Invoke(() => PrgWaiting.Visibility = Visibility.Visible);
                        DismountVhdx(vhdxPath);
                        AppendLog("VHDX safely detached and isolated.", Brushes.LightGreen);
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

            // Create and partition VHDX using DiskPart.
            // We separate 'format' from DiskPart to handle the /devdrv flag compatibility and provide fallbacks.
            string diskpartSetup = $@"create vdisk file=""{vhdxPath}"" maximum=102400 type=expandable
select vdisk file=""{vhdxPath}""
attach vdisk
create partition primary
assign letter={driveLetterStr}
exit";

            try
            {
                RunDiskpartScript(diskpartSetup);

                // Attempt to format as Dev Drive (ReFS) first.
                // DiskPart's internal 'format' command often lacks support for the 'devdrv' flag or fails on Home editions.
                try
                {
                    RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem ReFS -DevDrive -NewFileSystemLabel 'BackupReFS' -Confirm:$false");
                    // Apply Dev Drive trust policy for performance optimization
                    try { RunSystemCommand("fsutil.exe", $"devdrv trust /vol:{driveLetterStr}:"); } catch { }
                }
                catch
                {
                    // Fallback 1: Standard ReFS (Non-Dev Drive) - Works on Pro/Enterprise editions
                    try
                    {
                        RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem ReFS -NewFileSystemLabel 'BackupReFS' -Confirm:$false");
                    }
                    catch
                    {
                        // Fallback 2: Standard NTFS - Works on all Windows versions including Home
                        RunPowerShell($"Format-Volume -DriveLetter {driveLetterStr} -FileSystem NTFS -NewFileSystemLabel 'BackupReFS' -Confirm:$false");
                    }
                }
            }
            finally
            {
                // Cleanly detach disk so it stays closed lifecycle ready
                string detachScript = $@"select vdisk file=""{vhdxPath}""
detach vdisk
exit";
                try
                {
                    RunDiskpartScript(detachScript);
                }
                catch { /* Ignore errors during detach in setup phase */ }
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

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Diskpart allocation failed ({process.ExitCode}). Log: {output} Error: {error}");
                }
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

        private static string MountVhdxAndGetLetter(string vhdxPath)
        {
            // We run the retry logic inside PowerShell to avoid the overhead of starting multiple processes,
            // and use a more robust discovery path through Get-Disk and Get-Partition.
            // We MUST discard the output of Mount-DiskImage ($null = ...) otherwise it pollutes the return string.
            string script = $@"
$path = '{vhdxPath.Replace("'", "''")}';
$null = Mount-DiskImage -ImagePath $path -StorageType VHDX -ErrorAction SilentlyContinue;
for ($i = 0; $i -lt 20; $i++) {{
    $di = Get-DiskImage -ImagePath $path;
    if ($di.Number -ne $null) {{
        $letter = (Get-Disk -Number $di.Number | Get-Partition | Where-Object DriveLetter).DriveLetter;
        if ($letter) {{
            Write-Output $letter;
            return;
        }}
    }}
    Start-Sleep -Milliseconds 500;
}}";

            string output = RunPowerShell(script).Trim();

            if (!string.IsNullOrEmpty(output) && char.IsLetter(output[0]))
            {
                // Ensure we only take the letter, even if there's trailing whitespace or objects
                return output[0] + ":";
            }

            throw new Exception("VHDX container mounted successfully, but target letter discovery timed out. Please ensure the volume is initialized.");
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

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Storage automation cmdlet failed. Error: {error}");
                }
                return output;
            }
        }

        private void PurgeDeletedFilesAndDirs(string destDir, string sourceDir, System.Threading.CancellationToken cancellationToken, ref long purgedFiles, ref long purgedDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Purge Files
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
                        try
                        {
                            File.Delete(df);
                            purgedFiles++;
                        }
                        catch (Exception ex)
                        {
                            _errorDetailsReport.Add($"-> Purge Failed (File): {df} | Reason: {ex.Message}");
                        }
                    }
                }
            }
            catch { }

            // 2. Recursively Purge Subdirectories
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
                        try
                        {
                            Directory.Delete(dd, true);
                            purgedDirs++;
                        }
                        catch (Exception ex)
                        {
                            _errorDetailsReport.Add($"-> Purge Failed (Dir): {dd} | Reason: {ex.Message}");
                        }
                    }
                    else
                    {
                        PurgeDeletedFilesAndDirs(dd, sd, cancellationToken, ref purgedFiles, ref purgedDirs);
                    }
                }
            }
            catch { }
        }

        private void DiscoverFilesRecursively(string currentDir, List<string> files, ref long scannedCount, System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPathExcluded(currentDir)) return;

            try
            {
                string[] dirFiles = Directory.GetFiles(currentDir);
                foreach (var f in dirFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedCount++;
                    if (!IsPathExcluded(f))
                    {
                        files.Add(f);
                    }
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
                foreach (var d in subDirs)
                {
                    DiscoverFilesRecursively(d, files, ref scannedCount, cancellationToken);
                }
            }
            catch (Exception subDirEx)
            {
                _errorDetailsReport.Add($"-> Subfolder Tree Lock: {currentDir} | Reason: Bypassed Subdirectories traversal ({subDirEx.Message})");
            }
        }

        private bool IsPathExcluded(string fullPath)
        {
            string lower = fullPath.ToLower();

            // Explicitly exclude Phone Link, Microsoft Mobile Features, and phone sync files that trigger wireless/bluetooth/cloud sync on demand
            if (lower.Contains(@"\appdata\local\microsoft\phonelink") ||
                lower.Contains(@"\microsoft.yourphone") ||
                lower.Contains(@"\mobiledeviceconnect") ||
                lower.Contains(@"\.android") ||
                lower.Contains(@"\phone-link"))
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

        private string MapToBackupPath(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == '\\')
            {
                char driveLetter = fullPath[0];
                return $"Drive_{driveLetter}" + fullPath.Substring(2);
            }
            return fullPath;
        }

        private string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            double dblBytes = bytes;
            int i = 0;
            while (dblBytes >= 1024 && i < suffix.Length - 1)
            {
                i++;
                dblBytes /= 1024;
            }
            return $"{dblBytes:F2} {suffix[i]}";
        }

        private void AppendLog(string message, Brush color)
        {
            Dispatcher.Invoke(() =>
            {
                Run run = new Run($"[{DateTime.Now:HH:mm:ss}] {message}\n") { Foreground = color };
                Paragraph para = new Paragraph(run) { Margin = new Thickness(0), LineHeight = 16 };
                RtbLog.Document.Blocks.Add(para);

                if (RtbLog.Document.Blocks.Count > 1200)
                {
                    RtbLog.Document.Blocks.Remove(RtbLog.Document.Blocks.FirstBlock);
                }

                LogScrollViewer.ScrollToEnd();
            });
        }
    }
}
