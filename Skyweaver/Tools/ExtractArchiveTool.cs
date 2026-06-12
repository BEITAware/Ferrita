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
    public sealed class ExtractArchiveTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ExtractArchive";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Extracts the contents of a ZIP archive to a specified directory.",
            "Archive", // Using a generic icon, assuming "Archive" or something similar exists
            [
                new SkyweaverToolParameterDefinition(
                    "ArchiveFilePath",
                    "The path to the ZIP archive file. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "DestinationDirectoryPath",
                    "The directory where the contents should be extracted. If omitted, it extracts to a folder next to the archive.",
                    SkyweaverToolParameterType.String,
                    isRequired: false)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Extracts the contents of a ZIP archive file to a specified directory. Useful for unpacking compressed files. Both absolute and workspace-relative paths are supported.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Archive File", "ArchiveFilePath", "Waiting for archive path..."),
                    new ToolInvocationCardFieldDefinition("Destination", "DestinationDirectoryPath", "Default destination")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestedArchivePath = arguments.GetString("ArchiveFilePath") ?? string.Empty;
            var requestedDestPath = arguments.GetString("DestinationDirectoryPath");

            string resolvedArchivePath;
            try
            {
                resolvedArchivePath = ToolFileSystemHelper.ResolvePath(requestedArchivePath, context.WorkspacePath);
            }
            catch (Exception ex)
            {
                return SkyweaverToolResult.Failure($"Invalid archive path: {ex.Message}");
            }

            if (!File.Exists(resolvedArchivePath))
            {
                return SkyweaverToolResult.Failure($"Archive file not found: {resolvedArchivePath}");
            }

            string resolvedDestPath;
            if (string.IsNullOrWhiteSpace(requestedDestPath))
            {
                resolvedDestPath = Path.Combine(
                    Path.GetDirectoryName(resolvedArchivePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(resolvedArchivePath));
            }
            else
            {
                try
                {
                    resolvedDestPath = ToolFileSystemHelper.ResolvePath(requestedDestPath, context.WorkspacePath);
                }
                catch (Exception ex)
                {
                    return SkyweaverToolResult.Failure($"Invalid destination path: {ex.Message}");
                }
            }

            try
            {
                Directory.CreateDirectory(resolvedDestPath);

                // Run the actual extraction on a background thread to not block if it's large
                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(resolvedArchivePath, resolvedDestPath, overwriteFiles: true);
                }, cancellationToken);

                return SkyweaverToolResult.Success($"Successfully extracted archive to:\n{resolvedDestPath}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractArchiveTool execution failed: {ex}");
                return SkyweaverToolResult.Failure($"Failed to extract archive: {ex.Message}");
            }
        }
    }
}
