using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public abstract class PreferencePageViewModelBase : ObservableObject
    {
        public abstract string PageId { get; }

        public abstract string Title { get; }

        public abstract string Description { get; }

        public abstract string Hint { get; }
    }
}
