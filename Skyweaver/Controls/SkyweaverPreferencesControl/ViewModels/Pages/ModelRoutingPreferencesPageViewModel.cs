namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class ModelRoutingPreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public ModelRoutingPreferencesPageViewModel()
            : base(
                "preferences-model-routing",
                "Model Routing",
                "Central place for default models, capability-layer bindings, and fallback routing policy.",
                "The page exists so model-selection rules stop living only inside local editors and can grow into an app-level system.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Default Candidates",
                "Defines what new agents, new sessions, and background tasks pick when no model is specified.",
                "This turns default model selection into a first-class registered page instead of scattered local state.",
                new PreferenceScaffoldItemViewModel("Agent defaults", "Primary chat / compression / vision", "Supports multiple default roles cleanly."),
                new PreferenceScaffoldItemViewModel("Capability mapping", "Reasoning / retrieval / structured output", "Good fit for future capability-layer binding."),
                new PreferenceScaffoldItemViewModel("Fallback rule", "Same interface / same layer / block startup", "Reserves one consistent downgrade policy.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Catalog Visibility",
                "Controls grouping, visibility, and ownership for model catalogs.",
                "If several model sources show up later, the shell does not need to change.",
                new PreferenceScaffoldItemViewModel("Grouping mode", "Interface / vendor / capability layer", "Defines how lists are organized."),
                new PreferenceScaffoldItemViewModel("Sharing scope", "Global / workspace / session", "Leaves room for workspace-specific catalogs."),
                new PreferenceScaffoldItemViewModel("Testing hooks", "Connectivity, rate limits, diagnostic response", "Natural home for future model health tools.")));
        }
    }
}
