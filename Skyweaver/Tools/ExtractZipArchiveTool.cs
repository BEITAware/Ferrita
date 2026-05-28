using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ExtractZipArchiveTool :
        ISkyweaverTool,
        ISkyweaverToolConfigurationProvider,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ExtractZipArchive";

        private const string SettingsRootElementName = "ExtractZipArchiveSettings";

        private static readonly SkyweaverToolDefinition s_definition = BuildDefinition(new ToolFileSystemPermissionSettings());

        public SkyweaverToolDefinition Definition => s_definition;

        public SkyweaverToolDefinition GetEffectiveDefinition(SkyweaverToolConfigurationState configuration)
        {
            return BuildDefinition(ToolFileSystemPermissionSettings.FromConfiguration(configuration, SettingsRootElementName));
        }

        public SkyweaverToolConfigurationPresenter? CreateConfigurationPresenter(SkyweaverToolConfigurationEditorContext context)
        {
            return new ToolFileSystemPermissionConfigurationPresenter(context, SettingsRootElementName, ToolName);
        }

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            var settings = ToolFileSystemPermissionSettings.FromConfiguration(context.ConfigurationState, SettingsRootElementName);
            return ToolFileSystemMutationSupport.BuildPromptDescription(
                "Extracts all files from a ZIP archive to a specified directory. The destination directory will be created if it does not exist.",
                settings.PermissionScope);
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Archive", "ArchiveFile", "Waiting for archive path..."),
                    new ToolInvocationCardFieldDefinition("Destination", "DestinationDirectory", "Waiting for destination path..."),
                    new ToolInvocationCardFieldDefinition("Overwrite", "Overwrite", "Default false")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var settings = ToolFileSystemPermissionSettings.FromConfiguration(context.CurrentToolConfiguration, SettingsRootElementName);
            var requestedArchivePath = arguments.GetString("ArchiveFile") ?? string.Empty;
            var requestedDestinationPath = arguments.GetString("DestinationDirectory") ?? string.Empty;
            var overwrite = arguments.GetBoolean("Overwrite", false);

            ToolResolvedPathInfo? archivePathInfo = null;
            ToolResolvedPathInfo? destinationPathInfo = null;

            try
            {
                archivePathInfo = ToolFileSystemMutationSupport.ResolveAuthorizedPath(
                    requestedArchivePath,
                    context.WorkspacePath,
                    settings.PermissionScope); // Reusing permission scope logic

                destinationPathInfo = ToolFileSystemMutationSupport.ResolveAuthorizedPath(
                    requestedDestinationPath,
                    context.WorkspacePath,
                    settings.PermissionScope);

                if (Directory.Exists(archivePathInfo.ResolvedPath))
                {
                     return Task.FromResult(SkyweaverToolResult.Failure($"Archive path points to a directory, not a file: {archivePathInfo.ResolvedPath}"));
                }

                if (!File.Exists(archivePathInfo.ResolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"Archive file not found: {archivePathInfo.ResolvedPath}"));
                }

                if (File.Exists(destinationPathInfo.ResolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"Destination path points to an existing file: {destinationPathInfo.ResolvedPath}"));
                }

                ZipFile.ExtractToDirectory(archivePathInfo.ResolvedPath, destinationPathInfo.ResolvedPath, overwriteFiles: overwrite);

                return Task.FromResult(SkyweaverToolResult.Success(
                    $"Successfully extracted {archivePathInfo.ResolvedPath} to {destinationPathInfo.ResolvedPath}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to extract archive: {ex.Message}"));
            }
        }

        private static SkyweaverToolDefinition BuildDefinition(ToolFileSystemPermissionSettings settings)
        {
            return new SkyweaverToolDefinition(
                ToolName,
                ToolFileSystemMutationSupport.BuildPromptDescription(
                    "Extracts a ZIP archive to a directory. The destination directory will be created if it does not exist.",
                    settings.PermissionScope),
                "Script",
                [
                    new SkyweaverToolParameterDefinition(
                        "ArchiveFile",
                        "The path of the ZIP archive to extract. Relative paths resolve against the current workspace.",
                        SkyweaverToolParameterType.String,
                        isRequired: true),
                    new SkyweaverToolParameterDefinition(
                        "DestinationDirectory",
                        "The directory path where files will be extracted. Relative paths resolve against the current workspace.",
                        SkyweaverToolParameterType.String,
                        isRequired: true),
                    new SkyweaverToolParameterDefinition(
                        "Overwrite",
                        "If true, overwrites existing files in the destination. Defaults to false.",
                        SkyweaverToolParameterType.Boolean,
                        isRequired: false,
                        defaultValue: "false")
                ],
                isSystemTool: false);
        }

        private static bool IsExpectedException(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or InvalidDataException; // Zip specific
        }
    }
}
