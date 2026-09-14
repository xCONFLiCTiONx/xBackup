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
                    string[] dirs = Directory.GetDirectories(FullPath);
                    Array.Sort(dirs);
                    foreach (var dir in dirs)
                    {
                        var dirName = Path.GetFileName(dir);
                        var child = new FileSystemNode
                        {
                            Name = dirName,
                            FullPath = dir,
                            IsDirectory = true,
                            Parent = this
                        };
                        child.InitializeState();
                        if (HasSubItems(dir))
                        {
                            child.Children.Add(new FileSystemNode { Name = "Loading..." });
                        }
                        Children.Add(child);
                    }

                    string[] files = Directory.GetFiles(FullPath);
                    Array.Sort(files);
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileName(file);
                        var child = new FileSystemNode
                        {
                            Name = fileName,
                            FullPath = file,
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
            if (GlobalExclusions.IsDefaultExcluded(FullPath) || GlobalExclusions.CustomExcludedPaths.Contains(FullPath.ToLower()))
            {
                _isChecked = false;
            }
            else if (Parent != null && Parent.IsChecked == false)
            {
                _isChecked = false;
            }
            else if (Parent != null && Parent.IsChecked == true)
            {
                _isChecked = true;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
