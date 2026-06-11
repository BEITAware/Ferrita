using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class TakeScreenshotTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "TakeScreenshot";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Captures a screenshot of the entire primary display and returns it as an embedded image resource.",
            "Monitor",
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.RequireConfirmation,
            defaultToolKitKeys: ["multimodal"]);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Captures a screenshot of the main monitor. The image is saved locally and returned as a preserved resource, which you can analyze if you are capable of multimodal image understanding.";
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

                    var screen = System.Windows.Forms.Screen.PrimaryScreen;
                    if (screen == null)
                    {
                        tcs.TrySetResult(SkyweaverToolResult.Failure("No primary screen detected."));
                        return;
                    }

                    var bounds = screen.Bounds;
                    using var bitmap = new Bitmap(bounds.Width, bounds.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

                    var tempFilePath = Path.Combine(Path.GetTempPath(), $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    bitmap.Save(tempFilePath, ImageFormat.Png);

                    var xmlReturn = $"<SkyweaverPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(tempFilePath)}\" /></SkyweaverPreservedContent>\nScreenshot captured successfully.";
                    tcs.TrySetResult(SkyweaverToolResult.Success(xmlReturn));
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TakeScreenshotTool execution failed: {ex}");
                    tcs.TrySetResult(SkyweaverToolResult.Failure($"Failed to take screenshot: {ex.Message}"));
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            return tcs.Task;
        }
    }
}
