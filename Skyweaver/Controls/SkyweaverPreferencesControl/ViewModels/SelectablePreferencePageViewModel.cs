using Skyweaver.Controls.SkyweaverPreferencesControl.Models;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public sealed class SelectablePreferencePageViewModel : ObservableObject
    {
        private bool _isSelected;

        public SelectablePreferencePageViewModel(PreferencePageInfo pageInfo)
        {
            PageInfo = pageInfo;
        }

        public PreferencePageInfo PageInfo { get; }

        public string Id => PageInfo.Id;

        public string DisplayName => PageInfo.DisplayName;

        public string Description => PageInfo.Description;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
