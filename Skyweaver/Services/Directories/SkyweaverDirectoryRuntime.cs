using System.IO;
using Skyweaver.Models.Directories;

namespace Skyweaver.Services.Directories
{
    public sealed class SkyweaverDirectoryRuntime
    {
        private readonly object _syncRoot = new();
        private readonly DirectoriesConfigurationRepository _configurationRepository;
        private DirectoriesConfiguration _configuration;

        private SkyweaverDirectoryRuntime()
        {
            _configurationRepository = new DirectoriesConfigurationRepository();
            _configuration = SkyweaverDirectoryDefaults.NormalizeConfiguration(_configurationRepository.Load());
        }

        public static SkyweaverDirectoryRuntime Instance { get; } = new();

        public string DirectoriesConfigurationFilePath => _configurationRepository.ConfigurationFilePath;

        public string FixedConfigurationDirectoryPath => _configurationRepository.ConfigurationDirectoryPath;

        public string DefaultApplicationDirectoryPath => SkyweaverDirectoryDefaults.ApplicationDirectoryPath;

        public string ChatSessionsDirectoryPath => GetDirectoryPath(
            configuration => configuration.ChatSessionsDirectoryPath,
            SkyweaverDirectoryDefaults.DefaultChatSessionsDirectoryPath);

        public string ConfigurationDirectoryPath => GetDirectoryPath(
            configuration => configuration.ConfigurationDirectoryPath,
            SkyweaverDirectoryDefaults.DefaultConfigurationDirectoryPath);

        public string DebugDirectoryPath => GetDirectoryPath(
            configuration => configuration.DebugDirectoryPath,
            SkyweaverDirectoryDefaults.DefaultDebugDirectoryPath);

        public string SessionFlowsDirectoryPath => GetDirectoryPath(
            configuration => configuration.SessionFlowsDirectoryPath,
            SkyweaverDirectoryDefaults.DefaultSessionFlowsDirectoryPath);

        public string AerialCityDirectoryPath => GetDirectoryPath(
            configuration => configuration.AerialCityDirectoryPath,
            SkyweaverDirectoryDefaults.DefaultAerialCityDirectoryPath);

        public event EventHandler? ConfigurationChanged;

        public DirectoriesConfiguration GetConfiguration()
        {
            lock (_syncRoot)
            {
                return CloneConfiguration(_configuration);
            }
        }

        public void SaveConfiguration(DirectoriesConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            lock (_syncRoot)
            {
                var normalizedConfiguration = SkyweaverDirectoryDefaults.NormalizeConfiguration(configuration);
                EnsureConfiguredDirectories(normalizedConfiguration);

                _configuration = CloneConfiguration(normalizedConfiguration);
                _configurationRepository.Save(_configuration);
            }

            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private string GetDirectoryPath(Func<DirectoriesConfiguration, string> selector, string fallbackDirectoryPath)
        {
            lock (_syncRoot)
            {
                return SkyweaverDirectoryDefaults.NormalizeDirectoryPath(selector(_configuration), fallbackDirectoryPath);
            }
        }

        private static DirectoriesConfiguration CloneConfiguration(DirectoriesConfiguration configuration)
        {
            return new DirectoriesConfiguration
            {
                ChatSessionsDirectoryPath = configuration.ChatSessionsDirectoryPath,
                ConfigurationDirectoryPath = configuration.ConfigurationDirectoryPath,
                DebugDirectoryPath = configuration.DebugDirectoryPath,
                SessionFlowsDirectoryPath = configuration.SessionFlowsDirectoryPath,
                AerialCityDirectoryPath = configuration.AerialCityDirectoryPath
            };
        }

        private static void EnsureConfiguredDirectories(DirectoriesConfiguration configuration)
        {
            Directory.CreateDirectory(configuration.ChatSessionsDirectoryPath);
            Directory.CreateDirectory(configuration.ConfigurationDirectoryPath);
            Directory.CreateDirectory(configuration.DebugDirectoryPath);
            Directory.CreateDirectory(configuration.SessionFlowsDirectoryPath);
            Directory.CreateDirectory(configuration.AerialCityDirectoryPath);
        }
    }
}
