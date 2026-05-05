using System.IO;

namespace Skyweaver.Services.PresentationUI
{
    public sealed class PresentationUIPathProvider
    {
        public string ConfigurationDirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SkyWeaver",
            "Configuration");

        public string ConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "PresentationUI.xml");
    }
}
