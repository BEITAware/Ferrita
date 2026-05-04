namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class GeneralPreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public GeneralPreferencesPageViewModel()
            : base(
                "preferences-general",
                "General Defaults",
                "Application-wide startup, confirmation, and interaction defaults live here as an extensible scaffold.",
                "This page intentionally stops at architecture and layout. Real settings storage and validation can be connected later without changing the shell.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Startup and Restore",
                "Organizes how the app boots into a workspace and how desktop state is restored.",
                "This section is ready to map to startup preferences and recent-workspace restore logic.",
                new PreferenceScaffoldItemViewModel("Startup target", "Welcome page / last workspace / fixed panel", "Keeps enum-like defaults isolated and easy to persist later."),
                new PreferenceScaffoldItemViewModel("Window restore", "Position, size, dock layout", "Reserves a single home for desktop layout recovery rules."),
                new PreferenceScaffoldItemViewModel("First-run guidance", "Hero cards, sample project, onboarding prompts", "Provides a stable hook for progressive onboarding.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Confirmations and Notices",
                "Central place for dangerous-action prompts and lightweight runtime feedback.",
                "Future work can route tool-confirmation policy and global notifications through this layer.",
                new PreferenceScaffoldItemViewModel("Risk prompts", "Delete, overwrite, batch-run confirmations", "Useful for filesystem tools and workflow execution."),
                new PreferenceScaffoldItemViewModel("Completion notice", "Status bar, toast, dialog", "Leaves room for consistent notification levels."),
                new PreferenceScaffoldItemViewModel("Apply cadence", "Instant save / deferred apply / manual apply", "Defines how settings pages should feel before persistence is wired in.")));
        }
    }
}
