using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using Skyweaver.Commands;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.Directories;
using Skyweaver.Services.Directories;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class DirectoryLocationsPreferencesPageViewModel : ObservableObject
    {
        private readonly SkyweaverDirectoryRuntime _runtime;
        private DirectoriesConfiguration _configuration;
        private string _statusMessage = "目录配置已加载。";

        public DirectoryLocationsPreferencesPageViewModel()
        {
            _runtime = SkyweaverDirectoryRuntime.Instance;
            _configuration = _runtime.GetConfiguration();

            BrowseChatSessionsDirectoryCommand = new RelayCommand(
                () => BrowseDirectory("选择聊天会话保存目录", ChatSessionsDirectoryPath, path => ChatSessionsDirectoryPath = path));
            BrowseConfigurationDirectoryCommand = new RelayCommand(
                () => BrowseDirectory("选择配置文件保存目录", ConfigurationFilesDirectoryPath, path => ConfigurationFilesDirectoryPath = path));
            BrowseDebugDirectoryCommand = new RelayCommand(
                () => BrowseDirectory("选择调试文件保存目录", DebugDirectoryPath, path => DebugDirectoryPath = path));
            BrowseSessionFlowsDirectoryCommand = new RelayCommand(
                () => BrowseDirectory("选择会话流保存目录", SessionFlowsDirectoryPath, path => SessionFlowsDirectoryPath = path));
            BrowseAerialCityDirectoryCommand = new RelayCommand(
                () => BrowseDirectory("选择 AerialCity 目录", AerialCityDirectoryPath, path => AerialCityDirectoryPath = path));
            OpenDirectoriesConfigurationDirectoryCommand = new RelayCommand(OpenDirectoriesConfigurationDirectory);
        }

        public string Title { get; } = "目录位置";

        public string Description { get; } = "配置 Skyweaver 的会话、配置、调试和会话流文件保存位置。";

        public string Hint { get; } = "目录设置会立即写入默认 Configuration 目录中的 Directories.xml。";

        public string DirectoriesConfigurationFilePath => _runtime.DirectoriesConfigurationFilePath;

        public string FixedConfigurationDirectoryPath => _runtime.FixedConfigurationDirectoryPath;

        public string DefaultApplicationDirectoryPath => _runtime.DefaultApplicationDirectoryPath;

        public string ChatSessionsDirectoryPath
        {
            get => _configuration.ChatSessionsDirectoryPath;
            set => SetDirectoryPath(
                value,
                _configuration.ChatSessionsDirectoryPath,
                path => _configuration.ChatSessionsDirectoryPath = path,
                "聊天会话目录已保存。");
        }

        public string ConfigurationFilesDirectoryPath
        {
            get => _configuration.ConfigurationDirectoryPath;
            set => SetDirectoryPath(
                value,
                _configuration.ConfigurationDirectoryPath,
                path => _configuration.ConfigurationDirectoryPath = path,
                "配置文件目录已保存。");
        }

        public string DebugDirectoryPath
        {
            get => _configuration.DebugDirectoryPath;
            set => SetDirectoryPath(
                value,
                _configuration.DebugDirectoryPath,
                path => _configuration.DebugDirectoryPath = path,
                "调试文件目录已保存。");
        }

        public string SessionFlowsDirectoryPath
        {
            get => _configuration.SessionFlowsDirectoryPath;
            set => SetDirectoryPath(
                value,
                _configuration.SessionFlowsDirectoryPath,
                path => _configuration.SessionFlowsDirectoryPath = path,
                "会话流目录已保存。");
        }

        public string AerialCityDirectoryPath
        {
            get => _configuration.AerialCityDirectoryPath;
            set => SetDirectoryPath(
                value,
                _configuration.AerialCityDirectoryPath,
                path => _configuration.AerialCityDirectoryPath = path,
                "AerialCity 目录已保存。");
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public ICommand BrowseChatSessionsDirectoryCommand { get; }

        public ICommand BrowseConfigurationDirectoryCommand { get; }

        public ICommand BrowseDebugDirectoryCommand { get; }

        public ICommand BrowseSessionFlowsDirectoryCommand { get; }

        public ICommand BrowseAerialCityDirectoryCommand { get; }

        public ICommand OpenDirectoriesConfigurationDirectoryCommand { get; }

        private void SetDirectoryPath(
            string? value,
            string currentValue,
            Action<string> applyValue,
            string successMessage,
            [CallerMemberName] string? propertyName = null)
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            applyValue(normalizedValue);
            OnPropertyChanged(propertyName);
            PersistConfiguration(successMessage);
        }

        private void BrowseDirectory(string description, string currentPath, Action<string> applySelectedPath)
        {
            try
            {
                var selectedPath = Directory.Exists(currentPath)
                    ? currentPath
                    : DefaultApplicationDirectoryPath;

                using var dialog = new Forms.FolderBrowserDialog
                {
                    Description = description,
                    SelectedPath = selectedPath,
                    ShowNewFolderButton = true
                };

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    applySelectedPath(dialog.SelectedPath);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"选择目录失败：{ex.Message}";
            }
        }

        private void OpenDirectoriesConfigurationDirectory()
        {
            try
            {
                var directoryPath = Path.GetDirectoryName(DirectoriesConfigurationFilePath) ?? FixedConfigurationDirectoryPath;
                Directory.CreateDirectory(directoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = directoryPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开目录失败：{ex.Message}";
            }
        }

        private void PersistConfiguration(string successMessage)
        {
            try
            {
                _runtime.SaveConfiguration(_configuration);
                _configuration = _runtime.GetConfiguration();
                RefreshDirectoryProperties();
                StatusMessage = successMessage;
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存失败：{ex.Message}";
            }
        }

        private void RefreshDirectoryProperties()
        {
            OnPropertyChanged(nameof(ChatSessionsDirectoryPath));
            OnPropertyChanged(nameof(ConfigurationFilesDirectoryPath));
            OnPropertyChanged(nameof(DebugDirectoryPath));
            OnPropertyChanged(nameof(SessionFlowsDirectoryPath));
            OnPropertyChanged(nameof(AerialCityDirectoryPath));
            OnPropertyChanged(nameof(DirectoriesConfigurationFilePath));
            OnPropertyChanged(nameof(FixedConfigurationDirectoryPath));
            OnPropertyChanged(nameof(DefaultApplicationDirectoryPath));
        }
    }
}
