using System.IO;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class FileSystemIOCreateFileTool : ISkyweaverTool
    {
        public const string ToolName = "FileSystemIO_CreateFile";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Creates a new empty file. If the directory does not exist, it will be created.",
            "Script",
            parameters: [
                new SkyweaverToolParameterDefinition(
                    "FilePath",
                    "The path of the file to create.",
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
            var requestedPath = arguments.GetString("FilePath");
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("FilePath parameter is missing or empty."));
            }

            try
            {
                var resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);

                var directoryPath = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                if (File.Exists(resolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"File already exists at {resolvedPath}."));
                }

                File.WriteAllText(resolvedPath, string.Empty);

                return Task.FromResult(SkyweaverToolResult.Success($"Successfully created file at {resolvedPath}."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to create file: {ex.Message}"));
            }
        }
    }
}
