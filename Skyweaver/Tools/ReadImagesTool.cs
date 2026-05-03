using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ReadImagesTool : ISkyweaverTool
    {
        public const string ToolName = "ReadImages";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Reads multiple images and embeds them into the tool return as preserved resources.",
            "Image",
            [
                new SkyweaverToolParameterDefinition(
                    "Paths",
                    "A pipe-separated (|) list of image file paths to read.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ]);

        public SkyweaverToolDefinition Definition => s_definition;

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            var rawPaths = arguments.GetString("Paths");
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return SkyweaverToolResult.Failure("Paths parameter is required and cannot be empty.");
            }

            var paths = rawPaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var builder = new StringBuilder();
            var validPaths = new List<string>();
            var errors = new List<string>();

            // Concurrency limit is 3
            var semaphore = new SemaphoreSlim(3);
            var tasks = paths.Select(async p =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var resolvedPath = ToolFileSystemHelper.ResolvePath(p, context.WorkspacePath);
                    if (!File.Exists(resolvedPath))
                    {
                        return new { Path = p, ResolvedPath = resolvedPath, Error = "File does not exist." };
                    }
                    return new { Path = p, ResolvedPath = resolvedPath, Error = (string?)null };
                }
                catch (Exception ex)
                {
                    return new { Path = p, ResolvedPath = p, Error = ex.Message };
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                if (result.Error != null)
                {
                    errors.Add($"Failed to process path '{result.Path}': {result.Error}");
                }
                else
                {
                    validPaths.Add(result.ResolvedPath);
                    builder.AppendLine($"<SkyweaverPreservedContent><Image Path=\"{SecurityElement.Escape(result.ResolvedPath)}\" /></SkyweaverPreservedContent>");
                }
            }

            if (validPaths.Count == 0)
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to read any images. Errors:\n{string.Join("\n", errors)}");
            }

            var message = builder.ToString();
            if (errors.Count > 0)
            {
                message += $"\nNote: Some images could not be read:\n{string.Join("\n", errors)}";
            }

            return SkyweaverToolResult.Success(message);
        }
    }
}
