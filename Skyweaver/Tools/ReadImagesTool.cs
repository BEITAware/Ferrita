using System.Security;
using System.Text;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ReadImagesTool : ISkyweaverTool
    {
        public const string ToolName = "ReadImages";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Reads one or more images and embeds the validated image files into the tool return as preserved resources. Paths must point to real decodable image files; missing or invalid files are reported as warnings or failures.",
            "Image",
            [
                new SkyweaverToolParameterDefinition(
                    "Paths",
                    "A pipe-separated (|) list of image file paths to read.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["multimodal"]);

        public SkyweaverToolDefinition Definition => s_definition;

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawPaths = arguments.GetString("Paths");
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Task.FromResult(SkyweaverToolResult.Failure(
                    "Paths parameter is required and cannot be empty.",
                    BuildData([], ["Paths parameter is required and cannot be empty."])));
            }

            var paths = rawPaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.Length == 0)
            {
                return Task.FromResult(SkyweaverToolResult.Failure(
                    "At least one image path is required.",
                    BuildData([], ["At least one image path is required."])));
            }

            var errors = new List<string>();
            var validatedImages = ToolImageSupport.ResolveAndValidateImagePaths(
                paths,
                context.WorkspacePath,
                cancellationToken,
                errors);

            if (validatedImages.Count == 0)
            {
                return Task.FromResult(SkyweaverToolResult.Failure(
                    $"Failed to read any valid images. Errors:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                    BuildData([], errors)));
            }

            var builder = new StringBuilder();
            foreach (var image in validatedImages)
            {
                builder.AppendLine($"<PreservedContent><Image Path=\"{SecurityElement.Escape(image.ResolvedPath)}\" /></PreservedContent>");
            }

            if (errors.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Note: Some images could not be read:");
                foreach (var error in errors)
                {
                    builder.AppendLine(error);
                }
            }

            return Task.FromResult(SkyweaverToolResult.Success(
                builder.ToString().TrimEnd(),
                BuildData(validatedImages, errors)));
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            IReadOnlyList<ValidatedImagePath> validatedImages,
            IReadOnlyList<string> errors)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["imageCount"] = validatedImages.Count,
                ["validatedPaths"] = validatedImages.Select(image => image.ResolvedPath).ToArray(),
                ["invalidCount"] = errors.Count,
                ["errors"] = errors.ToArray()
            };
        }
    }
}
