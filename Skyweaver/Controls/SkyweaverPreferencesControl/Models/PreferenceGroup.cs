using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.Models
{
    public sealed class PreferenceGroup : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _displayName = string.Empty;
        private bool _isExpanded = true;

        public string Id
        {
            get => _id;
            set
            {
                if (_id == value)
                {
                    return;
                }

                _id = value;
                OnPropertyChanged();
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName == value)
                {
                    return;
                }

                _displayName = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<PreferencePageInfo> Pages { get; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
