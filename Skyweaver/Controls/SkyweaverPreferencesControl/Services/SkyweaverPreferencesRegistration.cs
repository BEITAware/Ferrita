using Skyweaver.Controls.ContextManagementConfigurationControl.ViewModels;
using Skyweaver.Controls.LateralFileSystemConfigurationControl.ViewModels;
using Skyweaver.Controls.PresentationUIConfigurationControl.ViewModels;
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

                registry.RegisterPage("files-system", new PreferencePageInfo
                {
                    Id = "preferences-directories",
                    DisplayName = "目录位置",
                    ViewType = typeof(DirectoryLocationsPreferencesPageView),
                    ViewModelType = typeof(DirectoryLocationsPreferencesPageViewModel),
                    Order = 5
                });

                registry.RegisterPage("files-system", new PreferencePageInfo
                {
                    Id = "preferences-storage",
                    DisplayName = "侧向文件系统配置",
                    ViewType = typeof(LateralFileSystemPreferencesPageView),
                    ViewModelType = typeof(LateralFileSystemConfigurationControlViewModel),
                    Order = 10
                });

                registry.RegisterPage("presentation-ui", new PreferencePageInfo
                {
                    Id = "preferences-chat-session",
                    DisplayName = "聊天会话",
                    ViewType = typeof(ChatSessionPreferencesPageView),
                    ViewModelType = typeof(PresentationUIConfigurationControlViewModel),
                    Order = 10
                });

                registry.RegisterPage("context-management", new PreferencePageInfo
                {
                    Id = "preferences-context-compression",
                    DisplayName = "压缩",
                    ViewType = typeof(ContextCompressionPreferencesPageView),
                    ViewModelType = typeof(ContextCompressionPreferencesPageViewModel),
                    Order = 10
                });

                registry.RegisterPage("context-management", new PreferencePageInfo
                {
                    Id = "preferences-semantic-search",
                    DisplayName = "语义搜索",
                    ViewType = typeof(SemanticSearchPreferencesPageView),
                    ViewModelType = typeof(SemanticSearchPreferencesPageViewModel),
                    Order = 20
                });

                s_isRegistered = true;
            }
        }
    }
}
