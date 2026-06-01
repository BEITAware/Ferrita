using System;
using System.Threading;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    /// <summary>
    /// 传递当前文档投影会话资源的上下文容器。
    /// </summary>
    public static class DocumentProjectionContext
    {
        private static readonly AsyncLocal<string?> s_currentResourcesPath = new();

        /// <summary>
        /// 获取或设置当前会话的资源文件夹路径。
        /// </summary>
        public static string? CurrentResourcesPath
        {
            get => s_currentResourcesPath.Value;
            set => s_currentResourcesPath.Value = value;
        }

        /// <summary>
        /// 在当前异步流生命周期中建立会话资源路径。
        /// </summary>
        public static IDisposable Establish(string resourcesPath)
        {
            var old = s_currentResourcesPath.Value;
            s_currentResourcesPath.Value = resourcesPath;
            return new ContextScope(old);
        }

        private sealed class ContextScope : IDisposable
        {
            private readonly string? _old;

            public ContextScope(string? old)
            {
                _old = old;
            }

            public void Dispose()
            {
                s_currentResourcesPath.Value = _old;
            }
        }
    }
}
