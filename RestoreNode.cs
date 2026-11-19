using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using xBackup.Models;

namespace xBackup
{
    public class RestoreNode : INotifyPropertyChanged
    {
        private bool? _isChecked = false;
        private bool _isExpanded = false;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public FileVersion? Version { get; set; }
        public ObservableCollection<RestoreNode> Children { get; } = new ObservableCollection<RestoreNode>();
        public RestoreNode? Parent { get; set; }

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
                }
            }
        }

        public Brush ForegroundBrush => IsDirectory ? new SolidColorBrush(Color.FromRgb(220, 202, 170)) : new SolidColorBrush(Color.FromRgb(212, 212, 212));

        public void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (updateChildren && _isChecked.HasValue)
            {
                foreach (var child in Children)
                {
                    child.SetIsChecked(_isChecked, true, false);
                }
            }

            if (updateParent && Parent != null)
            {
                Parent.VerifyCheckState();
            }
        }

        public void VerifyCheckState()
        {
            if (Children.Count == 0) return;

            bool? state = null;
            bool first = true;
            foreach (var child in Children)
            {
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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
