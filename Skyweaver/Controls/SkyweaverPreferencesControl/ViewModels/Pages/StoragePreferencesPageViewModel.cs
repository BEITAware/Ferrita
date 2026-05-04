namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class StoragePreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public StoragePreferencesPageViewModel()
            : base(
                "preferences-storage",
                "Storage and Cache",
                "One shell for workspace paths, export roots, temporary files, and cache policy.",
                "The page is split by path ownership and lifecycle policy so concrete services can bind in later.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Workspace Paths",
                "Defines root paths for projects, exports, and temporary work files.",
                "This can later share configuration with filesystem tooling and resource browsers.",
                new PreferenceScaffoldItemViewModel("Workspace root", "Default project location", "Provides one consistent path source."),
                new PreferenceScaffoldItemViewModel("Export target", "Inherit project / fixed path / recent path", "Useful for future export features."),
                new PreferenceScaffoldItemViewModel("Temporary folder", "Cache, preview, intermediate output", "Natural fit for background-task scratch space.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Cache Lifecycle",
                "Collects retention windows, cleanup scope, and manual maintenance entry points.",
                "When concrete cache services arrive, they can bind to this page without changing shell structure.",
                new PreferenceScaffoldItemViewModel("Retention policy", "By time / by size / manual cleanup", "Shared model for different cache families."),
                new PreferenceScaffoldItemViewModel("Cache scope", "Thumbnails / session assets / temporary output", "Makes later classification straightforward."),
                new PreferenceScaffoldItemViewModel("Cleanup entry", "Immediate / scheduled / startup check", "Reserves one place for maintenance actions.")));
        }
    }
}
