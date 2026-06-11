using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.Notifications;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class NotificationTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ShowNotification";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Displays a transient notification message in the Skyweaver UI.",
            "Message",
            [
                new SkyweaverToolParameterDefinition(
                    "Message",
                    "The text message to display in the notification.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Displays a transient (temporary) notification message on the screen to alert the user of important information or status updates. Use this tool when you need to proactively inform the user about an ongoing background task or a significant event.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Message", "Message", "Waiting for message...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var message = arguments.GetString("Message")?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(message))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("Message parameter is required and cannot be empty."));
            }

            try
            {
                // Ensure we run on the UI thread for UI notification
                Application.Current.Dispatcher.Invoke(() =>
                {
                    NotificationService.Instance.ShowTransient(message);
                });

                return Task.FromResult(SkyweaverToolResult.Success($"Notification displayed successfully: '{message}'"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NotificationTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to show notification: {ex.Message}"));
            }
        }
    }
}
