using System.IO;
using Newtonsoft.Json.Linq;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class FileSystemIODeleteFileTool : ISkyweaverTool
    {
        public const string ToolName = "FileSystemIO_DeleteFile";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Deletes one or multiple files.",
            "Script",
            parameters: [
                new SkyweaverToolParameterDefinition(
                    "Files",
                    "A JSON array of file paths to delete. Example: [\"file1.txt\", \"file2.txt\"]",
                    SkyweaverToolParameterType.Json,
                    isRequired: true)
            ],
            isSystemTool: false);

        public SkyweaverToolDefinition Definition => s_definition;

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            var filesJson = arguments.GetJson("Files");
            if (filesJson == null || filesJson.Type != JTokenType.Array)
            {
                return Task.FromResult(SkyweaverToolResult.Failure("Files parameter is missing or is not a valid JSON array."));
            }

            var deletedFiles = new List<string>();
            var errors = new List<string>();

            foreach (var fileToken in filesJson)
            {
                if (fileToken.Type != JTokenType.String)
                {
                    errors.Add($"Invalid JSON token type: {fileToken.Type}. Expected String.");
                    continue;
                }

                var requestedPath = fileToken.Value<string>();
                if (string.IsNullOrWhiteSpace(requestedPath))
                {
                    errors.Add("A file path was null or empty.");
                    continue;
                }

                try
                {
                    var resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);
                    if (!File.Exists(resolvedPath))
                    {
                        errors.Add($"File does not exist: {resolvedPath}");
                        continue;
                    }

                    File.Delete(resolvedPath);
                    deletedFiles.Add(resolvedPath);
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to delete {requestedPath}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                var errorMessage = $"Deleted {deletedFiles.Count} files. Errors encountered:\n" + string.Join("\n", errors);
                return Task.FromResult(SkyweaverToolResult.Failure(errorMessage));
            }

            return Task.FromResult(SkyweaverToolResult.Success($"Successfully deleted {deletedFiles.Count} files."));
        }
    }
}
