using System.Collections.ObjectModel;
using System.Linq;
using Skyweaver.Controls.SkyweaverPreferencesControl.Models;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public sealed class PreferenceGroupViewModel : ObservableObject
    {
        private bool _isExpanded;

        public PreferenceGroupViewModel(PreferenceGroup group)
        {
            Group = group;
            _isExpanded = group.IsExpanded;

            Pages = new ObservableCollection<SelectablePreferencePageViewModel>(
                group.Pages.Select(page => new SelectablePreferencePageViewModel(page)));

            group.Pages.CollectionChanged += (_, args) =>
            {
                if (args.NewItems != null)
                {
                    foreach (PreferencePageInfo page in args.NewItems)
                    {
                        Pages.Add(new SelectablePreferencePageViewModel(page));
                    }
                }

                if (args.OldItems == null)
                {
                    return;
                }

                foreach (PreferencePageInfo page in args.OldItems)
                {
                    var existing = Pages.FirstOrDefault(candidate => ReferenceEquals(candidate.PageInfo, page));
                    if (existing != null)
                    {
                        Pages.Remove(existing);
                    }
                }
            };
        }

        public PreferenceGroup Group { get; }

        public string Id => Group.Id;

        public string DisplayName => Group.DisplayName;

        public ObservableCollection<SelectablePreferencePageViewModel> Pages { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!SetProperty(ref _isExpanded, value))
                {
                    return;
                }

                Group.IsExpanded = value;
            }
        }
    }
}
