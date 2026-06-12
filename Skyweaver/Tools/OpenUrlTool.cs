using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class OpenUrlTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "OpenUrl";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Opens a given URL in the system's default web browser.",
            "Web",
            [
                new SkyweaverToolParameterDefinition(
                    "Url",
                    "The URL to open. Must start with http:// or https://.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Opens the specified URL in the host system's default web browser. Only http and https URLs are allowed.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("URL", "Url", "Waiting for URL...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var urlString = arguments.GetString("Url")?.Trim();

            if (string.IsNullOrEmpty(urlString))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("URL cannot be empty."));
            }

            if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Task.FromResult(SkyweaverToolResult.Failure("Invalid URL. Only absolute URLs starting with http:// or https:// are allowed."));
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                };

                using var process = Process.Start(startInfo);

                return Task.FromResult(SkyweaverToolResult.Success($"Successfully opened URL: {uri.AbsoluteUri}"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenUrlTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to open URL: {ex.Message}"));
            }
        }
    }
}
