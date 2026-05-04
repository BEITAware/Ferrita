namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class LayoutPreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public LayoutPreferencesPageViewModel()
            : base(
                "preferences-layout",
                "Layout and Panel Routing",
                "A single home for workspace docking, panel visibility, and multifunction tab behavior.",
                "The structure deliberately mirrors the Cascade operations shell so future layout pages can be registered without redesigning the frame.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Workspace Layout",
                "Describes dock layout presets, default splits, and visible panel clusters.",
                "This is the natural bridge to document-workspace layout snapshots later.",
                new PreferenceScaffoldItemViewModel("Preset pack", "Authoring / debug / asset review", "Leaves room for multiple workspace arrangements."),
                new PreferenceScaffoldItemViewModel("Panel visibility", "Sessions, filmstrip, file tree, node settings", "Good match for existing side panels."),
                new PreferenceScaffoldItemViewModel("Restore granularity", "Per project / per user / ad hoc session", "Defines layout persistence scope early.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Multifunction Tabs",
                "Controls how preferences and related tools open inside the multifunction container.",
                "New tools can be added by registration only, with no need to revisit the shell layout.",
                new PreferenceScaffoldItemViewModel("Open behavior", "Single instance / multi instance / recent-first", "Lines up well with current tab definitions."),
                new PreferenceScaffoldItemViewModel("Ordering policy", "Pinned / recent / user-ordered", "Reserves a place for sorting rules."),
                new PreferenceScaffoldItemViewModel("Empty state strategy", "Prompt copy, quick links, recent items", "Lets shell-level empty states evolve independently.")));
        }
    }
}
