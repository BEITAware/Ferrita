namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class DiagnosticsPreferencesPageViewModel : PreferenceScaffoldPageViewModel
    {
        public DiagnosticsPreferencesPageViewModel()
            : base(
                "preferences-diagnostics",
                "Diagnostics and Labs",
                "A dedicated home for logs, debug switches, and experimental feature flags.",
                "Only the shell is present today, but the extension points are intentionally grouped like a real diagnostics surface.")
        {
            AddSection(new PreferenceScaffoldSectionViewModel(
                "Logging and Trace",
                "Collects log level, destinations, and retention windows for runtime diagnostics.",
                "Multiple log sources can later converge on this single configuration page.",
                new PreferenceScaffoldItemViewModel("Log level", "Error / info / verbose / trace", "Gives every subsystem the same severity vocabulary."),
                new PreferenceScaffoldItemViewModel("Output target", "Window / file / debug console", "Leaves room for panel-based diagnostics later."),
                new PreferenceScaffoldItemViewModel("Retention window", "Per session / per workspace / rolling days", "Good foundation for persistent logs.")));

            AddSection(new PreferenceScaffoldSectionViewModel(
                "Experimental Features",
                "Provides a stable container for lab flags and future developer-facing surfaces.",
                "New features can be soft-launched here without polluting production pages.",
                new PreferenceScaffoldItemViewModel("Feature flags", "Per item enable / workspace override", "Makes experimentation explicit."),
                new PreferenceScaffoldItemViewModel("Diagnostics entry", "Performance, threads, request traces", "Reserves a bridge to deeper debug panels."),
                new PreferenceScaffoldItemViewModel("Support bundle", "Logs, config, resource index", "Natural placeholder for issue-report tooling.")));
        }
    }
}
