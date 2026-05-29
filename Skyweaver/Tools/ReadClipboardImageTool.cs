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
            "Reads image content currently copied to the system clipboard.",
            "Image", // icon name
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Reads image content currently copied to the system clipboard. The image is saved locally and returned as a preserved resource, which you can analyze if you are capable of multimodal image understanding.";
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

                    if (ClipboardAccessService.TryGetImage(out var image, out var error))
                    {
                        if (image == null)
                        {
                            tcs.TrySetResult(SkyweaverToolResult.Failure("The clipboard does not contain an image."));
                            return;
                        }

                        var tempFilePath = Path.Combine(Path.GetTempPath(), $"ClipboardImage_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(image));
                            encoder.Save(fileStream);
                        }

                        var xmlReturn = $"<SkyweaverPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(tempFilePath)}\" /></SkyweaverPreservedContent>\nImage read successfully.";
                        tcs.TrySetResult(SkyweaverToolResult.Success(xmlReturn));
                    }
                    else
                    {
                        tcs.TrySetResult(SkyweaverToolResult.Failure($"Failed to read image from clipboard: {error}"));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ReadClipboardImageTool execution failed: {ex}");
                    tcs.TrySetResult(SkyweaverToolResult.Failure($"Failed to read image from clipboard: {ex.Message}"));
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
