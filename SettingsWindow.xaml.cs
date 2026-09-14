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
                // 1. User Profile Directory Root
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (Directory.Exists(userProfile))
                {
                    var userNode = new FileSystemNode
                    {
                        Name = $"User Profile ({Path.GetFileName(userProfile)})",
                        FullPath = userProfile,
                        IsDirectory = true
                    };
                    userNode.InitializeState();
                    userNode.Children.Add(new FileSystemNode { Name = "Loading..." });
                    FileTreeView.Items.Add(userNode);
                }

                // 2. ProgramData Directory Root
                string programData = @"C:\ProgramData";
                if (Directory.Exists(programData))
                {
                    var progNode = new FileSystemNode
                    {
                        Name = "ProgramData (C:\\ProgramData)",
                        FullPath = programData,
                        IsDirectory = true
                    };
                    progNode.InitializeState();
                    progNode.Children.Add(new FileSystemNode { Name = "Loading..." });
                    FileTreeView.Items.Add(progNode);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize file tree structure roots: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                foreach (FileSystemNode rootNode in FileTreeView.Items)
                {
                    CollectExclusionsFromNode(rootNode, newExclusions);
                }

                // Update the global list and persist to json config cache
                GlobalExclusions.CustomExcludedPaths.Clear();
                foreach (var path in newExclusions)
                {
                    GlobalExclusions.CustomExcludedPaths.Add(path);
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
