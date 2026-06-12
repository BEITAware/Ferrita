using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ReadClipboardImageTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "ReadClipboardImage";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Reads an image currently copied to the system clipboard and returns it as an embedded image resource.",
            "ClipboardImage", // icon name
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation,
            defaultToolKitKeys: ["multimodal"]);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Reads an image currently copied to the system clipboard. The image is saved locally and returned as a preserved resource, which you can analyze if you are capable of multimodal image understanding.";
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

            var tcs = new TaskCompletionSource<SkyweaverToolResult>();

            var thread = new Thread(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    if (ClipboardAccessService.TryGetImage(out BitmapSource? image, out string? errorMessage) && image != null)
                    {
                        string saveDirectory;
                        if (context.Properties.TryGetValue("resourcesFolderPath", out var resourcesFolder) && !string.IsNullOrWhiteSpace(resourcesFolder))
                        {
                            saveDirectory = resourcesFolder;
                        }
                        else if (!string.IsNullOrWhiteSpace(context.WorkspacePath))
                        {
                            saveDirectory = context.WorkspacePath;
                        }
                        else
                        {
                            saveDirectory = Path.GetTempPath();
                        }

                        try
                        {
                            if (!Directory.Exists(saveDirectory))
                            {
                                Directory.CreateDirectory(saveDirectory);
                            }
                        }
                        catch
                        {
                            saveDirectory = Path.GetTempPath();
                        }

                        var targetFilePath = Path.Combine(saveDirectory, $"ClipboardImage_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                        using (var fileStream = new FileStream(targetFilePath, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(image));
                            encoder.Save(fileStream);
                        }

                        var xmlReturn = $"<SkyweaverPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(targetFilePath)}\" /></SkyweaverPreservedContent>\nClipboard image captured successfully.";
                        tcs.TrySetResult(SkyweaverToolResult.Success(xmlReturn));
                    }
                    else
                    {
                        tcs.TrySetResult(SkyweaverToolResult.Failure(errorMessage ?? "The clipboard does not contain a valid image."));
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ReadClipboardImageTool execution failed: {ex}");
                    tcs.TrySetResult(SkyweaverToolResult.Failure($"Failed to read clipboard image: {ex.Message}"));
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            // Handle external cancellation to avoid hanging if the STA thread gets stuck
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            return tcs.Task;
        }
    }
}
