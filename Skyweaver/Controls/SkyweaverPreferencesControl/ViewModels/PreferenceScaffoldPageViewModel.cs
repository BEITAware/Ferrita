using System.Collections.ObjectModel;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public abstract class PreferenceScaffoldPageViewModel : PreferencePageViewModelBase
    {
        protected PreferenceScaffoldPageViewModel(
            string pageId,
            string title,
            string description,
            string hint)
        {
            PageId = pageId;
            Title = title;
            Description = description;
            Hint = hint;
        }

        public sealed override string PageId { get; }

        public sealed override string Title { get; }

        public sealed override string Description { get; }

        public sealed override string Hint { get; }

        public ObservableCollection<PreferenceScaffoldSectionViewModel> Sections { get; } = new();

        protected void AddSection(PreferenceScaffoldSectionViewModel section)
        {
            Sections.Add(section);
        }
    }
}
