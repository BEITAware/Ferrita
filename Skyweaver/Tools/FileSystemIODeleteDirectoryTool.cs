using System.IO;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class FileSystemIODeleteDirectoryTool : ISkyweaverTool
    {
        public const string ToolName = "FileSystemIO_DeleteDirectory";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Deletes a directory and optionally its contents.",
            "Script",
            parameters: [
                new SkyweaverToolParameterDefinition(
                    "DirectoryPath",
                    "The path of the directory to delete.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Recursive",
                    "If true, removes all files and subdirectories. Defaults to true.",
                    SkyweaverToolParameterType.Boolean,
                    isRequired: false,
                    defaultValue: "true")
            ],
            isSystemTool: false);

        public SkyweaverToolDefinition Definition => s_definition;

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            var requestedPath = arguments.GetString("DirectoryPath");
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("DirectoryPath parameter is missing or empty."));
            }

            var recursive = arguments.GetBoolean("Recursive", true);

            try
            {
                var resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);

                if (!Directory.Exists(resolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"Directory does not exist: {resolvedPath}"));
                }

                Directory.Delete(resolvedPath, recursive);

                return Task.FromResult(SkyweaverToolResult.Success($"Successfully deleted directory at {resolvedPath}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to delete directory: {ex.Message}"));
            }
        }
    }
}
