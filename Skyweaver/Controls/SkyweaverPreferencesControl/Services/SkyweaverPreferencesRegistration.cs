using Skyweaver.Controls.SkyweaverPreferencesControl.Models;
using Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages;
using Skyweaver.Controls.SkyweaverPreferencesControl.Views.Pages;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.Services
{
    public static class SkyweaverPreferencesRegistration
    {
        private static readonly object s_syncRoot = new();
        private static bool s_isRegistered;

        public static void EnsureRegistered()
        {
            if (s_isRegistered)
            {
                return;
            }

            lock (s_syncRoot)
            {
                if (s_isRegistered)
                {
                    return;
                }

                var registry = PreferenceRegistry.Instance;

                registry.RegisterPage("workbench-shell", new PreferencePageInfo
                {
                    Id = "preferences-general",
                    DisplayName = "General",
                    Description = "Global entry point for startup flow, confirmations, and basic interaction defaults.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(GeneralPreferencesPageViewModel),
                    Order = 10
                });

                registry.RegisterPage("workbench-shell", new PreferencePageInfo
                {
                    Id = "preferences-appearance",
                    DisplayName = "Appearance",
                    Description = "Theme, density, and visual feedback scaffolding.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(AppearancePreferencesPageViewModel),
                    Order = 20
                });

                registry.RegisterPage("workbench-shell", new PreferencePageInfo
                {
                    Id = "preferences-layout",
                    DisplayName = "Layout",
                    Description = "Arrangement of multifunction tabs, document space, and side panels.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(LayoutPreferencesPageViewModel),
                    Order = 30
                });

                registry.RegisterPage("session-agents", new PreferencePageInfo
                {
                    Id = "preferences-session-defaults",
                    DisplayName = "Session Defaults",
                    Description = "Skeleton defaults for new sessions, resources, and runtime behavior.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(SessionDefaultsPreferencesPageViewModel),
                    Order = 10
                });

                registry.RegisterPage("session-agents", new PreferencePageInfo
                {
                    Id = "preferences-agent-defaults",
                    DisplayName = "Agent Defaults",
                    Description = "Agent persona, tool permissions, and prompt attachment scaffolding.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(AgentDefaultsPreferencesPageViewModel),
                    Order = 20
                });

                registry.RegisterPage("session-agents", new PreferencePageInfo
                {
                    Id = "preferences-model-routing",
                    DisplayName = "Model Routing",
                    Description = "Model catalog defaults, candidate sets, and routing priority.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(ModelRoutingPreferencesPageViewModel),
                    Order = 30
                });

                registry.RegisterPage("system-diagnostics", new PreferencePageInfo
                {
                    Id = "preferences-storage",
                    DisplayName = "Storage and Cache",
                    Description = "Workspace paths, cache policy, and cleanup entry points.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(StoragePreferencesPageViewModel),
                    Order = 10
                });

                registry.RegisterPage("system-diagnostics", new PreferencePageInfo
                {
                    Id = "preferences-diagnostics",
                    DisplayName = "Diagnostics and Labs",
                    Description = "Logs, debug switches, and experimental feature containers.",
                    ViewType = typeof(PreferenceScaffoldPageView),
                    ViewModelType = typeof(DiagnosticsPreferencesPageViewModel),
                    Order = 20
                });

                s_isRegistered = true;
            }
        }
    }
}
