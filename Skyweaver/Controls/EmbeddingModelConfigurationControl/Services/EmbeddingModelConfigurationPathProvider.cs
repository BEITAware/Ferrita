using System.IO;
using Skyweaver.Services.Directories;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Services
{
    public sealed class EmbeddingModelConfigurationPathProvider
    {
        public string ConfigurationDirectoryPath => SkyweaverDirectoryRuntime.Instance.ConfigurationDirectoryPath;

        public string EmbeddingModelFilePath => Path.Combine(ConfigurationDirectoryPath, "EmbeddingModel.xml");
    }
}
