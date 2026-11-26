using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using xBackup.Models;

namespace xBackup
{
    public partial class MainWindow : Window
    {
        private string? _currentFilePath;
        private string? CurrentFilePath
        {
            get => _currentFilePath;
            set
            {
                _currentFilePath = value;
                TxtProgressDetails.ToolTip = value;
            }
        }

        private static readonly Brush AccentBrushColor = (Brush)new BrushConverter().ConvertFromString("#F38020")!;
        private static readonly Brush AccentBrightBrush = (Brush)new BrushConverter().ConvertFromString("#FAAD3F")!;
        private static readonly Brush SuccessBrushColor = (Brush)new BrushConverter().ConvertFromString("#22C55E")!;
        private static readonly Brush DangerBrushColor = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
        private static readonly Brush MutedBrushColor = (Brush)new BrushConverter().ConvertFromString("#A1A1AA")!;
        private static readonly Brush DefaultTextBrush = (Brush)new BrushConverter().ConvertFromString("#F4F4F5")!;

        private string _destinationRoot = string.Empty;
        private bool _enableWeeklyIntegrityInSilent = false;
        private XServerSettings _xServerSettings = new();

        private bool _isProcessing = false;
        private bool _isVerifying = false;
        private bool _isVhdxMountedManual = false;
        private bool _isXServerVhdxMountedManual = false;
        private bool _isClosingInProgress = false;
        private bool _isCleanupDone = false;
        private bool _isForceExit = false;
        private bool _isInitializing = true;
        private readonly bool _isSilentMode = false;

        private H.NotifyIcon.TaskbarIcon? _notifyIcon;
        private readonly List<string> _errorDetailsReport = [];
        private CancellationTokenSource? _cts;

        public class BackupCheckpoint
        {
            public DateTime Timestamp { get; set; }
            public List<string> FilesToProcess { get; set; } = [];
            public int CurrentIndex { get; set; }
            public string DestinationRoot { get; set; } = string.Empty;
            public long BackedUpCount { get; set; }
            public long UpToDateCount { get; set; }
            public long LockedCount { get; set; }
            public long TotalBytesMirrored { get; set; }
        }

        public class RelayCommand(Action execute) : ICommand
        {
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => execute();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        public class ScopeDisplayItem
        {
            public string DisplayText { get; set; } = string.Empty;
            public bool IsDrive { get; set; }
        }

        private class WindowPlacementData
        {
            public double Left { get; set; } = -1;
            public double Top { get; set; } = -1;
            public double Width { get; set; } = 1040;
            public double Height { get; set; } = 680;
            public bool IsMaximized { get; set; } = false;
            public string DestinationRoot { get; set; } = string.Empty;
            public int ContainerSizeGb { get; set; } = 200;
            public string MountDriveLetter { get; set; } = "X";
            public bool EnableWeeklyIntegrityInSilent { get; set; } = false;
        }

        public ICommand ShowWindowCommand { get; }

        public MainWindow(bool silentMode)
        {
            _isSilentMode = silentMode;
            ShowWindowCommand = new RelayCommand(RestoreFromTray);
            InitializeComponent();
            DataContext = this;

            Task.Run(VhdxManager.PreloadDiskpart);

            LoadWindowPlacementSettings();
            GlobalExclusions.Load();
            _xServerSettings = XServerSettings.Load();
            PopulateXServerFields();
            RefreshBackupScopesDisplay();
            UpdateDashboardSummary();

            TxtLogFilePath.Text = $"Log file: {AppLogger.CurrentLogFilePath}";

            _isInitializing = false;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DwmThemeHelper.ApplyDarkWindowChrome(this);
        }

        // =========================================================================
        // NAVIGATION
        // =========================================================================
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || DashboardView == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;

            DashboardView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            HomePcView.Visibility = tag == "HomePc" ? Visibility.Visible : Visibility.Collapsed;
            XServerView.Visibility = tag == "XServer" ? Visibility.Visible : Visibility.Collapsed;
            LogsView.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

            switch (tag)
            {
                case "Dashboard":
                    PageTitleText.Text = "Dashboard";
                    PageSubtitleText.Text = "Automated incremental ReFS backups for Home-PC and xServer";
                    break;
                case "HomePc":
                    PageTitleText.Text = "Home-PC Backup";
                    PageSubtitleText.Text = "Local drive scopes, ReFS VHDX container, restore, and integrity checks";
                    break;
                case "XServer":
                    PageTitleText.Text = "xServer Incremental Clone";
                    PageSubtitleText.Text = "Daily SSH delta mirror onto a dedicated ReFS VHDX container";
                    break;
                case "Logs":
                    PageTitleText.Text = "Activity & Logs";
                    PageSubtitleText.Text = "Real-time operations feed mirrored to persistent daily log files";
                    break;
                case "Settings":
                    PageTitleText.Text = "Schedule & Settings";
                    PageSubtitleText.Text = "Midnight wake task, dismount verification, and integrity policy";
                    break;
            }
        }

