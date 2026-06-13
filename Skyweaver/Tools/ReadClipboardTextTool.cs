using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ReadClipboardTextTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
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
            ArgumentNullException.ThrowIfNull(context);
            return "Reads text from the system clipboard. Due to privacy and security, this tool requires user confirmation.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                []);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (ClipboardAccessService.TryGetText(out var text, out var errorMessage))
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        return Task.FromResult(SkyweaverToolResult.Success("The clipboard is empty or does not contain text."));
                    }
                    return Task.FromResult(SkyweaverToolResult.Success(text));
                }
                else
                {
                    return Task.FromResult(SkyweaverToolResult.Failure(errorMessage ?? "Unknown error reading clipboard."));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadClipboardTextTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to read clipboard: {ex.Message}"));
            }
        }
    }
}
