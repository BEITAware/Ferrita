namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class AgentDefaultsPreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public AgentDefaultsPreferencesPageViewModel()
            : base(
                "preferences-agent-defaults",
                "Agent Defaults",
                "A global layer for persona, system prompt fragments, permission defaults, and execution posture.",
                "This mirrors the separation benefits of Cascade: the shell stays stable while page internals evolve independently.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Persona and Prompting",
                "Collects base personas, shared prompt fragments, and inheritance rules.",
                "If prompt inheritance becomes layered later, this page already provides a clean baseline tier.",
                new PreferenceScaffoldItemViewModel("Persona preset", "General executor / collaborator / reviewer", "Reserves a source for new-agent defaults."),
                new PreferenceScaffoldItemViewModel("Shared prompt fragments", "Project rules, output format, collaboration tone", "Ideal for team-wide prompt policies."),
                new PreferenceScaffoldItemViewModel("Inheritance mode", "Full inherit / selective override / detach", "Future-facing hook for prompt composition.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Tools and Permissions",
                "Defines default tool packs, filesystem posture, and execution affordances.",
                "Existing permission models can plug in here later without reworking the navigation shell.",
                new PreferenceScaffoldItemViewModel("Default tool pack", "Built-ins / workspace tools / extensions", "Sets a global capability floor for new agents."),
                new PreferenceScaffoldItemViewModel("Permission posture", "Sandbox first / confirm risky / inherited allowlist", "Matches current tool-evaluation concepts."),
                new PreferenceScaffoldItemViewModel("Execution stance", "Allow delegation / batch edit / assisted mode", "Reserves switches for higher-level behavior.")));
        }
    }
}
