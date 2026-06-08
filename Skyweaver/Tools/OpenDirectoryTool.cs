using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class OpenDirectoryTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "OpenDirectory";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Opens a specified directory in the host system's file explorer.",
            "Folder",
            [
                new SkyweaverToolParameterDefinition(
                    "Path",
                    "The path to the directory to open. Can be an absolute path or relative to the workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Opens the specified directory in the system's file explorer. Useful for showing the user the result of generated files or taking them to a specific location on disk.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Path", "Path", "Waiting for path...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestedPath = arguments.GetString("Path") ?? string.Empty;

            try
            {
                var resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);

                if (!Directory.Exists(resolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"The directory does not exist: {resolvedPath}"));
                }

                Process.Start(new ProcessStartInfo()
                {
                    FileName = resolvedPath,
                    UseShellExecute = true
                });

                return Task.FromResult(SkyweaverToolResult.Success($"Successfully opened directory: {resolvedPath}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenDirectoryTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to open directory: {ex.Message}"));
            }
        }
    }
}
