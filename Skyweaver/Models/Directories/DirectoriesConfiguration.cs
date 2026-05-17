using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Models.Directories
{
    public sealed class DirectoriesConfiguration : ObservableObject
    {
        private string _chatSessionsDirectoryPath = string.Empty;
        private string _configurationDirectoryPath = string.Empty;
        private string _debugDirectoryPath = string.Empty;
        private string _sessionFlowsDirectoryPath = string.Empty;
        private string _aerialCityDirectoryPath = string.Empty;

        public string ChatSessionsDirectoryPath
        {
            get => _chatSessionsDirectoryPath;
            set => SetProperty(ref _chatSessionsDirectoryPath, value?.Trim() ?? string.Empty);
        }

        public string ConfigurationDirectoryPath
        {
            get => _configurationDirectoryPath;
            set => SetProperty(ref _configurationDirectoryPath, value?.Trim() ?? string.Empty);
        }

        public string DebugDirectoryPath
        {
            get => _debugDirectoryPath;
            set => SetProperty(ref _debugDirectoryPath, value?.Trim() ?? string.Empty);
        }

        public string SessionFlowsDirectoryPath
        {
            get => _sessionFlowsDirectoryPath;
            set => SetProperty(ref _sessionFlowsDirectoryPath, value?.Trim() ?? string.Empty);
        }

        public string AerialCityDirectoryPath
        {
            get => _aerialCityDirectoryPath;
            set => SetProperty(ref _aerialCityDirectoryPath, value?.Trim() ?? string.Empty);
        }
    }
}
