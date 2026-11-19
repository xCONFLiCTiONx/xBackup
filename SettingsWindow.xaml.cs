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
            TxtRetentionDays.Text = GlobalExclusions.RetentionDays.ToString();
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
                int rentDays = 90;
                if (!int.TryParse(TxtRetentionDays.Text, out rentDays) || rentDays <= 0)
                {
                    MessageBox.Show("Please enter a valid number of days for history retention (minimum 1).", "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                GlobalExclusions.UpdateSettings(newExclusions, selectedDrives, rentDays);

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

            // Handle Indeterminate state for unloaded nodes to prevent wiping out deep exclusions
            if (node.IsChecked == null && node.Children.Count == 1 && node.Children[0].Name == "Loading...")
            {
                var existingExclusions = GlobalExclusions.GetExclusionsUnderPath(node.FullPath);
                foreach (var excl in existingExclusions)
                {
                    exclusions.Add(excl.ToLower());
                }
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
