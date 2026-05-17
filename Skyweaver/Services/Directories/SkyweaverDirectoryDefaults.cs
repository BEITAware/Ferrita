using System.IO;
using Skyweaver.Models.Directories;

namespace Skyweaver.Services.Directories
{
    public static class SkyweaverDirectoryDefaults
    {
        public const string ApplicationFolderName = "Skyweaver";
        public const string ChatSessionsFolderName = "ChatSessions";
        public const string ConfigurationFolderName = "Configuration";
        public const string DebugFolderName = "Debug";
        public const string SessionFlowsFolderName = "Nodegraphs";
        public const string AerialCityFolderName = "AerialCity";
        public const string DirectoriesFileName = "Directories.xml";

        public static string UserProfileDirectoryPath => ResolveUserProfileDirectoryPath();

        public static string ApplicationDirectoryPath => Path.Combine(
            UserProfileDirectoryPath,
            ApplicationFolderName);

        public static string DefaultChatSessionsDirectoryPath => Path.Combine(
            ApplicationDirectoryPath,
            ChatSessionsFolderName);

        public static string DefaultConfigurationDirectoryPath => Path.Combine(
            ApplicationDirectoryPath,
            ConfigurationFolderName);

        public static string DefaultDebugDirectoryPath => Path.Combine(
            ApplicationDirectoryPath,
            DebugFolderName);

        public static string DefaultSessionFlowsDirectoryPath => Path.Combine(
            ApplicationDirectoryPath,
            SessionFlowsFolderName);

        public static string DefaultAerialCityDirectoryPath => Path.Combine(
            ApplicationDirectoryPath,
            AerialCityFolderName);

        public static string DirectoriesConfigurationFilePath => Path.Combine(
            DefaultConfigurationDirectoryPath,
            DirectoriesFileName);

        public static DirectoriesConfiguration CreateDefaultConfiguration()
        {
            return new DirectoriesConfiguration
            {
                ChatSessionsDirectoryPath = DefaultChatSessionsDirectoryPath,
                ConfigurationDirectoryPath = DefaultConfigurationDirectoryPath,
                DebugDirectoryPath = DefaultDebugDirectoryPath,
                SessionFlowsDirectoryPath = DefaultSessionFlowsDirectoryPath,
                AerialCityDirectoryPath = DefaultAerialCityDirectoryPath
            };
        }

        public static DirectoriesConfiguration NormalizeConfiguration(DirectoriesConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return new DirectoriesConfiguration
            {
                ChatSessionsDirectoryPath = NormalizeDirectoryPath(
                    configuration.ChatSessionsDirectoryPath,
                    DefaultChatSessionsDirectoryPath),
                ConfigurationDirectoryPath = NormalizeDirectoryPath(
                    configuration.ConfigurationDirectoryPath,
                    DefaultConfigurationDirectoryPath),
                DebugDirectoryPath = NormalizeDirectoryPath(
                    configuration.DebugDirectoryPath,
                    DefaultDebugDirectoryPath),
                SessionFlowsDirectoryPath = NormalizeDirectoryPath(
                    configuration.SessionFlowsDirectoryPath,
                    DefaultSessionFlowsDirectoryPath),
                AerialCityDirectoryPath = NormalizeDirectoryPath(
                    configuration.AerialCityDirectoryPath,
                    DefaultAerialCityDirectoryPath)
            };
        }

        public static string NormalizeDirectoryPath(string? directoryPath, string fallbackDirectoryPath)
        {
            var normalizedPath = string.IsNullOrWhiteSpace(directoryPath)
                ? fallbackDirectoryPath
                : Environment.ExpandEnvironmentVariables(directoryPath.Trim());

            return Path.GetFullPath(normalizedPath);
        }

        private static string ResolveUserProfileDirectoryPath()
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return userProfile.Trim();
            }

            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return userProfile;
            }

            userProfile = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return userProfile.Trim();
            }

            return AppContext.BaseDirectory;
        }
    }
}
