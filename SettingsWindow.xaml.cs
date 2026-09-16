using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace xBackup
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadRootNodes();
        }

        private void LoadRootNodes()
        {
            try
            {
                DriveInfo[] allDrives = DriveInfo.GetDrives();
                foreach (DriveInfo d in allDrives)
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady)
                    {
                        var driveNode = new FileSystemNode
                        {
                            Name = $"Drive {d.Name} ({d.VolumeLabel})",
                            FullPath = d.Name,
                            IsDirectory = true
                        };
                        driveNode.InitializeState();
                        driveNode.Children.Add(new FileSystemNode { Name = "Loading..." });
                        FileTreeView.Items.Add(driveNode);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize drive tree: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var newExclusions = new HashSet<string>();
                var selectedDrives = new HashSet<string>();

                foreach (FileSystemNode rootNode in FileTreeView.Items)
                {
                    if (rootNode.IsChecked != false)
                    {
                        selectedDrives.Add(rootNode.FullPath.ToUpperInvariant());
                    }
                    CollectExclusionsFromNode(rootNode, newExclusions);
                }

                // Update the global list and persist to json config cache
                GlobalExclusions.CustomExcludedPaths.Clear();
                foreach (var path in newExclusions)
                {
                    GlobalExclusions.CustomExcludedPaths.Add(path);
                }

                GlobalExclusions.SelectedDrives.Clear();
                foreach (var drive in selectedDrives)
                {
                    GlobalExclusions.SelectedDrives.Add(drive);
                }

                GlobalExclusions.Save();

                MessageBox.Show("Custom backup exclusions updated and saved successfully.", "Exclusions Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving exclusions: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CollectExclusionsFromNode(FileSystemNode node, HashSet<string> exclusions)
        {
            if (node.IsChecked == false)
            {
                exclusions.Add(node.FullPath.ToLower());
                return;
            }

            foreach (var child in node.Children)
            {
                if (child.Name != "Loading...")
                {
                    CollectExclusionsFromNode(child, exclusions);
                }
            }
        }
    }
}
