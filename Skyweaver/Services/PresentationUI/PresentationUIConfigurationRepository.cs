using System.IO;
using System.Xml.Linq;
using Skyweaver.Models.PresentationUI;

namespace Skyweaver.Services.PresentationUI
{
    public sealed class PresentationUIConfigurationRepository
    {
        private readonly PresentationUIPathProvider _pathProvider;
        private readonly object _syncRoot = new();

        public PresentationUIConfigurationRepository(PresentationUIPathProvider pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public string ConfigurationFilePath => _pathProvider.ConfigurationFilePath;

        public PresentationUIConfiguration Load()
        {
            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                if (!File.Exists(ConfigurationFilePath))
                {
                    var configuration = CreateDefaultConfiguration();
                    Save(configuration);
                    return configuration;
                }

                var document = XDocument.Load(ConfigurationFilePath);
                var root = document.Root ?? throw new InvalidDataException("PresentationUI 配置 XML 缺少根节点。");

                return new PresentationUIConfiguration
                {
                    CollapseReasoningByDefault = (bool?)root.Element("CollapseReasoningByDefault") ?? true
                };
            }
        }

        public void Save(PresentationUIConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                var document = new XDocument(
                    new XElement("PresentationUIConfiguration",
                        new XAttribute("SchemaVersion", 1),
                        new XElement("CollapseReasoningByDefault", configuration.CollapseReasoningByDefault)));

                document.Save(ConfigurationFilePath);
            }
        }

        private void EnsureConfigurationDirectory()
        {
            Directory.CreateDirectory(_pathProvider.ConfigurationDirectoryPath);
        }

        private static PresentationUIConfiguration CreateDefaultConfiguration()
        {
            return new PresentationUIConfiguration();
        }
    }
}
