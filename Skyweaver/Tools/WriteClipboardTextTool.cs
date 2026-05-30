using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class WriteClipboardTextTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "WriteClipboardText";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Writes text content to the system clipboard.",
            "ClipboardText", // icon name
            [
                new SkyweaverToolParameterDefinition(
                    "Text",
                    "The text content to copy to the system clipboard.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Writes text content to the system clipboard. Useful for providing data or commands to the user that they can easily paste elsewhere.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Text", "Text", "Waiting for text...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = arguments.GetString("Text") ?? string.Empty;

            try
            {
                if (ClipboardAccessService.TrySetText(text, out var errorMessage))
                {
                    return Task.FromResult(SkyweaverToolResult.Success("Successfully copied text to clipboard."));
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
                System.Diagnostics.Debug.WriteLine($"WriteClipboardTextTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to write text to clipboard: {ex.Message}"));
            }
        }
    }
}
