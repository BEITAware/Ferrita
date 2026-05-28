using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class GetSystemInfoTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "GetSystemInfo";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Retrieves basic information about the host system.",
            "Device",
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Retrieves information about the host system where the agent is running, including OS version, architecture, CPU count, and machine name.";
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
                var builder = new StringBuilder();
                builder.AppendLine($"OS Description: {RuntimeInformation.OSDescription}");
                builder.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
                builder.AppendLine($"Framework Description: {RuntimeInformation.FrameworkDescription}");
                builder.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
                builder.AppendLine($"Processor Count: {Environment.ProcessorCount}");
                builder.AppendLine($"Machine Name: {Environment.MachineName}");
                builder.AppendLine($"User Domain Name: {Environment.UserDomainName}");
                builder.AppendLine($"User Name: {Environment.UserName}");
                builder.AppendLine($"System Directory: {Environment.SystemDirectory}");
                builder.AppendLine($"Current Directory: {Environment.CurrentDirectory}");

                return Task.FromResult(SkyweaverToolResult.Success(builder.ToString().TrimEnd()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetSystemInfoTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to retrieve system information: {ex.Message}"));
            }
        }
    }
}
