using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.PresentationUI;
using Skyweaver.Services.PresentationUI;

namespace Skyweaver.Controls.PresentationUIConfigurationControl.ViewModels
{
    public sealed class PresentationUIConfigurationControlViewModel : ObservableObject
    {
        private readonly PresentationUIRuntime _runtime;
        private readonly PresentationUIConfiguration _configuration;
        private string _statusMessage = "配置已加载。";

        public PresentationUIConfigurationControlViewModel()
        {
            _runtime = PresentationUIRuntime.Instance;
            _configuration = _runtime.GetConfiguration();

            OpenConfigurationDirectoryCommand = new RelayCommand(OpenConfigurationDirectory);
        }

        public string Title { get; } = "聊天会话";

        public string Description { get; } = "配置聊天会话中的思维链呈现方式。";

        public string Hint { get; } = "修改后会立即写入 PresentationUI.xml。";

        public string ConfigurationFilePath => _runtime.ConfigurationFilePath;

        public bool CollapseReasoningByDefault
        {
            get => _configuration.CollapseReasoningByDefault;
            set
            {
                if (_configuration.CollapseReasoningByDefault == value)
                {
                    return;
                }

                _configuration.CollapseReasoningByDefault = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultReasoningBehaviorText));
                PersistConfiguration("聊天会话呈现设置已保存。");
            }
        }

        public string DefaultReasoningBehaviorText => CollapseReasoningByDefault
            ? "新的可折叠思维链默认收起。"
            : "新的可折叠思维链默认展开。";

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public ICommand OpenConfigurationDirectoryCommand { get; }

        private void OpenConfigurationDirectory()
        {
            try
            {
                var directoryPath = Path.GetDirectoryName(ConfigurationFilePath) ?? string.Empty;
                if (directoryPath.Length == 0)
                {
                    StatusMessage = "无法定位配置目录。";
                    return;
                }

                Directory.CreateDirectory(directoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = directoryPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开配置目录失败：{ex.Message}";
            }
        }

        private void PersistConfiguration(string successMessage)
        {
            try
            {
                _runtime.SaveConfiguration(_configuration);
                StatusMessage = successMessage;
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存失败：{ex.Message}";
            }
        }
    }
}
