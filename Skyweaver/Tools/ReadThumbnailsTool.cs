using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class ReadThumbnailsTool : ISkyweaverTool
    {
        public const string ToolName = "ReadThumbnails";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Reads up to 75 images, generating up to 3 thumbnail sheets (5x5 grid per sheet) and embedding them into the tool return as preserved resources.",
            "Image",
            [
                new SkyweaverToolParameterDefinition(
                    "Paths",
                    "A pipe-separated (|) list of image file paths to read. Maximum 75 paths allowed.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ]);

        public SkyweaverToolDefinition Definition => s_definition;

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            var rawPaths = arguments.GetString("Paths");
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return SkyweaverToolResult.Failure("Paths parameter is required and cannot be empty.");
            }

            var paths = rawPaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.Length > 75)
            {
                return SkyweaverToolResult.Failure("A maximum of 75 images can be read concurrently.");
            }

            var validPaths = new List<string>();
            var errors = new List<string>();

            // Resolve paths and check if they exist
            foreach (var p in paths)
            {
                try
                {
                    var resolvedPath = ToolFileSystemHelper.ResolvePath(p, context.WorkspacePath);
                    if (File.Exists(resolvedPath))
                    {
                        validPaths.Add(resolvedPath);
                    }
                    else
                    {
                        errors.Add($"File does not exist: {p}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Error resolving path {p}: {ex.Message}");
                }
            }

            if (validPaths.Count == 0)
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to read any valid images. Errors:\n{string.Join("\n", errors)}");
            }

            var thumbnailSheets = new List<string>();

            try
            {
                var tcs = new TaskCompletionSource<List<string>>();
                var thread = new Thread(() =>
                {
                    try
                    {
                        tcs.SetResult(CreateThumbnailSheets(validPaths));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();

                thumbnailSheets = await tcs.Task;
            }
            catch (Exception ex)
            {
                errors.Add($"Error generating thumbnails: {ex.Message}");
            }

            var builder = new StringBuilder();
            foreach (var sheetPath in thumbnailSheets)
            {
                builder.AppendLine($"<SkyweaverPreservedContent><Image Path=\"{SecurityElement.Escape(sheetPath)}\" /></SkyweaverPreservedContent>");
            }

            if (thumbnailSheets.Count == 0)
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to generate any thumbnails. Errors:\n{string.Join("\n", errors)}");
            }

            var message = builder.ToString();
            if (errors.Count > 0)
            {
                message += $"\nNote: Some issues occurred:\n{string.Join("\n", errors)}";
            }

            return SkyweaverToolResult.Success(message);
        }

        private List<string> CreateThumbnailSheets(List<string> imagePaths)
        {
            var generatedSheets = new List<string>();
            const int imagesPerSheet = 25; // 5x5
            const int columns = 5;
            const int rows = 5;
            const int cellWidth = 256;
            const int cellHeight = 256;
            const int sheetWidth = columns * cellWidth;
            const int sheetHeight = rows * cellHeight;

            int totalSheets = (int)Math.Ceiling(imagePaths.Count / (double)imagesPerSheet);
            int sheetsToGenerate = Math.Min(3, totalSheets);

            for (int sheetIndex = 0; sheetIndex < sheetsToGenerate; sheetIndex++)
            {
                int startIndex = sheetIndex * imagesPerSheet;
                int count = Math.Min(imagesPerSheet, imagePaths.Count - startIndex);
                var currentBatch = imagePaths.Skip(startIndex).Take(count).ToList();

                var drawingVisual = new DrawingVisual();
                using (var drawingContext = drawingVisual.RenderOpen())
                {
                    // Draw white background
                    drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, sheetWidth, sheetHeight));

                    for (int i = 0; i < currentBatch.Count; i++)
                    {
                        try
                        {
                            var bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.UriSource = new Uri(currentBatch[i], UriKind.Absolute);
                            bitmapImage.EndInit();
                            bitmapImage.Freeze(); // Needed if passed between threads, though we are doing all on UI thread here

                            int col = i % columns;
                            int row = i / columns;

                            double x = col * cellWidth;
                            double y = row * cellHeight;

                            // Calculate aspect ratio preserving rect
                            double scaleX = (double)cellWidth / bitmapImage.PixelWidth;
                            double scaleY = (double)cellHeight / bitmapImage.PixelHeight;
                            double scale = Math.Min(scaleX, scaleY);

                            double newWidth = bitmapImage.PixelWidth * scale;
                            double newHeight = bitmapImage.PixelHeight * scale;

                            double offsetX = x + (cellWidth - newWidth) / 2;
                            double offsetY = y + (cellHeight - newHeight) / 2;

                            drawingContext.DrawImage(bitmapImage, new Rect(offsetX, offsetY, newWidth, newHeight));
                        }
                        catch
                        {
                            // Ignore individual image load failures in thumbnail sheet
                        }
                    }
                }

                var renderTargetBitmap = new RenderTargetBitmap(sheetWidth, sheetHeight, 96, 96, PixelFormats.Pbgra32);
                renderTargetBitmap.Render(drawingVisual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));

                string tempPath = Path.Combine(Path.GetTempPath(), $"Skyweaver_Thumbnails_{Guid.NewGuid():N}.png");
                using (var fileStream = new FileStream(tempPath, FileMode.Create))
                {
                    encoder.Save(fileStream);
                }

                generatedSheets.Add(tempPath);
            }

            return generatedSheets;
        }
    }
}
