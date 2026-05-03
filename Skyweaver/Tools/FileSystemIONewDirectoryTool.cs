using System.IO;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class FileSystemIONewDirectoryTool : ISkyweaverTool
    {
        public const string ToolName = "FileSystemIO_NewDirectory";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Creates a new directory and its parents if they do not exist.",
            "Script",
            parameters: [
                new SkyweaverToolParameterDefinition(
                    "DirectoryPath",
                    "The path of the directory to create.",
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
            var requestedPath = arguments.GetString("DirectoryPath");
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("DirectoryPath parameter is missing or empty."));
            }

            try
            {
                var resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);

                if (Directory.Exists(resolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"Directory already exists at {resolvedPath}."));
                }

                Directory.CreateDirectory(resolvedPath);

                return Task.FromResult(SkyweaverToolResult.Success($"Successfully created directory at {resolvedPath}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to create directory: {ex.Message}"));
            }
        }
    }
}
