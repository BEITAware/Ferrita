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
            "Opens a directory in the host system's default file manager.",
            "Folder",
            [
                new SkyweaverToolParameterDefinition(
                    "DirectoryPath",
                    "Path of the directory to open. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Opens the specified directory in the system's default file manager (e.g., Windows Explorer).";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Directory", "DirectoryPath", "Waiting for directory path...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestedPath = arguments.GetString("DirectoryPath") ?? string.Empty;
            string resolvedPath;

            try
            {
                resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(SkyweaverToolResult.Failure($"Invalid path: {ex.Message}"));
            }

            if (!Directory.Exists(resolvedPath))
            {
                if (File.Exists(resolvedPath))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure($"Path points to a file, not a directory: {resolvedPath}"));
                }
                return Task.FromResult(SkyweaverToolResult.Failure($"Directory not found: {resolvedPath}"));
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    UseShellExecute = true,
                    Verb = "open"
                });

                return Task.FromResult(SkyweaverToolResult.Success($"Opened directory: {resolvedPath}"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenDirectoryTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to open directory: {ex.Message}"));
            }
        }
    }
}
