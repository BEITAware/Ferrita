using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
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
            "Reads text content currently copied to the system clipboard.",
            "ClipboardText", // icon name
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Reads text content currently copied to the system clipboard. Useful for accessing data the user has just copied.";
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

            var tcs = new TaskCompletionSource<SkyweaverToolResult>();

            var thread = new Thread(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    string? clipboardText = null;
                    if (Clipboard.ContainsText())
                    {
                        clipboardText = Clipboard.GetText();
                    }

                    if (clipboardText == null)
                    {
                        tcs.TrySetResult(SkyweaverToolResult.Failure("The clipboard does not contain text."));
                    }
                    else
                    {
                        tcs.TrySetResult(SkyweaverToolResult.Success(clipboardText));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ReadClipboardTextTool execution failed: {ex}");
                    tcs.TrySetResult(SkyweaverToolResult.Failure($"Failed to read clipboard: {ex.Message}"));
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            // Handle external cancellation to avoid hanging if the STA thread gets stuck
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            return tcs.Task;
        }
    }
}
