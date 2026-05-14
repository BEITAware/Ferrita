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
    public sealed class OpenTargetTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "OpenTarget";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Opens a web URL in the default browser or a local directory path in the default file manager.",
            "Earth",
            [
                new SkyweaverToolParameterDefinition(
                    "Target",
                    "The web URL (http or https) or absolute local directory path to open.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Opens a target in the host operating system's default application. Supported targets are HTTP/HTTPS URLs (opens in default browser) and local directory paths (opens in default file manager like Windows Explorer). Local file paths are not supported, only directory paths.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Target", "Target", "Waiting for target...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = arguments.GetString("Target")?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(target))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("Target cannot be empty."));
            }

            try
            {
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uri.ToString(),
                        UseShellExecute = true
                    });

                    return Task.FromResult(SkyweaverToolResult.Success($"Opened URL '{target}' in the default browser."));
                }

                if (Directory.Exists(target))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });

                    return Task.FromResult(SkyweaverToolResult.Success($"Opened directory '{target}' in the default file manager."));
                }

                return Task.FromResult(SkyweaverToolResult.Failure(
                    $"Target '{target}' is neither a valid HTTP/HTTPS URL nor an existing local directory path. File paths are not supported, only directories."));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenTargetTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to open target '{target}': {ex.Message}"));
            }
        }
    }
}
