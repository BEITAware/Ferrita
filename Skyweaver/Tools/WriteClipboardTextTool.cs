using System;
using System.Threading;
using System.Threading.Tasks;
using Skyweaver.Services;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class WriteClipboardTextTool : ISkyweaverTool, ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "WriteClipboardText";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Writes text to the system clipboard.",
            "System",
            [
                new SkyweaverToolParameterDefinition(
                    "Text",
                    "The text to write to the clipboard.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            return "Writes text to the system clipboard. Use this when the user explicitly asks to copy something to the clipboard.";
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? text = arguments.GetString("Text");
                if (text == null)
                {
                    return Task.FromResult(SkyweaverToolResult.Failure("Missing or invalid 'Text' parameter."));
                }

                if (ClipboardAccessService.TrySetText(text, out var errorMessage))
                {
                    return Task.FromResult(SkyweaverToolResult.Success("Text successfully written to the clipboard."));
                }
                else
                {
                    return Task.FromResult(SkyweaverToolResult.Failure(errorMessage ?? "Failed to write text to clipboard."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"An unexpected error occurred: {ex.Message}"));
            }
        }
    }
}
