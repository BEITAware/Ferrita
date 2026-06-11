using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ClipboardTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "Clipboard";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Reads text from or writes text to the system clipboard.",
            "Copy",
            [
                new SkyweaverToolParameterDefinition(
                    "Operation",
                    "The operation to perform: 'Read' or 'Write'.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Text",
                    "The text to write to the clipboard. Required if Operation is 'Write'. Ignored if Operation is 'Read'.",
                    SkyweaverToolParameterType.String,
                    isRequired: false)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Reads text from or writes text to the host system clipboard. The 'Operation' parameter must be either 'Read' or 'Write' (case-insensitive). When writing, provide the 'Text' parameter. When reading, the 'Text' parameter is ignored and the tool returns the current clipboard text content.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Operation", "Operation", "Waiting for operation..."),
                    new ToolInvocationCardFieldDefinition("Text", "Text", "N/A or waiting for text...")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operation = arguments.GetString("Operation")?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(operation))
            {
                return SkyweaverToolResult.Failure("Operation parameter is required.");
            }

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

                    if (string.Equals(operation, "Read", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Skyweaver.Services.ClipboardAccessService.TryGetText(out var text, out var error))
                        {
                            tcs.TrySetResult(SkyweaverToolResult.Success(text));
                        }
                        else
                        {
                            tcs.TrySetResult(SkyweaverToolResult.Failure(error ?? "Failed to read clipboard"));
                        }
                    }
                    else if (string.Equals(operation, "Write", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = arguments.GetString("Text") ?? string.Empty;
                        if (Skyweaver.Services.ClipboardAccessService.TrySetText(text, out var error))
                        {
                            tcs.TrySetResult(SkyweaverToolResult.Success($"Successfully wrote {text.Length} characters to clipboard."));
                        }
                        else
                        {
                            tcs.TrySetResult(SkyweaverToolResult.Failure(error ?? "Failed to write clipboard"));
                        }
                    }
                    else
                    {
                        tcs.TrySetResult(SkyweaverToolResult.Failure($"Unsupported operation: '{operation}'. Valid operations are 'Read' and 'Write'."));
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ClipboardTool execution failed: {ex}");
                    tcs.TrySetResult(SkyweaverToolResult.Failure($"Failed to access clipboard: {ex.Message}"));
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            // Wait for the STA thread task or cancellation
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.ConfigureAwait(false);
        }
    }
}
