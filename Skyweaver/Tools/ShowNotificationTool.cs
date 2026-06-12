using System;
using System.Threading;
using System.Threading.Tasks;
using Skyweaver.Services.Notifications;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ShowNotificationTool : ISkyweaverTool, ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ShowNotification";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Shows a transient in-app notification message to the user.",
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
            return "Shows a transient in-app notification message to the user. Use this to provide non-intrusive feedback or alerts.";
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? message = arguments.GetString("Message");
                if (message == null)
                {
                    return Task.FromResult(SkyweaverToolResult.Failure("Missing or invalid 'Message' parameter."));
                }

                NotificationService.Instance.ShowTransient(message);

                return Task.FromResult(SkyweaverToolResult.Success("Notification shown successfully."));
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