        // =========================================================================
        // CONFIG & INITIALIZATION
        // =========================================================================
        private static string GetConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "window_placement.json");
        }

        private static string GetCheckpointFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SmartBackupEngine");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "backup_checkpoint.json");
        }

        private void SaveCheckpoint(BackupCheckpoint checkpoint)
        {
            try { File.WriteAllText(GetCheckpointFilePath(), JsonSerializer.Serialize(checkpoint)); } catch { }
        }

        private BackupCheckpoint? LoadCheckpoint()
        {
            try
            {
                string path = GetCheckpointFilePath();
                if (File.Exists(path))
                {
                    var checkpoint = JsonSerializer.Deserialize<BackupCheckpoint>(File.ReadAllText(path));
                    if (checkpoint != null && (DateTime.Now - checkpoint.Timestamp).TotalHours < 48)
                        return checkpoint;
                }
            }
            catch { }
            return null;
        }

        private void DeleteCheckpoint()
        {
            try { string path = GetCheckpointFilePath(); if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void LoadWindowPlacementSettings()
        {
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<WindowPlacementData>(File.ReadAllText(path));
                    if (data != null)
                    {
                        if (data.IsMaximized) WindowState = WindowState.Maximized;

                        if (!string.IsNullOrWhiteSpace(data.DestinationRoot))
                        {
                            TxtDestRoot.Text = data.DestinationRoot;
                            _destinationRoot = data.DestinationRoot;
                        }

                        if (data.ContainerSizeGb > 0) TxtContainerSizeGb.Text = data.ContainerSizeGb.ToString();
                        _enableWeeklyIntegrityInSilent = data.EnableWeeklyIntegrityInSilent;
                        ChkWeeklyIntegrity.IsChecked = _enableWeeklyIntegrityInSilent;

                        SelectComboBoxLetter(CboDriveLetter, data.MountDriveLetter);

                        if (data.Left >= 0 && data.Top >= 0 &&
                            data.Left + 100 < SystemParameters.VirtualScreenWidth &&
                            data.Top + 100 < SystemParameters.VirtualScreenHeight)
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual;
                            Left = data.Left;
                            Top = data.Top;
                            Width = Math.Max(880, data.Width);
                            Height = Math.Max(580, data.Height);
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
                var data = new WindowPlacementData
                {
                    IsMaximized = WindowState == WindowState.Maximized,
                    Left = WindowState == WindowState.Maximized ? RestoreBounds.Left : Left,
                    Top = WindowState == WindowState.Maximized ? RestoreBounds.Top : Top,
                    Width = WindowState == WindowState.Maximized ? RestoreBounds.Width : Width,
                    Height = WindowState == WindowState.Maximized ? RestoreBounds.Height : Height,
                    DestinationRoot = TxtDestRoot.Text,
                    ContainerSizeGb = GetConfiguredContainerSizeGb(),
                    MountDriveLetter = GetSelectedDriveLetter(CboDriveLetter, "X"),
                    EnableWeeklyIntegrityInSilent = _enableWeeklyIntegrityInSilent
                };
                File.WriteAllText(GetConfigFilePath(), JsonSerializer.Serialize(data));
            }
            catch { }
        }

        private static void SelectComboBoxLetter(ComboBox cbo, string? letter)
        {
            if (string.IsNullOrWhiteSpace(letter)) return;
            foreach (ComboBoxItem item in cbo.Items)
            {
                if (string.Equals(item.Content?.ToString(), letter, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }

        private string GetSelectedDriveLetter(ComboBox cbo, string fallback)
        {
            string val = fallback;
            Dispatcher.Invoke(() =>
            {
                if (cbo.SelectedItem is ComboBoxItem item && !string.IsNullOrWhiteSpace(item.Content?.ToString()))
                {
                    val = item.Content.ToString()!.Trim();
                }
            });
            return val;
        }

        private void PopulateXServerFields()
        {
            TxtXServerHost.Text = _xServerSettings.Host;
            TxtXServerUser.Text = _xServerSettings.Username;
            TxtXServerPort.Text = _xServerSettings.SshPort.ToString();
            TxtXServerDestRoot.Text = _xServerSettings.DestinationRoot;
            TxtXServerContainerSizeGb.Text = _xServerSettings.ContainerSizeGb.ToString();
            ChkXServerEnabled.IsChecked = _xServerSettings.Enabled;
            ChkXServerMirror.IsChecked = _xServerSettings.MirrorDeletions;
            SelectComboBoxLetter(CboXServerDriveLetter, _xServerSettings.MountDriveLetter);
            TxtXServerIncludes.Text = string.Join(Environment.NewLine, _xServerSettings.IncludePaths);
            TxtXServerExcludes.Text = string.Join(Environment.NewLine, _xServerSettings.ExcludePatterns);
        }

        private void SyncXServerFieldsToSettings()
        {
            _xServerSettings.Host = TxtXServerHost.Text.Trim();
            _xServerSettings.Username = TxtXServerUser.Text.Trim();
            if (int.TryParse(TxtXServerPort.Text.Trim(), out int port) && port > 0 && port <= 65535)
                _xServerSettings.SshPort = port;
            _xServerSettings.DestinationRoot = TxtXServerDestRoot.Text.Trim();
            if (int.TryParse(TxtXServerContainerSizeGb.Text.Trim(), out int gb) && gb >= 10 && gb <= 64000)
                _xServerSettings.ContainerSizeGb = gb;
            _xServerSettings.MountDriveLetter = GetSelectedDriveLetter(CboXServerDriveLetter, "Y");
            _xServerSettings.Enabled = ChkXServerEnabled.IsChecked == true;
            _xServerSettings.MirrorDeletions = ChkXServerMirror.IsChecked == true;
            _xServerSettings.IncludePaths = TxtXServerIncludes.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            _xServerSettings.ExcludePatterns = TxtXServerExcludes.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            _xServerSettings.Save();
            UpdateDashboardSummary();
        }

        private void UpdateDashboardSummary()
        {
            Dispatcher.Invoke(() =>
            {
                TxtDashHomePath.Text = Path.Combine(TxtDestRoot.Text.Trim(), "BackupDev.vhdx");
                TxtDashXServerTitle.Text = $"{_xServerSettings.Username}@{_xServerSettings.Host}";
                if (_xServerSettings.LastBackupUtc.HasValue)
                {
                    var local = _xServerSettings.LastBackupUtc.Value.ToLocalTime();
                    TxtDashXServerStatus.Text = $"Last clone: {local:g} • {_xServerSettings.LastFilesUpdated:N0} updated, {_xServerSettings.LastFilesUnchanged:N0} unchanged";
                }
                else
                {
                    TxtDashXServerStatus.Text = $"Last clone: {_xServerSettings.LastStatus} • Target: {_xServerSettings.VhdxFullPath}";
                }
            });
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
                    try { label = new DriveInfo(drive).VolumeLabel; } catch { }
                    string cleanDrive = drive.TrimEnd('\\');
                    string displayName = string.IsNullOrEmpty(label) ? $"Local Disk ({cleanDrive})" : $"{label} ({cleanDrive})";
                    scopes.Add(new ScopeDisplayItem { DisplayText = displayName, IsDrive = true });
                }
            }
            ItemsScopes.ItemsSource = scopes;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _notifyIcon = (H.NotifyIcon.TaskbarIcon)FindResource("NotifyIcon");
                _notifyIcon.TrayMouseDoubleClick += (_, _) => RestoreFromTray();
                _notifyIcon.ForceCreate();
            }
            catch (Exception iconEx)
            {
                AppLogger.Error("TRAY", $"System tray init failed: {iconEx.Message}");
            }

            Task.Run(RefreshTaskSchedulerStatusAsync);
            Task.Run(RefreshAllMountStatuses);
        }

        private void RefreshAllMountStatuses()
        {
            string homeVhdx = Path.Combine(Dispatcher.Invoke(() => TxtDestRoot.Text.Trim()), "BackupDev.vhdx");
            string xServerVhdx = _xServerSettings.VhdxFullPath;

            if (File.Exists(homeVhdx))
            {
                int sizeGb = VhdxManager.GetVhdxVirtualSizeGb(homeVhdx);
                if (sizeGb > 0) Dispatcher.Invoke(() => TxtContainerSizeGb.Text = sizeGb.ToString());
            }
            if (File.Exists(xServerVhdx))
            {
                int sizeGb = VhdxManager.GetVhdxVirtualSizeGb(xServerVhdx);
                if (sizeGb > 0) Dispatcher.Invoke(() => TxtXServerContainerSizeGb.Text = sizeGb.ToString());
            }

            string homeStatus = VhdxManager.CheckVhdxMountStatus(homeVhdx);
            string xServerStatus = VhdxManager.CheckVhdxMountStatus(xServerVhdx);

            Dispatcher.Invoke(() =>
            {
                UpdateHomeMountUi(homeStatus);
                UpdateXServerMountUi(xServerStatus);
            });
        }

        private void UpdateHomeMountUi(string status)
        {
            bool mounted = status != "NotMounted";
            _isVhdxMountedManual = mounted;
            string label = !mounted ? "Unmounted" : (status == "Mounted" ? "Mounted" : $"Mounted ({status})");
            TxtHomeMountBadge.Text = label;
            TxtHomeMountBadge.Foreground = mounted ? SuccessBrushColor : MutedBrushColor;
            DrawerHomeMountText.Text = $"Home-PC: {label}";
            DrawerHomeMountText.Foreground = mounted ? SuccessBrushColor : MutedBrushColor;
            BtnToggleMount.Content = mounted ? "Eject Drive" : "Mount Drive";
            BtnHomeMountAlt.Content = mounted ? "Eject Home-PC Drive" : "Mount Home-PC Drive";
        }

        private void UpdateXServerMountUi(string status)
        {
            bool mounted = status != "NotMounted";
            _isXServerVhdxMountedManual = mounted;
            string label = !mounted ? "Unmounted" : (status == "Mounted" ? "Mounted" : $"Mounted ({status})");
            TxtXServerMountBadge.Text = label;
            TxtXServerMountBadge.Foreground = mounted ? SuccessBrushColor : MutedBrushColor;
            DrawerXServerMountText.Text = $"xServer: {label}";
            DrawerXServerMountText.Foreground = mounted ? SuccessBrushColor : MutedBrushColor;
            BtnDashMountXServer.Content = mounted ? "Eject Drive" : "Mount Drive";
            BtnToggleXServerMount.Content = mounted ? "Eject xServer Drive" : "Mount xServer Drive";
        }

        // =========================================================================
        // WINDOW CLOSING & GUARANTEED DISMOUNT
        // =========================================================================
        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isCleanupDone) return;
            if (_isClosingInProgress) { e.Cancel = true; return; }

            if (!_isSilentMode && !_isForceExit && (_isProcessing || _isVerifying))
            {
                e.Cancel = true;
                SaveWindowPlacementSettings();
                _ = Dispatcher.InvokeAsync(Hide);
                return;
            }

            e.Cancel = true;
            _isClosingInProgress = true;
            AppLogger.Info("APP", $"MainWindow_Closing started (silent={_isSilentMode}, forceExit={_isForceExit}).");

            if (_isProcessing || _isVerifying)
            {
                _cts?.Cancel();
            }

            // Safety watchdog (35s so 5-attempt verified dismount has time to complete cleanly)
            var watchdogCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(35000, watchdogCts.Token);
                    if (!_isCleanupDone)
                    {
                        AppLogger.Warn("APP", "Shutdown watchdog triggered; forcing final dismount check and process exit.");
                        ForceVerifyAllDismounted();
                        Process.GetCurrentProcess().Kill();
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });

            if (Visibility == Visibility.Visible)
            {
                TxtStatus.Text = "Shutting down...";
                StatusDot.Fill = AccentBrightBrush;
                PrgWaiting.Visibility = Visibility.Visible;
                PrgBar.IsIndeterminate = true;
                TxtProgressDetails.Text = "Verifying ReFS containers are dismounted before exit...";
                TxtProgressDetails.Foreground = AccentBrightBrush;
            }

            await Task.Delay(50);

            if (_isProcessing || _isVerifying)
            {
                int waitCount = 0;
                while ((_isProcessing || _isVerifying) && waitCount < 40)
                {
                    await Task.Delay(100);
                    waitCount++;
                }
            }

            SaveWindowPlacementSettings();
            SyncXServerFieldsToSettings();

            await Task.Run(ForceVerifyAllDismounted);

            watchdogCts.Cancel();
            _notifyIcon?.Dispose();
            _isCleanupDone = true;
            AppLogger.Info("APP", "xBackup cleanup complete; exiting cleanly.");
            try { Application.Current.Shutdown(); } catch { }
            Environment.Exit(0);
        }

        private void ForceVerifyAllDismounted()
        {
            string homeDest = Dispatcher.Invoke(() => string.IsNullOrWhiteSpace(TxtDestRoot.Text) ? _destinationRoot : TxtDestRoot.Text.Trim());
            string homeVhdx = Path.Combine(homeDest, "BackupDev.vhdx");
            string xServerVhdx = _xServerSettings.VhdxFullPath;

            bool homeOk = VhdxManager.DismountVhdxVerified(homeVhdx, "Home-PC", null);
            bool xServerOk = VhdxManager.DismountVhdxVerified(xServerVhdx, "xServer", null);

            AppLogger.Info("DISMOUNT", $"Pre-exit verification complete: Home-PC unmounted={homeOk}, xServer unmounted={xServerOk}.");
        }

        private void RestoreFromTray()
        {
            if (_isClosingInProgress || _isCleanupDone) return;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MenuShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying)
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                var res = MessageBox.Show(
                    this,
                    "A backup or verification job is actively running.\n\nStop the operation, verify containers are dismounted, and exit?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _isForceExit = true;
            Close();
        }

        // =========================================================================
        // SILENT SCHEDULED EXECUTION (HOME-PC + XSERVER + VERIFIED DISMOUNT)
        // =========================================================================
        public async void ExecuteSilentScheduledBackup()
        {
            AppLogger.Info("SCHEDULE", "=== Midnight silent scheduled backup started ===");
            DeleteCheckpoint();

            _destinationRoot = string.IsNullOrWhiteSpace(TxtDestRoot.Text) ? _destinationRoot : TxtDestRoot.Text.Trim();
            SetUiState(processing: true, statusLabel: "Scheduled Run");

            _cts = new CancellationTokenSource();

            // Single KeepAwakeSession covers the ENTIRE silent run: Home-PC + xServer + all dismounts!
            using (var keepAwake = new KeepAwakeSession("Scheduled Midnight Backup"))
            {
                try
                {
                    // 1. Home-PC incremental ReFS snapshot
                    AppLogger.Info("SCHEDULE", "Phase 1/2: Starting Home-PC incremental backup...");
                    await Task.Run(() => RunBackupEngine(_destinationRoot, _cts.Token, checkpoint: null, forceDismountOnFinish: true));

                    // Verify Home-PC is unmounted before proceeding
                    string homeVhdx = Path.Combine(_destinationRoot, "BackupDev.vhdx");
                    bool homeUnmounted = await Task.Run(() => VhdxManager.DismountVhdxVerified(homeVhdx, "Home-PC", null));
                    AppLogger.Info("SCHEDULE", $"Phase 1/2 finished. Home-PC VHDX verified unmounted={homeUnmounted}.");

                    // 2. xServer incremental ReFS clone
                    if (_xServerSettings.Enabled && !_cts.IsCancellationRequested)
                    {
                        AppLogger.Info("SCHEDULE", "Phase 2/2: Starting xServer incremental ReFS clone...");
                        var engine = new XServerBackupEngine();
                        var xRes = await Task.Run(() => engine.RunIncrementalClone(
                            _xServerSettings,
                            forceDismountOnFinish: true,
                            _cts.Token,
                            log: (lvl, msg) => AppendLog($"[xServer] {msg}", lvl == "ERROR" ? DangerBrushColor : lvl == "WARN" ? AccentBrightBrush : DefaultTextBrush),
                            progress: OnXServerProgress));
                        AppLogger.Info("SCHEDULE", $"Phase 2/2 finished. Success={xRes.Success}, Summary='{xRes.Summary}', xServer VHDX verified unmounted={xRes.VhdxVerifiedUnmounted}.");
                    }
                    else
                    {
                        AppLogger.Info("SCHEDULE", "Phase 2/2 skipped (xServer backup disabled or cancelled).");
                    }
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Warn("SCHEDULE", "Scheduled backup cancelled.");
                    AppendLog("Scheduled backup cancelled.", AccentBrightBrush);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("SCHEDULE", $"Scheduled backup error: {ex.Message}");
                    AppendLog($"Scheduled backup encountered an error: {ex.Message}", DangerBrushColor);
                }
                finally
                {
                    _cts?.Dispose();
                    _cts = null;
                    SetUiState(processing: false);

                    // Final rock-solid verified dismount check BEFORE releasing KeepAwake and closing!
                    await Task.Run(ForceVerifyAllDismounted);
                    AppLogger.Info("SCHEDULE", "=== Midnight silent scheduled backup finished ===");
                }
            }

            await Task.Delay(500);
            Close();
        }

        // =========================================================================
        // UI BUTTON HANDLERS — COMBINED, HOME-PC, XSERVER
        // =========================================================================
        private async void BtnRunAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;

            _destinationRoot = TxtDestRoot.Text.Trim();
            SyncXServerFieldsToSettings();
            SetUiState(processing: true, statusLabel: "Running All");
            RtbLog.Document.Blocks.Clear();
            PrgBar.Value = 0;

            _cts = new CancellationTokenSource();
            using var keepAwake = new KeepAwakeSession("Manual Full Run (Home-PC + xServer)");

            try
            {
                AppendLog("Starting full backup cycle (Home-PC + xServer)...", AccentBrightBrush);
                await Task.Run(() => RunBackupEngine(_destinationRoot, _cts.Token, checkpoint: null, forceDismountOnFinish: false));

                if (_xServerSettings.Enabled && !_cts.IsCancellationRequested)
                {
                    AppendLog("Starting xServer incremental ReFS clone...", AccentBrightBrush);
                    var engine = new XServerBackupEngine();
                    await Task.Run(() => engine.RunIncrementalClone(
                        _xServerSettings,
                        forceDismountOnFinish: false,
                        _cts.Token,
                        log: (lvl, msg) => AppendLog($"[xServer] {msg}", lvl == "ERROR" ? DangerBrushColor : lvl == "WARN" ? AccentBrightBrush : DefaultTextBrush),
                        progress: OnXServerProgress));
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("Backup cycle cancelled by user.", AccentBrightBrush);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetUiState(processing: false);
                UpdateDashboardSummary();
                _ = Task.Run(RefreshAllMountStatuses);
            }
        }

        private async void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;

            _destinationRoot = TxtDestRoot.Text.Trim();
            BackupCheckpoint? checkpoint = LoadCheckpoint();
            bool resume = false;
            if (checkpoint != null && checkpoint.DestinationRoot == _destinationRoot)
            {
                var result = MessageBox.Show(
                    $"An interrupted backup from {checkpoint.Timestamp:g} was found.\nResume from file {checkpoint.CurrentIndex:N0} of {checkpoint.FilesToProcess.Count:N0}?",
                    "Resume Backup?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                resume = result == MessageBoxResult.Yes;
            }

            if (!resume) { DeleteCheckpoint(); checkpoint = null; }

            SetUiState(processing: true, statusLabel: "Home-PC Active");
            if (!resume) { RtbLog.Document.Blocks.Clear(); PrgBar.Value = 0; }

            _cts = new CancellationTokenSource();
            using var keepAwake = new KeepAwakeSession("Manual Home-PC Backup");

            try
            {
                await Task.Run(() => RunBackupEngine(_destinationRoot, _cts.Token, checkpoint, forceDismountOnFinish: false));
            }
            catch (OperationCanceledException)
            {
                AppendLog("Home-PC backup cancelled by user.", AccentBrightBrush);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetUiState(processing: false);
                _ = Task.Run(RefreshAllMountStatuses);
            }
        }

        private async void BtnRunXServer_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;

            SyncXServerFieldsToSettings();
            SetUiState(processing: true, statusLabel: "xServer Clone");
            PrgBar.Value = 0;
            PrgBar.Maximum = 100;
            _cts = new CancellationTokenSource();

            try
            {
                var engine = new XServerBackupEngine();
                var res = await Task.Run(() => engine.RunIncrementalClone(
                    _xServerSettings,
                    forceDismountOnFinish: false,
                    _cts.Token,
                    log: (lvl, msg) => AppendLog($"[xServer] {msg}", lvl == "ERROR" ? DangerBrushColor : lvl == "WARN" ? AccentBrightBrush : DefaultTextBrush),
                    progress: OnXServerProgress));

                AppendLog(res.Success ? $"xServer clone complete: {res.Summary}" : $"xServer clone ended: {res.Summary}",
                    res.Success ? SuccessBrushColor : DangerBrushColor);
            }
            catch (OperationCanceledException)
            {
                AppendLog("xServer clone cancelled by user.", AccentBrightBrush);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetUiState(processing: false);
                UpdateDashboardSummary();
                _ = Task.Run(RefreshAllMountStatuses);
            }
        }

        private void OnXServerProgress(XServerBackupProgress p)
        {
            Dispatcher.Invoke(() =>
            {
                PrgBar.Maximum = 100;
                PrgBar.Value = Math.Clamp(p.Percent, 0, 100);
                TxtProgressDetails.Text = $"[xServer • {p.Stage}] {p.CurrentItem}";
                TxtProgressDetails.Foreground = AccentBrightBrush;
                LblScanned.Text = p.Scanned.ToString("N0");
                LblBackedUp.Text = p.Updated.ToString("N0");
                LblUpToDate.Text = p.Unchanged.ToString("N0");
                LblLocked.Text = p.Errors.ToString("N0");
                LblSavings.Text = XServerBackupEngine.FormatBytes(p.BytesTransferred);
            });
        }

        private async void BtnTestXServer_Click(object sender, RoutedEventArgs e)
        {
            SyncXServerFieldsToSettings();
            BtnTestXServer.IsEnabled = false;
            BtnDashTestXServer.IsEnabled = false;
            PrgWaiting.Visibility = Visibility.Visible;
            TxtProgressDetails.Text = $"Testing SSH to {_xServerSettings.Username}@{_xServerSettings.Host}...";
            try
            {
                var (ok, msg) = await Task.Run(() => XServerBackupEngine.TestConnection(_xServerSettings));
                AppendLog($"[xServer SSH] {msg}", ok ? SuccessBrushColor : DangerBrushColor);
                MessageBox.Show(msg, ok ? "SSH Connected" : "SSH Connection Failed", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally
            {
                BtnTestXServer.IsEnabled = true;
                BtnDashTestXServer.IsEnabled = true;
                PrgWaiting.Visibility = Visibility.Collapsed;
                TxtProgressDetails.Text = "Ready";
            }
        }

        private void BtnSaveXServerSettings_Click(object sender, RoutedEventArgs e)
        {
            SyncXServerFieldsToSettings();
            AppendLog("xServer settings saved.", SuccessBrushColor);
            MessageBox.Show("xServer clone settings have been saved.", "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnBrowseXServerDest_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select xServer ReFS Container Folder",
                InitialDirectory = Directory.Exists(TxtXServerDestRoot.Text) ? TxtXServerDestRoot.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (dialog.ShowDialog() == true)
            {
                TxtXServerDestRoot.Text = dialog.FolderName;
                SyncXServerFieldsToSettings();
            }
        }

        private async void BtnToggleXServerMount_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;
            SyncXServerFieldsToSettings();
            string vhdxPath = _xServerSettings.VhdxFullPath;

            BtnToggleXServerMount.IsEnabled = false;
            BtnDashMountXServer.IsEnabled = false;
            PrgWaiting.Visibility = Visibility.Visible;

            try
            {
                if (!_isXServerVhdxMountedManual)
                {
                    if (!File.Exists(vhdxPath))
                    {
                        AppendLog($"Creating xServer ReFS container ({_xServerSettings.ContainerSizeGb} GB dynamic)...", AccentBrightBrush);
                        await Task.Run(() => VhdxManager.EnsureVhdxExists(vhdxPath, _xServerSettings.ContainerSizeGb, _xServerSettings.MountDriveLetter, "xServerReFS"));
                    }
                    string letter = await Task.Run(() => VhdxManager.MountVhdxAndGetLetter(vhdxPath, _xServerSettings.MountDriveLetter));
                    UpdateXServerMountUi(letter);
                    AppendLog($"xServer ReFS container mounted at {letter}", SuccessBrushColor);
                    Process.Start("explorer.exe", letter);
                }
                else
                {
                    AppendLog("Dismounting xServer ReFS container...", AccentBrightBrush);
                    bool ok = await Task.Run(() => VhdxManager.DismountVhdxVerified(vhdxPath, "xServer", null));
                    UpdateXServerMountUi(ok ? "NotMounted" : VhdxManager.CheckVhdxMountStatus(vhdxPath));
                    AppendLog(ok ? "xServer ReFS container verified unmounted." : "Warning: xServer container may still be attached.", ok ? SuccessBrushColor : DangerBrushColor);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"xServer mount/eject error: {ex.Message}", DangerBrushColor);
                MessageBox.Show(ex.Message, "xServer Container Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnToggleXServerMount.IsEnabled = true;
                BtnDashMountXServer.IsEnabled = true;
                PrgWaiting.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnResizeXServerVhdx_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;
            SyncXServerFieldsToSettings();
            await ResizeContainerInternal(_xServerSettings.VhdxFullPath, _xServerSettings.ContainerSizeGb, "xServer", _xServerSettings.MountDriveLetter, TxtXServerContainerSizeGb);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if ((_isProcessing || _isVerifying) && _cts != null)
            {
                AppendLog("Stop requested — winding down safely...", AccentBrightBrush);
                PrgWaiting.Visibility = Visibility.Visible;
                _cts.Cancel();
                BtnStop.IsEnabled = false;
            }
        }

        private void BtnBrowseDest_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Home-PC Backup Destination Folder Root",
                InitialDirectory = Directory.Exists(_destinationRoot) ? _destinationRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (dialog.ShowDialog() == true)
            {
                TxtDestRoot.Text = dialog.FolderName;
                _destinationRoot = dialog.FolderName;
                SaveWindowPlacementSettings();
                UpdateDashboardSummary();
                _ = Task.Run(RefreshAllMountStatuses);
            }
        }

        private void BtnConfigureExclusions_Click(object sender, RoutedEventArgs e)
        {
            var settingsWin = new SettingsWindow { Owner = this };
            settingsWin.ShowDialog();
            RefreshBackupScopesDisplay();
        }

        private async void BtnToggleMount_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;
            _destinationRoot = TxtDestRoot.Text.Trim();
            string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");

            BtnToggleMount.IsEnabled = false;
            BtnHomeMountAlt.IsEnabled = false;
            PrgWaiting.Visibility = Visibility.Visible;

            try
            {
                if (!_isVhdxMountedManual)
                {
                    if (!File.Exists(vhdxPath))
                    {
                        int gb = GetConfiguredContainerSizeGb();
                        string letterPref = GetSelectedDriveLetter(CboDriveLetter, "X");
                        AppendLog($"Creating Home-PC ReFS container ({gb} GB dynamic)...", AccentBrightBrush);
                        await Task.Run(() => VhdxManager.EnsureVhdxExists(vhdxPath, gb, letterPref, "BackupReFS"));
                    }
                    string letter = await Task.Run(() => VhdxManager.MountVhdxAndGetLetter(vhdxPath, GetSelectedDriveLetter(CboDriveLetter, "X")));
                    UpdateHomeMountUi(letter);
                    AppendLog($"Home-PC ReFS container mounted at {letter}", SuccessBrushColor);
                    Process.Start("explorer.exe", letter);
                }
                else
                {
                    AppendLog("Dismounting Home-PC ReFS container...", AccentBrightBrush);
                    bool ok = await Task.Run(() => VhdxManager.DismountVhdxVerified(vhdxPath, "Home-PC", null));
                    UpdateHomeMountUi(ok ? "NotMounted" : VhdxManager.CheckVhdxMountStatus(vhdxPath));
                    AppendLog(ok ? "Home-PC ReFS container verified unmounted." : "Warning: Home-PC container may still be attached.", ok ? SuccessBrushColor : DangerBrushColor);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Mount/Eject error: {ex.Message}", DangerBrushColor);
                MessageBox.Show(ex.Message, "Container Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnToggleMount.IsEnabled = true;
                BtnHomeMountAlt.IsEnabled = true;
                PrgWaiting.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnResizeVhdx_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;
            string dest = string.IsNullOrWhiteSpace(TxtDestRoot.Text) ? _destinationRoot : TxtDestRoot.Text.Trim();
            string vhdxPath = Path.Combine(dest, "BackupDev.vhdx");
            int newSizeGb = GetConfiguredContainerSizeGb();
            await ResizeContainerInternal(vhdxPath, newSizeGb, "Home-PC", GetSelectedDriveLetter(CboDriveLetter, "X"), TxtContainerSizeGb);
        }

        private async Task ResizeContainerInternal(string vhdxPath, int newSizeGb, string label, string driveLetter, TextBox sizeBox)
        {
            if (!File.Exists(vhdxPath))
            {
                MessageBox.Show($"No existing VHDX container was found at:\n{vhdxPath}", "Container Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int currentGb = await Task.Run(() => VhdxManager.GetVhdxVirtualSizeGb(vhdxPath));
            if (currentGb > 0 && newSizeGb <= currentGb)
            {
                MessageBox.Show($"Desired capacity ({newSizeGb} GB) must be greater than current capacity ({currentGb} GB).", "Expansion Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Expand {label} container from {currentGb} GB to {newSizeGb} GB?\nAll existing files will be preserved.",
                "Confirm Container Expansion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetUiState(processing: true, statusLabel: "Expanding...");
            PrgBar.IsIndeterminate = true;
            try
            {
                await Task.Run(() =>
                {
                    VhdxManager.DismountVhdxVerified(vhdxPath, label, null);
                    VhdxManager.ExpandVhdx(vhdxPath, newSizeGb);
                });
                int verified = await Task.Run(() => VhdxManager.GetVhdxVirtualSizeGb(vhdxPath));
                if (verified > 0) sizeBox.Text = verified.ToString();
                AppendLog($"{label} container expanded to {newSizeGb} GB.", SuccessBrushColor);
                MessageBox.Show($"{label} container successfully expanded to {newSizeGb} GB!", "Expanded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"Container expansion failed: {ex.Message}", DangerBrushColor);
                MessageBox.Show($"Failed to expand container:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PrgBar.IsIndeterminate = false;
                SetUiState(processing: false);
                _ = Task.Run(RefreshAllMountStatuses);
            }
        }

        // =========================================================================
        // SCHEDULER & SETTINGS HANDLERS
        // =========================================================================
        private void RefreshTaskSchedulerStatusAsync()
        {
            bool exists = TaskSchedulerHelper.DoesTaskExist();
            string summary = TaskSchedulerHelper.GetTaskStatusSummary();
            Dispatcher.Invoke(() =>
            {
                BtnScheduleTask.Content = exists ? "Unregister Midnight Task" : "Register Midnight Wake Task";
                TxtSchedulerSummary.Text = summary;
                TxtDashScheduleInfo.Text = summary;
            });
        }

        private void BtnScheduleTask_Click(object sender, RoutedEventArgs e)
        {
            if (TaskSchedulerHelper.DoesTaskExist())
            {
                TaskSchedulerHelper.DeleteBackupTask();
                AppendLog("Unregistered daily midnight scheduled task.", AccentBrightBrush);
            }
            else
            {
                if (!TaskSchedulerHelper.IsRunningAsAdmin())
                {
                    MessageBox.Show("Administrator privileges are required to register scheduled tasks.", "Admin Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                bool ok = TaskSchedulerHelper.CreateDailyBackupTask();
                AppendLog(ok ? "Registered daily midnight wake task (6h limit)." : "Failed to register scheduled task.", ok ? SuccessBrushColor : DangerBrushColor);
            }
            Task.Run(RefreshTaskSchedulerStatusAsync);
        }

        private void BtnUpdateTask_Click(object sender, RoutedEventArgs e)
        {
            if (!TaskSchedulerHelper.IsRunningAsAdmin())
            {
                MessageBox.Show("Administrator privileges are required.", "Admin Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool ok = TaskSchedulerHelper.CreateDailyBackupTask();
            AppendLog(ok ? "Updated daily midnight task with 6-hour limit and WakeToRun." : "Failed to update task.", ok ? SuccessBrushColor : DangerBrushColor);
            Task.Run(RefreshTaskSchedulerStatusAsync);
        }

        private void BtnRefreshTask_Click(object sender, RoutedEventArgs e) => Task.Run(RefreshTaskSchedulerStatusAsync);

        private void ChkWeeklyIntegrity_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _enableWeeklyIntegrityInSilent = ChkWeeklyIntegrity.IsChecked == true;
            SaveWindowPlacementSettings();
        }

        private async void BtnVerifyDismountAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;
            BtnVerifyDismountAll.IsEnabled = false;
            PrgWaiting.Visibility = Visibility.Visible;
            try
            {
                await Task.Run(ForceVerifyAllDismounted);
                await Task.Run(RefreshAllMountStatuses);
                AppendLog("Verified all VHDX containers are dismounted.", SuccessBrushColor);
            }
            finally
            {
                BtnVerifyDismountAll.IsEnabled = true;
                PrgWaiting.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", AppLogger.LogDirectory); } catch { }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e) => RtbLog.Document.Blocks.Clear();

        // =========================================================================
        // UI STATE & RESTORE / VERIFY
        // =========================================================================
        private void SetUiState(bool processing, string? statusLabel = null)
        {
            Dispatcher.Invoke(() =>
            {
                _isProcessing = processing;
                BtnRunAll.IsEnabled = !processing;
                BtnBackup.IsEnabled = !processing;
                BtnRunXServer.IsEnabled = !processing;
                BtnDashRunXServer.IsEnabled = !processing;
                BtnRestore.IsEnabled = !processing;
                BtnVerify.IsEnabled = !processing;
                BtnToggleMount.IsEnabled = !processing;
                BtnHomeMountAlt.IsEnabled = !processing;
                BtnToggleXServerMount.IsEnabled = !processing;
                BtnDashMountXServer.IsEnabled = !processing;
                BtnResizeVhdx.IsEnabled = !processing;
                BtnResizeXServerVhdx.IsEnabled = !processing;
                BtnStop.IsEnabled = processing;
                PrgWaiting.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;
                TxtStatus.Text = processing ? (statusLabel ?? "Active") : "Ready";
                StatusDot.Fill = processing ? AccentBrushColor : SuccessBrushColor;
            });
        }

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _isVerifying) return;

            _destinationRoot = TxtDestRoot.Text.Trim();
            string vhdxPath = Path.Combine(_destinationRoot, "BackupDev.vhdx");

            string mountedDrive;
            try
            {
                AppendLog("Mounting Home-PC ReFS container for restore...", AccentBrightBrush);
                mountedDrive = await Task.Run(() => VhdxManager.MountVhdxAndGetLetter(vhdxPath, GetSelectedDriveLetter(CboDriveLetter, "X")));
                UpdateHomeMountUi(mountedDrive);
                await Task.Delay(2500);
            }
            catch (Exception ex)
            {
                AppendLog($"Mount failure: {ex.Message}", DangerBrushColor);
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

                var selectedItems = restoreWindow.SelectedNodes.Select(n => (n.FullPath, n.Version!)).ToList();
                SetUiState(processing: true, statusLabel: "Restoring");
                PrgBar.Value = 0;
                _cts = new CancellationTokenSource();

                using var keepAwake = new KeepAwakeSession("Catalog Restore");
                await Task.Run(() => RunCatalogRestoreEngine(mountedDrive, selectedItems, restoreWindow.TargetPath, restoreWindow.FullRestore, restoreWindow.Overwrite, _cts.Token));
            }
            catch (Exception ex)
            {
                AppendLog($"Restore failed: {ex.Message}", DangerBrushColor);
                MessageBox.Show($"Could not initialize restore: {ex.Message}", "Restore Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetUiState(processing: false);
            }
        }

        private void RunCatalogRestoreEngine(string mountedDrive, List<(string sourcePath, FileVersion version)> filesToRestore, string? targetPath, bool fullRestore, bool overwrite, CancellationToken cancellationToken)
        {
            var restoreErrors = new List<string>();
            try
            {
                AppendLog($"Initializing Catalog Restore for {filesToRestore.Count:N0} files...", AccentBrightBrush);
                string dbPath = Path.Combine(mountedDrive, "BackupCatalog.db");
                using var catalog = new BackupCatalog(dbPath);
                var engine = new RestoreEngine(catalog, mountedDrive);

                int errorCount = 0;
                engine.RestoreFiles(filesToRestore, targetPath, fullRestore, overwrite, cancellationToken, (file, current, total) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        CurrentFilePath = file;
                        TxtProgressDetails.Text = $"[{current:N0}/{total:N0}] Restoring: {Path.GetFileName(file)}";
                        TxtProgressDetails.Foreground = AccentBrightBrush;
                        if (current % 100 == 0 || current == total)
                        {
                            PrgBar.Maximum = total;
                            PrgBar.Value = current;
                            LblScanned.Text = total.ToString("N0");
                            LblBackedUp.Text = (current - errorCount).ToString("N0");
                            LblLocked.Text = errorCount.ToString("N0");
                        }
                    });
                }, err => { errorCount++; restoreErrors.Add(err); });

                if (restoreErrors.Count > 0)
                {
                    AppendLog($"Restore finished with {restoreErrors.Count} errors.", AccentBrightBrush);
                    foreach (var err in restoreErrors.Take(50)) AppendLog($"-> {err}", DangerBrushColor);
                }
                else
                {
                    AppendLog("Catalog restore completed successfully.", SuccessBrushColor);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Restore engine failure: {ex.Message}", DangerBrushColor);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    TxtProgressDetails.Text = "Restore Operation Finished";
                    CurrentFilePath = null;
                    TxtProgressDetails.Foreground = MutedBrushColor;
                });
            }
        }

        // =========================================================================
        // HOME-PC INCREMENTAL BACKUP ENGINE
        // =========================================================================
        private void RunBackupEngine(string destRoot, CancellationToken cancellationToken, BackupCheckpoint? checkpoint = null, bool forceDismountOnFinish = false)
        {
            string vhdxPath = Path.Combine(destRoot, "BackupDev.vhdx");
            string mountedDrive = string.Empty;
            bool mountedByUs = false;

            List<string> filesToProcess = [];
            long backedUpCount = checkpoint?.BackedUpCount ?? 0;
            long upToDateCount = checkpoint?.UpToDateCount ?? 0;
            long lockedCount = checkpoint?.LockedCount ?? 0;
            long totalBytesMirrored = checkpoint?.TotalBytesMirrored ?? 0;
            long purgedFilesCount = 0;

            try
            {
                AppendLog(checkpoint == null ? "Initializing Home-PC Incremental Snapshot..." : "Resuming Home-PC Snapshot...", AccentBrightBrush);

                if (!Directory.Exists(destRoot)) Directory.CreateDirectory(destRoot);

                if (!File.Exists(vhdxPath))
                {
                    int initialSizeGb = GetConfiguredContainerSizeGb();
                    string letter = GetSelectedDriveLetter(CboDriveLetter, "X");
                    AppendLog($"Creating Home-PC ReFS VHDX container ({initialSizeGb} GB dynamic)...", AccentBrightBrush);
                    VhdxManager.EnsureVhdxExists(vhdxPath, initialSizeGb, letter, "BackupReFS");
                    AppendLog("Home-PC VHDX container created.", SuccessBrushColor);
                }

                string preStatus = VhdxManager.CheckVhdxMountStatus(vhdxPath);
                AppendLog("Mounting Home-PC ReFS VHDX container...", AccentBrightBrush);
                mountedDrive = VhdxManager.MountVhdxAndGetLetter(vhdxPath, GetSelectedDriveLetter(CboDriveLetter, "X"));
                mountedByUs = (preStatus == "NotMounted");
                Dispatcher.Invoke(() => UpdateHomeMountUi(mountedDrive));
                AppendLog($"Home-PC VHDX attached at {mountedDrive} (mountedByUs={mountedByUs})", SuccessBrushColor);

                Thread.Sleep(2000);

                string dbPath = Path.Combine(mountedDrive, "BackupCatalog.db");
                using (var catalog = new BackupCatalog(dbPath))
                {
                    catalog.MarkAbandonedSnapshotsFailed();

                    string nowString = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    string snapshotFolderName = $"Snapshot_{nowString}";
                    string activeSnapshotDir = Path.Combine(mountedDrive, snapshotFolderName);

                    var snapshot = catalog.CreateSnapshot(DateTime.Now);
                    Directory.CreateDirectory(activeSnapshotDir);
                    AppendLog($"Created snapshot folder: {snapshotFolderName}", AccentBrightBrush);

                    // History retention pruning
                    try
                    {
                        var snapshots = catalog.GetCompletedSnapshots();
                        int prunedFoldersCount = 0;
                        int promotedFilesCount = 0;

                        foreach (var oldSnap in snapshots)
                        {
                            if ((DateTime.Today - oldSnap.SnapshotDate).TotalDays > GlobalExclusions.RetentionDays)
                            {
                                AppendLog($"Smart-pruning expired snapshot: {oldSnap.SnapshotDate:yyyy-MM-dd}", AccentBrightBrush);
                                var versions = catalog.GetFileVersionsForSnapshot(oldSnap.Id);
                                foreach (var v in versions)
                                {
                                    if (v.ChangeType == ChangeType.Deleted || string.IsNullOrEmpty(v.BackupPath)) continue;
                                    string oldFull = Path.Combine(mountedDrive, v.BackupPath);
                                    if (!File.Exists(oldFull)) continue;

                                    if (!v.IsCorrupted && !catalog.HasNewerVersion(v.FileId, oldSnap.Id))
                                    {
                                        try
                                        {
                                            int firstSlash = v.BackupPath.IndexOf(Path.DirectorySeparatorChar);
                                            if (firstSlash == -1) firstSlash = v.BackupPath.IndexOf('/');
                                            if (firstSlash != -1)
                                            {
                                                string relativeSubPath = v.BackupPath[(firstSlash + 1)..];
                                                string newRelative = Path.Combine(snapshotFolderName, relativeSubPath);
                                                string newFull = Path.Combine(mountedDrive, newRelative);
                                                string? parent = Path.GetDirectoryName(newFull);
                                                if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                                                File.Move(oldFull, newFull, true);
                                                catalog.UpdateVersionLocation(v.Id, snapshot.Id, newRelative);
                                                promotedFilesCount++;
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                string datePattern = $"Snapshot_{oldSnap.SnapshotDate:yyyy-MM-dd}*";
                                foreach (var dir in Directory.GetDirectories(mountedDrive, datePattern))
                                {
                                    try { Directory.Delete(dir, true); } catch { }
                                }
                                catalog.DeleteSnapshot(oldSnap.Id);
                                prunedFoldersCount++;
                            }
                        }

                        if (prunedFoldersCount > 0)
                        {
                            AppendLog($"Retention complete: pruned {prunedFoldersCount} snapshots, promoted {promotedFilesCount:N0} current files.", SuccessBrushColor);
                        }
                    }
                    catch (Exception rentEx)
                    {
                        AppendLog($"Retention warning: {rentEx.Message}", AccentBrightBrush);
                    }

                    _errorDetailsReport.Clear();
                    long totalScannedCount = 0;

                    if (checkpoint != null)
                    {
                        filesToProcess = checkpoint.FilesToProcess;
                        AppendLog($"Resumed discovery: {filesToProcess.Count:N0} files.", SuccessBrushColor);
                    }
                    else
                    {
                        AppendLog("Scanning Home-PC drives...", AccentBrightBrush);
                        foreach (var sourceRoot in new List<string>(GlobalExclusions.SelectedDrives))
                        {
                            if (!Directory.Exists(sourceRoot)) continue;
                            DiscoverFilesRecursively(sourceRoot, filesToProcess, ref totalScannedCount, cancellationToken);
                        }
                        AppendLog($"Discovery complete: {filesToProcess.Count:N0} files matched.", SuccessBrushColor);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        LblScanned.Text = filesToProcess.Count.ToString("N0");
                        PrgBar.Maximum = Math.Max(1, filesToProcess.Count);
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

                        if (currentIndex % 100 == 0 || currentIndex == filesToProcess.Count)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                CurrentFilePath = file;
                                TxtProgressDetails.Text = $"[Home-PC • {currentIndex:N0}/{filesToProcess.Count:N0}] {Path.GetFileName(file)}";
                                TxtProgressDetails.Foreground = AccentBrightBrush;
                                PrgBar.Value = currentIndex;
                                LblBackedUp.Text = backedUpCount.ToString("N0");
                                LblUpToDate.Text = upToDateCount.ToString("N0");
                                LblLocked.Text = lockedCount.ToString("N0");
                                LblSavings.Text = FormatBytes(totalBytesMirrored);
                            });
                        }

                        FileInfo sourceFi;
                        try
                        {
                            sourceFi = new FileInfo(file);
                            if (!sourceFi.Exists) continue;
                        }
                        catch { lockedCount++; continue; }

                        var catalogFile = catalog.GetOrCreateFile(file);
                        seenFilesIds.Add(catalogFile.Id);
                        var latestVersion = catalog.GetLatestVersion(catalogFile.Id);

                        bool needsCopy = true;
                        ChangeType changeType = ChangeType.New;

                        if (latestVersion != null && latestVersion.ChangeType != ChangeType.Deleted && !latestVersion.IsCorrupted)
                        {
                            bool sizeMatch = latestVersion.Size == sourceFi.Length;
                            bool dateMatch = latestVersion.LastWriteUtc.ToUniversalTime().Ticks == sourceFi.LastWriteTimeUtc.Ticks;
                            if (sizeMatch && dateMatch) needsCopy = false;
                            else changeType = ChangeType.Modified;
                        }

                        if (!needsCopy) { upToDateCount++; continue; }

                        string relativeStructurePath = MapToBackupPath(file);
                        string destFilePath = Path.Combine(activeSnapshotDir, relativeStructurePath);
                        string tempFilePath = destFilePath + ".tmp";

                        try
                        {
                            string? parentDir = Path.GetDirectoryName(destFilePath);
                            if (parentDir != null && !Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);

                            string hash;
                            using (var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                            using (var sha256 = SHA256.Create())
                            {
                                byte[] buffer = new byte[81920];
                                int bytesRead;
                                long totalRead = 0;
                                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                                    destStream.Write(buffer, 0, bytesRead);
                                    totalRead += bytesRead;
                                }
                                sha256.TransformFinalBlock(buffer, 0, 0);
                                hash = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();
                                if (totalRead != sourceFi.Length) throw new IOException("Copied file size mismatch.");
                            }

                            File.Move(tempFilePath, destFilePath, overwrite: true);
                            File.SetLastWriteTimeUtc(destFilePath, sourceFi.LastWriteTimeUtc);
                            CompressFileTransparently(destFilePath);

                            catalog.AddFileVersion(new FileVersion
                            {
                                FileId = catalogFile.Id,
                                SnapshotId = snapshot.Id,
                                BackupPath = Path.Combine(snapshotFolderName, relativeStructurePath),
                                Size = sourceFi.Length,
                                LastWriteUtc = sourceFi.LastWriteTimeUtc,
                                Hash = hash,
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

                    // Deletion tracking
                    AppendLog("Tracking deletions...", MutedBrushColor);
                    foreach (var f in catalog.GetAllFiles())
                    {
                        if (!seenFilesIds.Contains(f.Id))
                        {
                            bool inScope = GlobalExclusions.SelectedDrives.Any(d => f.SourcePath.StartsWith(d, StringComparison.OrdinalIgnoreCase));
                            if (inScope && !IsPathExcluded(f.SourcePath))
                            {
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
                    AppendLog($"Home-PC backup finished: {backedUpCount:N0} copied, {upToDateCount:N0} up-to-date, {purgedFilesCount:N0} deleted.", SuccessBrushColor);

                    if (_isSilentMode && _enableWeeklyIntegrityInSilent)
                    {
                        CheckAndRunWeeklyIntegrity(catalog, mountedDrive, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"Home-PC engine error: {ex.Message}", DangerBrushColor);
            }
            finally
            {
                WriteBackupReport(destRoot, filesToProcess.Count, backedUpCount, upToDateCount, lockedCount, purgedFilesCount, totalBytesMirrored);

                if (mountedByUs || forceDismountOnFinish)
                {
                    bool ok = VhdxManager.DismountVhdxVerified(
                        vhdxPath,
                        "Home-PC",
                        (msg, isWarn) => AppendLog(msg, isWarn ? AccentBrightBrush : SuccessBrushColor));
                    Dispatcher.Invoke(() => UpdateHomeMountUi(ok ? "NotMounted" : VhdxManager.CheckVhdxMountStatus(vhdxPath)));
                }
            }
        }

        private void WriteBackupReport(string destRoot, long totalScannedCount, long backedUpCount, long upToDateCount, long lockedCount, long purgedFilesCount, long totalBytesMirrored)
        {
            try
            {
                string historyDir = Path.Combine(destRoot, "BackupHistoryLogs");
                Directory.CreateDirectory(historyDir);
                string logFileName = $"BackupReport_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
                string fullLogPath = Path.Combine(historyDir, logFileName);

                using var sw = new StreamWriter(fullLogPath, false, Encoding.UTF8);
                sw.WriteLine("==========================================================================");
                sw.WriteLine("xBACKUP — HOME-PC INCREMENTAL SNAPSHOT REPORT");
                sw.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine("==========================================================================");
                sw.WriteLine($"Total Files Scanned:               {totalScannedCount:N0}");
                sw.WriteLine($"Successfully Copied (New/Modified) : {backedUpCount:N0}");
                sw.WriteLine($"Up-to-Date (Skipped Unchanged)     : {upToDateCount:N0}");
                sw.WriteLine($"Locked / Bypassed System Files     : {lockedCount:N0}");
                sw.WriteLine($"Files Marked as Deleted            : {purgedFilesCount:N0}");
                sw.WriteLine($"Total Data Streamed This Session   : {FormatBytes(totalBytesMirrored)}");
                sw.WriteLine("==========================================================================");

                if (_errorDetailsReport.Count > 0)
                {
                    sw.WriteLine();
                    sw.WriteLine("BYPASSED / LOCKED FILES:");
                    foreach (var errItem in _errorDetailsReport) sw.WriteLine(errItem);
                }
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                TxtProgressDetails.Text = "Operation Finished";
                CurrentFilePath = null;
                TxtProgressDetails.Foreground = MutedBrushColor;
                LblBackedUp.Text = backedUpCount.ToString("N0");
                LblUpToDate.Text = upToDateCount.ToString("N0");
                LblLocked.Text = lockedCount.ToString("N0");
                LblSavings.Text = FormatBytes(totalBytesMirrored);
            });
        }

        private int GetConfiguredContainerSizeGb()
        {
            int size = 200;
            Dispatcher.Invoke(() =>
            {
                if (int.TryParse(TxtContainerSizeGb.Text.Trim(), out int parsed) && parsed >= 10 && parsed <= 64000)
                    size = parsed;
            });
            return size;
        }

        private static void CompressFileTransparently(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                string[] skipExtensions = [".zip", ".7z", ".rar", ".gz", ".iso", ".png", ".jpg", ".jpeg", ".mp4", ".mov", ".mp3", ".exe", ".msi"];
                if (skipExtensions.Contains(ext)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = "compact.exe",
                    Arguments = $"/c /i /exe:lzx \"{filePath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(psi);
                process?.WaitForExit(15000);
            }
            catch { }
        }

        private static string CalculateHash(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            if (_isSilentMode) return;
            if (_isVerifying)
            {
                _cts?.Cancel();
                BtnVerify.IsEnabled = false;
                return;
            }
            if (_isProcessing) return;

            string destRoot = TxtDestRoot.Text.Trim();
            string vhdxPath = Path.Combine(destRoot, "BackupDev.vhdx");
            if (!File.Exists(vhdxPath))
            {
                MessageBox.Show($"Backup container not found at:\n{vhdxPath}", "Container Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isVerifying = true;
            SetUiState(processing: true, statusLabel: "Verifying");
            BtnVerify.IsEnabled = true;
            BtnVerify.Content = "Stop Verification";
            _cts = new CancellationTokenSource();

            bool mountedByUs = false;
            using var keepAwake = new KeepAwakeSession("Integrity Verification");

            try
            {
                await Task.Run(() =>
                {
                    string preStatus = VhdxManager.CheckVhdxMountStatus(vhdxPath);
                    string mountedDrive = VhdxManager.MountVhdxAndGetLetter(vhdxPath, GetSelectedDriveLetter(CboDriveLetter, "X"));
                    mountedByUs = (preStatus == "NotMounted");
                    Thread.Sleep(1500);

                    string dbPath = Path.Combine(mountedDrive, "BackupCatalog.db");
                    if (!File.Exists(dbPath))
                    {
                        AppendLog("Catalog database not found inside container.", DangerBrushColor);
                        return;
                    }

                    using var catalog = new BackupCatalog(dbPath);
                    int corruptCount = PerformIntegrityCheckInternal(catalog, mountedDrive, _cts.Token);
                    AppendLog(corruptCount == 0
                        ? "Integrity Check Complete: 0 corrupted files detected."
                        : $"Integrity Check Complete: {corruptCount} corrupted files marked for re-sync.",
                        corruptCount == 0 ? SuccessBrushColor : DangerBrushColor);
                }, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Integrity verification cancelled.", AccentBrightBrush);
            }
            catch (Exception ex)
            {
                AppendLog($"Verification failed: {ex.Message}", DangerBrushColor);
            }
            finally
            {
                if (mountedByUs)
                {
                    await Task.Run(() => VhdxManager.DismountVhdxVerified(vhdxPath, "Home-PC", null));
                }
                _isVerifying = false;
                BtnVerify.Content = "Verify Integrity (SHA-256)";
                SetUiState(processing: false);
                _cts?.Dispose();
                _cts = null;
                _ = Task.Run(RefreshAllMountStatuses);
            }
        }

        private void CheckAndRunWeeklyIntegrity(BackupCatalog catalog, string mountedDrive, CancellationToken cancellationToken)
        {
            try
            {
                string? lastCheckStr = catalog.GetMetadata("LastIntegrityCheckUtc");
                DateTime lastCheck = DateTime.MinValue;
                if (lastCheckStr != null) DateTime.TryParse(lastCheckStr, out lastCheck);

                if ((DateTime.UtcNow - lastCheck).TotalDays >= 7)
                {
                    AppendLog("Automated Weekly Integrity Check started...", AccentBrightBrush);
                    int corruptCount = PerformIntegrityCheckInternal(catalog, mountedDrive, cancellationToken);
                    catalog.SetMetadata("LastIntegrityCheckUtc", DateTime.UtcNow.ToString("O"));
                    AppendLog($"Weekly Integrity Check finished ({corruptCount} issues).", corruptCount == 0 ? SuccessBrushColor : AccentBrightBrush);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Weekly integrity check warning: {ex.Message}", AccentBrightBrush);
            }
        }

        private int PerformIntegrityCheckInternal(BackupCatalog catalog, string mountedDrive, CancellationToken cancellationToken)
        {
            var files = catalog.GetFilesForIntegrityVerification();
            int total = files.Count;
            if (total == 0) return 0;

            int checkedCount = 0;
            int corruptCount = 0;
            var sw = Stopwatch.StartNew();

            Dispatcher.Invoke(() => { PrgBar.Maximum = total; PrgBar.Value = 0; });

            foreach (var (id, backupPath, expectedHash) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string physicalPath = Path.Combine(mountedDrive, backupPath);

                if (!_isSilentMode && (checkedCount == 0 || sw.ElapsedMilliseconds > 100 || checkedCount == total - 1))
                {
                    sw.Restart();
                    Dispatcher.Invoke(() =>
                    {
                        CurrentFilePath = physicalPath;
                        TxtProgressDetails.Text = $"Verifying [{checkedCount + 1:N0}/{total:N0}] {Path.GetFileName(backupPath)}";
                        PrgBar.Value = checkedCount;
                    });
                }

                if (File.Exists(physicalPath))
                {
                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        try
                        {
                            if (CalculateHash(physicalPath) != expectedHash)
                            {
                                AppendLog($"BIT ROT DETECTED: {backupPath}", DangerBrushColor);
                                catalog.MarkVersionCorrupted(id);
                                corruptCount++;
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                    catalog.MarkVersionCorrupted(id);
                    corruptCount++;
                }
                checkedCount++;
            }

            return corruptCount;
        }

        // =========================================================================
        // FILE DISCOVERY & HELPERS
        // =========================================================================
        private void DiscoverFilesRecursively(string currentDir, List<string> files, ref long scannedCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new DirectoryInfo(currentDir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            }
            catch { return; }

            if (IsPathExcluded(currentDir)) return;

            try
            {
                foreach (var f in Directory.GetFiles(currentDir))
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
                _errorDetailsReport.Add($"-> Scope Folder Lock: {currentDir} | {dirEx.Message}");
                return;
            }

            try
            {
                foreach (var d in Directory.GetDirectories(currentDir))
                {
                    DiscoverFilesRecursively(d, files, ref scannedCount, cancellationToken);
                }
            }
            catch (Exception subEx)
            {
                _errorDetailsReport.Add($"-> Subfolder Lock: {currentDir} | {subEx.Message}");
            }
        }

        private static bool IsPathExcluded(string fullPath)
        {
            string lower = fullPath.ToLowerInvariant();
            return GlobalExclusions.IsCustomExcluded(lower) || GlobalExclusions.IsDefaultExcluded(fullPath);
        }

        private static string MapToBackupPath(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == '\\')
                return Path.Combine(char.ToUpper(fullPath[0]).ToString(), fullPath[3..]);
            return fullPath;
        }

        private static string FormatBytes(long bytes) => XServerBackupEngine.FormatBytes(bytes);

        private void TxtProgressDetails_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentFilePath))
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentFilePath}\"") { UseShellExecute = true }); } catch { }
            }
        }

        private void AppendLog(string message, Brush color)
        {
            string level = color == DangerBrushColor ? "ERROR" : color == AccentBrightBrush ? "WARN" : "INFO";
            AppLogger.Write(level, "ENGINE", message);

            Dispatcher.Invoke(() =>
            {
                Run run = new($"[{DateTime.Now:HH:mm:ss}] {message}\n") { Foreground = color };
                Paragraph para = new(run) { Margin = new Thickness(0), LineHeight = 16 };
                RtbLog.Document.Blocks.Add(para);
                if (RtbLog.Document.Blocks.Count > 1200) RtbLog.Document.Blocks.Remove(RtbLog.Document.Blocks.FirstBlock);
                LogScrollViewer.ScrollToEnd();
            });
        }
    }
}
