using Skyweaver.Models.PresentationUI;

namespace Skyweaver.Services.PresentationUI
{
    public sealed class PresentationUIRuntime
    {
        private readonly object _syncRoot = new();
        private readonly PresentationUIConfigurationRepository _configurationRepository;
        private PresentationUIConfiguration _configuration;

        private PresentationUIRuntime()
        {
            var pathProvider = new PresentationUIPathProvider();
            _configurationRepository = new PresentationUIConfigurationRepository(pathProvider);
            _configuration = CloneConfiguration(_configurationRepository.Load());
        }

        public static PresentationUIRuntime Instance { get; } = new();

        public string ConfigurationFilePath => _configurationRepository.ConfigurationFilePath;

        public bool CollapseReasoningByDefault
        {
            get
            {
                lock (_syncRoot)
                {
                    return _configuration.CollapseReasoningByDefault;
                }
            }
        }

        public event EventHandler? ConfigurationChanged;

        public PresentationUIConfiguration GetConfiguration()
        {
            lock (_syncRoot)
            {
                return CloneConfiguration(_configuration);
            }
        }

        public void SaveConfiguration(PresentationUIConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            lock (_syncRoot)
            {
                _configuration = CloneConfiguration(configuration);
                _configurationRepository.Save(_configuration);
            }

            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private static PresentationUIConfiguration CloneConfiguration(PresentationUIConfiguration configuration)
        {
            return new PresentationUIConfiguration
            {
                CollapseReasoningByDefault = configuration.CollapseReasoningByDefault
            };
        }
    }
}
