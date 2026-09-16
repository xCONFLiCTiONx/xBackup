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
            // If children are not loaded yet or the folder is empty, don't attempt to calculate state from them.
            // This prevents folders with Access Denied or empty directories from flipping to Indeterminate/Unchecked.
            if (Children.Count == 0 || (Children.Count == 1 && Children[0].Name == "Loading...")) return;

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

            if (!first)
            {
                SetIsChecked(state, false, true);
            }
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
                bool isSelected = GlobalExclusions.SelectedDrives.Contains(FullPath.ToUpperInvariant());
                if (isSelected && GlobalExclusions.HasCustomExcludedChildren(FullPath))
                {
                    _isChecked = null;
                }
                else
                {
                    _isChecked = isSelected;
                }
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
                if (GlobalExclusions.HasCustomExcludedChildren(FullPath))
                {
                    _isChecked = null;
                }
                else
                {
                    _isChecked = true;
                }
                return;
            }

            // 5. Otherwise inherit from parent
            if (Parent != null)
            {
                // Fix: If the parent is Indeterminate (null), it means some parts of the parent are excluded.
                // However, a newly loaded child should default to 'true' (Included) unless it's explicitly in the exclusion list.
                // This prevents the 'Indeterminate' state of the Drive Root (caused by Windows/Program Files exclusions)
                // from cascading down to every other folder on the drive.
                bool? parentState = Parent.IsChecked ?? true;

                if (parentState == true && GlobalExclusions.HasCustomExcludedChildren(FullPath))
                {
                    _isChecked = null;
                }
                else
                {
                    _isChecked = parentState;
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
