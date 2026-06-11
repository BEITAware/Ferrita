using System;
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
            "Retrieves basic system information such as OS version, machine name, logical drives, processor count, and framework version.",
            "Device",
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Retrieves basic system information about the host environment, useful for context and diagnostics. Returns data like OS Version, Machine Name, Logical Drives, Processor Count, etc.";
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
                builder.AppendLine("System Information:");
                builder.AppendLine(new string('-', 30));
                builder.AppendLine($"OS Version:        {Environment.OSVersion}");
                builder.AppendLine($"Machine Name:      {Environment.MachineName}");
                builder.AppendLine($"User Name:         {Environment.UserName}");
                builder.AppendLine($"Processor Count:   {Environment.ProcessorCount}");
                builder.AppendLine($"System Directory:  {Environment.SystemDirectory}");
                builder.AppendLine($"Current Directory: {Environment.CurrentDirectory}");
                builder.AppendLine($"64-bit OS:         {Environment.Is64BitOperatingSystem}");
                builder.AppendLine($"64-bit Process:    {Environment.Is64BitProcess}");
                builder.AppendLine($"Framework Version: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                builder.AppendLine($"Logical Drives:    {string.Join(", ", Environment.GetLogicalDrives())}");

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
