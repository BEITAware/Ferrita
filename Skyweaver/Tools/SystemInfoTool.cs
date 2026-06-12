using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class SystemInfoTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "SystemInfo";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Retrieves detailed system information including OS, CPU, RAM, and Disk space.",
            "Device",
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Retrieves detailed system information including OS version, CPU details, RAM usage, and available disk space. Useful for understanding the host environment.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.Create(context, []);
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
                builder.AppendLine("=== System Information ===");

                // OS Info
                builder.AppendLine($"OS Description: {RuntimeInformation.OSDescription}");
                builder.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
                builder.AppendLine($"Framework Description: {RuntimeInformation.FrameworkDescription}");
                builder.AppendLine($"Machine Name: {Environment.MachineName}");
                builder.AppendLine($"User Domain Name: {Environment.UserDomainName}");
                builder.AppendLine($"User Name: {Environment.UserName}");

                // Processor Info
                builder.AppendLine($"Processor Count: {Environment.ProcessorCount}");
                builder.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");

                // Memory Info (Basic)
                var process = Process.GetCurrentProcess();
                builder.AppendLine($"Current Process Working Set: {process.WorkingSet64 / 1024 / 1024} MB");

                // Drives Info
                builder.AppendLine("\n=== Logical Drives ===");
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (drive.IsReady)
                    {
                        builder.AppendLine($"Drive {drive.Name}");
                        builder.AppendLine($"  Type: {drive.DriveType}");
                        builder.AppendLine($"  Format: {drive.DriveFormat}");
                        builder.AppendLine($"  Total Space: {drive.TotalSize / 1024 / 1024 / 1024} GB");
                        builder.AppendLine($"  Free Space: {drive.AvailableFreeSpace / 1024 / 1024 / 1024} GB");
                    }
                }

                return Task.FromResult(SkyweaverToolResult.Success(builder.ToString().TrimEnd()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SystemInfoTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to retrieve system info: {ex.Message}"));
            }
        }
    }
}
