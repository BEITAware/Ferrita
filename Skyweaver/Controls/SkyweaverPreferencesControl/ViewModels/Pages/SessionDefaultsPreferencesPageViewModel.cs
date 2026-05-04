namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class SessionDefaultsPreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public SessionDefaultsPreferencesPageViewModel()
            : base(
                "preferences-session-defaults",
                "Session Defaults",
                "Skeleton defaults for new sessions, attached resources, and runtime behavior start here.",
                "This is scaffold-only today, but it already separates creation defaults from execution policy.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "New Session Skeleton",
                "Organizes template choice, default resources, and startup attachments.",
                "A future create-session dialog can read from this section directly.",
                new PreferenceScaffoldItemViewModel("Default template", "Blank / agent-driven / workflow-driven", "Keeps session bootstrapping consistent."),
                new PreferenceScaffoldItemViewModel("Initial attachments", "System prompt, project context, working folder", "Reserves a home for default resources."),
                new PreferenceScaffoldItemViewModel("Creation behavior", "Create folder / load recent / attach starter assets", "Maps naturally to session setup flows.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Runtime Policy",
                "Collects save cadence, confirmation defaults, and context maintenance rules.",
                "Later wiring can connect these placeholders to runtime strategy objects.",
                new PreferenceScaffoldItemViewModel("Auto-save cadence", "Per turn / idle window / manual", "Lets persistence have a clear global default."),
                new PreferenceScaffoldItemViewModel("Confirmation level", "Always ask / risky only / auto-allow", "Matches the tool-confirmation service."),
                new PreferenceScaffoldItemViewModel("Context upkeep", "Compress / archive / summarize", "Provides a clear route into context-management logic.")));
        }
    }
}
