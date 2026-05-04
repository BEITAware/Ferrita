namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class AppearancePreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public AppearancePreferencesPageViewModel()
            : base(
                "preferences-appearance",
                "Appearance System",
                "Theme families, density, accent lighting, and motion live in a single visual settings scaffold.",
                "Nothing here changes runtime themes yet; the page exists to mirror the Cascade-style structure and visual language.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Theme Layer",
                "Holds theme family, material intensity, and core palette decisions.",
                "Each item can later bind directly to resource dictionaries or theme configuration objects.",
                new PreferenceScaffoldItemViewModel("Theme family", "Aero / dark glass / high contrast", "Keeps shell-level theme choice outside individual pages."),
                new PreferenceScaffoldItemViewModel("Accent system", "Highlights, button rims, selection glow", "Lets the sidebar and content area share the same visual source."),
                new PreferenceScaffoldItemViewModel("Material intensity", "Blur, border brightness, top highlight", "Preserves the current glass-first direction.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Density and Motion",
                "Controls type scale, list spacing, and page transition energy.",
                "If touch or compact modes arrive later, they can slot into this section without changing the panel shell.",
                new PreferenceScaffoldItemViewModel("Density profile", "Compact / standard / relaxed", "Intended to drive row height and spacing."),
                new PreferenceScaffoldItemViewModel("Typography bands", "Headline / body / helper copy", "Keeps text hierarchy configurable."),
                new PreferenceScaffoldItemViewModel("Motion profile", "Hover, selection, page-switch feedback", "Matches the Cascade-inspired navigation feel.")));
        }
    }
}
