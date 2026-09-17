using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using xBackup.Models;

namespace xBackup
{
    public partial class RestoreWindow : Window
    {
        private readonly BackupCatalog _catalog;
        private readonly string _mountedDrive;
        public List<RestoreNode> SelectedNodes { get; private set; } = new();
        public int SelectedSnapshotId { get; private set; }
        public string? TargetPath { get; private set; }
        public bool Overwrite { get; private set; }
        public bool FullRestore { get; private set; }

        public RestoreWindow(BackupCatalog catalog, string mountedDrive)
        {
            InitializeComponent();
            _catalog = catalog;
            _mountedDrive = mountedDrive;
            LoadSnapshots();
        }

        private void LoadSnapshots()
        {
            var snapshots = _catalog.GetSnapshots();
            CboSnapshots.ItemsSource = snapshots;
            if (snapshots.Count > 0)
            {
                CboSnapshots.SelectedIndex = 0;
            }
        }

        private async void CboSnapshots_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboSnapshots.SelectedValue is int snapId)
            {
                SelectedSnapshotId = snapId;
                await LoadFileTree(snapId);
            }
        }

        private async Task LoadFileTree(int snapshotId)
        {
            try
            {
                PrgLoading.Visibility = Visibility.Visible;
                TxtNoData.Visibility = Visibility.Collapsed;
                RestoreTreeView.ItemsSource = null;

                var files = await Task.Run(() => _catalog.GetFilesAtSnapshot(snapshotId));

                var roots = await Task.Run(() => BuildTree(files));
                RestoreTreeView.ItemsSource = roots;

                if (roots.Count == 0)
                {
                    TxtNoData.Text = "No files found in this snapshot.";
                    TxtNoData.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load file tree: {ex.Message}\n\nPlease try selecting the snapshot again or wait a few seconds for the drive to stabilize.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtNoData.Text = "Error loading files. Please retry.";
                TxtNoData.Visibility = Visibility.Visible;
            }
            finally
            {
                PrgLoading.Visibility = Visibility.Collapsed;
            }
        }

        private List<RestoreNode> BuildTree(List<(BackupFile file, FileVersion version)> files)
        {
            var rootNodes = new Dictionary<string, RestoreNode>(StringComparer.OrdinalIgnoreCase);
            var folderLookup = new Dictionary<string, RestoreNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in files)
            {
                string path = item.file.SourcePath;
                string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (parts.Length == 0) continue;

                string currentPath = "";
                RestoreNode? parent = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (i == 0 && part.EndsWith(":")) part += "\\"; // Drive root normalization

                    currentPath = string.IsNullOrEmpty(currentPath) ? part : Path.Combine(currentPath, part);

                    bool isFile = (i == parts.Length - 1);

                    if (!folderLookup.TryGetValue(currentPath, out var node))
                    {
                        node = new RestoreNode
                        {
                            Name = part,
                            FullPath = currentPath,
                            IsDirectory = !isFile,
                            Parent = parent,
                            Version = isFile ? item.version : null
                        };

                        if (parent == null)
                        {
                            rootNodes[currentPath] = node;
                        }
                        else
                        {
                            parent.Children.Add(node);
                        }
                        folderLookup[currentPath] = node;
                    }
                    else if (isFile)
                    {
                        // Should not happen with UNIQUE NormalizedPath but safety first
                        node.Version = item.version;
                    }

                    parent = node;
                }
            }

            return rootNodes.Values.OrderBy(n => n.Name).ToList();
        }

        private void RbRestoreMode_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtDestLabel == null) return;

            bool isSelective = RbSelectiveRestore.IsChecked == true;
            TxtDestLabel.Visibility = isSelective ? Visibility.Visible : Visibility.Collapsed;
            GridDestPicker.Visibility = isSelective ? Visibility.Visible : Visibility.Collapsed;
            TxtFullRestoreInfo.Visibility = isSelective ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnBrowseDest_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Restore Destination"
            };
            if (dialog.ShowDialog() == true)
            {
                TxtDestPath.Text = dialog.FolderName;
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (RestoreTreeView.ItemsSource is IEnumerable<RestoreNode> roots)
            {
                foreach (var root in roots) root.IsChecked = true;
            }
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (RestoreTreeView.ItemsSource is IEnumerable<RestoreNode> roots)
            {
                foreach (var root in roots) root.IsChecked = false;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (RbSelectiveRestore.IsChecked == true && string.IsNullOrWhiteSpace(TxtDestPath.Text))
            {
                MessageBox.Show("Please select a destination folder for selective restore.", "Destination Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            FullRestore = RbFullRestore.IsChecked == true;
            TargetPath = TxtDestPath.Text;
            Overwrite = ChkOverwrite.IsChecked ?? false;

            if (FullRestore)
            {
                var result = MessageBox.Show(
                    "You are about to restore files to their ORIGINAL locations. Existing files may be overwritten.\n\nContinue?",
                    "Confirm Full Restore",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );
                if (result != MessageBoxResult.Yes) return;
            }

            // Collect selected files
            SelectedNodes.Clear();
            if (RestoreTreeView.ItemsSource is IEnumerable<RestoreNode> roots)
            {
                foreach (var root in roots)
                {
                    CollectSelectedFiles(root, SelectedNodes);
                }
            }

            if (SelectedNodes.Count == 0)
            {
                MessageBox.Show("No files selected for restore.", "Selection Empty", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CollectSelectedFiles(RestoreNode node, List<RestoreNode> selected)
        {
            if (node.IsChecked == false) return;

            if (!node.IsDirectory)
            {
                if (node.IsChecked == true)
                {
                    selected.Add(node);
                }
            }
            else
            {
                foreach (var child in node.Children)
                {
                    CollectSelectedFiles(child, selected);
                }
            }
        }
    }
}
