using Skyweaver.Services.Directories;

namespace Skyweaver.Services.AerialCityRag
{
    public static class AerialCityRagAvailability
    {
        private static readonly HashSet<string> s_toolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "InitializeAerialCityRAG",
            "SemanticSearch",
            "KeywordSearch"
        };

        public static bool IsAerialCityRagTool(string? toolName)
        {
            return !string.IsNullOrWhiteSpace(toolName) && s_toolNames.Contains(toolName.Trim());
        }

        public static bool AreToolsAvailable()
        {
            try
            {
                var configuration = new AerialCityRagConfigurationRepository().Load();
                return configuration.IsEnabled &&
                    !string.IsNullOrWhiteSpace(SkyweaverDirectoryRuntime.Instance.AerialCityDirectoryPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
