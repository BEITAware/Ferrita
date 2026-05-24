using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ListProcessesTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ListProcesses";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Lists running processes on the host machine.",
            "Device",
            [
                new SkyweaverToolParameterDefinition(
                    "NameFilter",
                    "Optional filter to search for specific process names. If provided, only processes whose name contains this string (case-insensitive) are returned.",
                    SkyweaverToolParameterType.String,
                    isRequired: false)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Lists running processes on the host machine. Returns Process ID (PID), Process Name, and Memory Usage. You can optionally provide a NameFilter to only return processes matching a specific name.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Name Filter", "NameFilter", "All processes")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nameFilter = arguments.GetString("NameFilter")?.Trim();

            try
            {
                var processes = Process.GetProcesses();
                var builder = new StringBuilder();

                var filteredProcesses = string.IsNullOrEmpty(nameFilter)
                    ? processes
                    : processes.Where(p => p.ProcessName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

                builder.AppendLine($"Found {filteredProcesses.Length} processes" + (string.IsNullOrEmpty(nameFilter) ? "." : $" matching filter '{nameFilter}'."));
                builder.AppendLine(new string('-', 60));
                builder.AppendLine($"{"PID",-10} {"Name",-35} {"Memory (MB)",12}");
                builder.AppendLine(new string('-', 60));

                foreach (var p in filteredProcesses.OrderBy(p => p.ProcessName).ThenBy(p => p.Id))
                {
                    double memoryMb = 0;
                    try
                    {
                        memoryMb = p.WorkingSet64 / (1024.0 * 1024.0);
                    }
                    catch
                    {
                        // Ignore access denied for memory usage on certain system processes
                    }

                    builder.AppendLine($"{p.Id,-10} {Truncate(p.ProcessName, 33),-35} {memoryMb,12:F2}");
                }

                return Task.FromResult(SkyweaverToolResult.Success(builder.ToString().TrimEnd()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListProcessesTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to list processes: {ex.Message}"));
            }
        }

        private static string Truncate(string value, int maxChars)
        {
            return value.Length <= maxChars ? value : value.Substring(0, maxChars - 3) + "...";
        }
    }
}
