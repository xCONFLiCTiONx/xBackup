using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace xBackup
{
    public class FileSystemNode : INotifyPropertyChanged
    {
        private bool? _isChecked = true;
        private bool _isExpanded = false;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public ObservableCollection<FileSystemNode> Children { get; set; } = new ObservableCollection<FileSystemNode>();
        public FileSystemNode? Parent { get; set; }

        public bool? IsChecked
        {
            get => _isChecked;
            set => SetIsChecked(value, true, true);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                    if (_isExpanded && IsDirectory)
                    {
                        LoadChildren();
                    }
                }
            }
        }

        public Brush ForegroundBrush => IsDirectory ? new SolidColorBrush(Color.FromRgb(220, 202, 170)) : new SolidColorBrush(Color.FromRgb(212, 212, 212));

        public void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (updateChildren && _isChecked.HasValue && IsDirectory)
            {
                foreach (var child in Children)
                {
                    if (child.Name != "Loading...")
                    {
                        child.SetIsChecked(_isChecked, true, false);
                    }
                }
            }

            if (updateParent && Parent != null)
            {
                Parent.VerifyCheckState();
            }
        }

        public void VerifyCheckState()
        {
            bool? state = null;
            bool first = true;
            foreach (var child in Children)
            {
                if (child.Name == "Loading...") continue;
                if (first)
                {
                    state = child.IsChecked;
                    first = false;
                }
                else if (state != child.IsChecked)
                {
                    state = null;
                    break;
                }
            }
            SetIsChecked(state, false, true);
        }

        public void LoadChildren()
        {
            if (!IsDirectory) return;
            if (Children.Count == 1 && Children[0].Name == "Loading...")
            {
                Children.Clear();
                try
                {
                    DirectoryInfo di = new DirectoryInfo(FullPath);

                    // Load Directories
                    foreach (var dirInfo in di.GetDirectories())
                    {
                        // Skip junctions/reparse points to avoid infinite recursion loops (e.g., "Application Data" loops)
                        // EXCEPTION: Allow "All Users" as requested by user, even though it's a junction
                        bool isAllUsers = dirInfo.FullName.Equals(@"C:\Users\All Users", StringComparison.OrdinalIgnoreCase);

                        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && !isAllUsers) continue;

                        // Skip default excluded junk folders entirely from the view to keep the tree clean
                        if (GlobalExclusions.IsDefaultExcluded(dirInfo.FullName)) continue;

                        var child = new FileSystemNode
                        {
                            Name = dirInfo.Name,
                            FullPath = dirInfo.FullName,
                            IsDirectory = true,
                            Parent = this
                        };
                        child.InitializeState();
                        if (HasSubItems(dirInfo.FullName))
                        {
                            child.Children.Add(new FileSystemNode { Name = "Loading..." });
                        }
                        Children.Add(child);
                    }

                    // Load Files
                    foreach (var fileInfo in di.GetFiles())
                    {
                        // Skip default excluded files
                        if (GlobalExclusions.IsDefaultExcluded(fileInfo.FullName)) continue;

                        var child = new FileSystemNode
                        {
                            Name = fileInfo.Name,
                            FullPath = fileInfo.FullName,
                            IsDirectory = false,
                            Parent = this
                        };
                        child.InitializeState();
                        Children.Add(child);
                    }
                }
                catch { }

                VerifyCheckState();
            }
        }

        private bool HasSubItems(string path)
        {
            try
            {
                using (var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    return enumerator.MoveNext();
                }
            }
            catch { return false; }
        }

        public void InitializeState()
        {
            string lower = FullPath.ToLowerInvariant();

            // 1. Check if explicitly custom excluded
            if (GlobalExclusions.CustomExcludedPaths.Contains(lower))
            {
                _isChecked = false;
                return;
            }

            // 2. Drive Roots are checked if in SelectedDrives
            if (FullPath.Length <= 3 && FullPath.Contains(":\\"))
            {
                _isChecked = GlobalExclusions.SelectedDrives.Contains(FullPath.ToUpperInvariant());
                return;
            }

            // 3. Default exclusions (Windows, Program Files, etc.)
            if (GlobalExclusions.IsDefaultExcluded(FullPath))
            {
                _isChecked = false;
                return;
            }

            // 4. Special cases for C: drive default inclusions
            if (lower.StartsWith("c:\\users") || lower.Equals("c:\\programdata"))
            {
                _isChecked = true;
                return;
            }

            // 5. Otherwise inherit from parent
            if (Parent != null)
            {
                _isChecked = Parent.IsChecked;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
