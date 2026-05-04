using System.Collections.ObjectModel;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public sealed class PreferenceScaffoldSectionViewModel
    {
        public PreferenceScaffoldSectionViewModel(
            string title,
            string description,
            string footerHint,
            params PreferenceScaffoldItemViewModel[] items)
        {
            Title = title;
            Description = description;
            FooterHint = footerHint;
            Items = new ObservableCollection<PreferenceScaffoldItemViewModel>(items);
        }

        public string Title { get; }

        public string Description { get; }

        public string FooterHint { get; }

        public ObservableCollection<PreferenceScaffoldItemViewModel> Items { get; }
    }
}
