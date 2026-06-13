using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.Notifications;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ShowNotificationTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ShowNotification";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Shows a transient notification to the user in the application UI.",
            "UI",
            [
                new SkyweaverToolParameterDefinition(
                    "Message",
                    "The message to display in the notification.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Shows a transient notification to the user in the application UI. Use this to inform the user about important but non-blocking updates or events.";
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

            var message = arguments.GetString("Message")?.Trim();

            if (string.IsNullOrEmpty(message))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("Message is required."));
            }

            try
            {
                NotificationService.Instance.ShowTransient(message);
                return Task.FromResult(SkyweaverToolResult.Success("Notification shown."));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowNotificationTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to show notification: {ex.Message}"));
            }
        }
    }
}
