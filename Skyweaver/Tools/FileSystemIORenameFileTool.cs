using System.IO;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class FileSystemIORenameFileTool : ISkyweaverTool
    {
        public const string ToolName = "FileSystemIO_RenameFile";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Renames or moves a file or directory.",
            "Script",
            parameters: [
                new SkyweaverToolParameterDefinition(
                    "SourcePath",
                    "The current path of the file or directory.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "DestinationPath",
                    "The new path for the file or directory.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            isSystemTool: false);

        public SkyweaverToolDefinition Definition => s_definition;

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            var sourcePath = arguments.GetString("SourcePath");
            var destinationPath = arguments.GetString("DestinationPath");

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("SourcePath parameter is missing or empty."));
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("DestinationPath parameter is missing or empty."));
            }

            try
            {
                var resolvedSource = ToolFileSystemHelper.ResolvePath(sourcePath, context.WorkspacePath);
                var resolvedDestination = ToolFileSystemHelper.ResolvePath(destinationPath, context.WorkspacePath);

                var destinationDirectory = Path.GetDirectoryName(resolvedDestination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory) && !Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (File.Exists(resolvedSource))
                {
                    if (File.Exists(resolvedDestination))
                    {
                        return Task.FromResult(SkyweaverToolResult.Failure($"A file already exists at the destination path: {resolvedDestination}"));
                    }

                    File.Move(resolvedSource, resolvedDestination);
                    return Task.FromResult(SkyweaverToolResult.Success($"Successfully moved file from {resolvedSource} to {resolvedDestination}."));
                }

                if (Directory.Exists(resolvedSource))
                {
                    if (Directory.Exists(resolvedDestination))
                    {
                        return Task.FromResult(SkyweaverToolResult.Failure($"A directory already exists at the destination path: {resolvedDestination}"));
                    }

                    Directory.Move(resolvedSource, resolvedDestination);
                    return Task.FromResult(SkyweaverToolResult.Success($"Successfully moved directory from {resolvedSource} to {resolvedDestination}."));
                }

                return Task.FromResult(SkyweaverToolResult.Failure($"Source path does not exist: {resolvedSource}"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to rename/move: {ex.Message}"));
            }
        }
    }
}
