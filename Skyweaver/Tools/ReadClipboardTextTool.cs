using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Skyweaver.Services;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ReadClipboardTextTool : ISkyweaverTool, ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ReadClipboardText";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Reads text from the system clipboard.",
            "System",
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            return "Reads text from the system clipboard. Use this when you need to access information the user has copied.";
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (ClipboardAccessService.TryGetText(out string text, out var errorMessage))
                {
                    return Task.FromResult(SkyweaverToolResult.Success(
                        "Text successfully read from the clipboard.",
                        new Dictionary<string, object?> { ["Text"] = text }
                    ));
                }
                else
                {
                    return Task.FromResult(SkyweaverToolResult.Failure(errorMessage ?? "Failed to read text from clipboard."));
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
