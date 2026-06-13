using System.IO;
using System.Security;
using System.Text;
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
            "Reads up to 75 images, generates up to 3 thumbnail sheets (5x5 grid per sheet), and embeds those sheets into the tool return as preserved resources. Paths must point to real decodable image files.",
            "Image",
            [
                new SkyweaverToolParameterDefinition(
                    "Paths",
                    "A pipe-separated (|) list of image file paths to read. Maximum 75 paths allowed.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["multimodal"]);

        public SkyweaverToolDefinition Definition => s_definition;

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawPaths = arguments.GetString("Paths");
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return SkyweaverToolResult.Failure(
                    "Paths parameter is required and cannot be empty.",
                    BuildData(0, 0, 0, 0, ["Paths parameter is required and cannot be empty."]));
            }

            var paths = rawPaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.Length == 0)
            {
                return SkyweaverToolResult.Failure(
                    "At least one image path is required.",
                    BuildData(0, 0, 0, 0, ["At least one image path is required."]));
            }

            if (paths.Length > 75)
            {
                return SkyweaverToolResult.Failure(
                    "A maximum of 75 image paths is allowed.",
                    BuildData(paths.Length, 0, 0, 0, ["A maximum of 75 image paths is allowed."]));
            }

            var errors = new List<string>();
            var validatedImages = ToolImageSupport.ResolveAndValidateImagePaths(
                paths,
                context.WorkspacePath,
                cancellationToken,
                errors);

            if (validatedImages.Count == 0)
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to read any valid images. Errors:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                    BuildData(paths.Length, 0, 0, 0, errors));
            }

            ThumbnailSheetGenerationResult generationResult;
            try
            {
                generationResult = await GenerateThumbnailSheetsAsync(validatedImages, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedImageException(ex))
            {
                errors.Add($"Error generating thumbnails: {ex.Message}");
                return SkyweaverToolResult.Failure(
                    $"Failed to generate any thumbnails. Errors:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                    BuildData(paths.Length, validatedImages.Count, 0, 0, errors));
            }

            foreach (var generationError in generationResult.Errors)
            {
                errors.Add(generationError);
            }

            if (generationResult.SheetPaths.Count == 0 || generationResult.ImagesRendered == 0)
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to generate any thumbnails. Errors:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                    BuildData(paths.Length, validatedImages.Count, generationResult.SheetPaths.Count, generationResult.ImagesRendered, errors));
            }

            var builder = new StringBuilder();
            foreach (var sheetPath in generationResult.SheetPaths)
            {
                builder.AppendLine($"<PreservedContent><Image Path=\"{SecurityElement.Escape(sheetPath)}\" /></PreservedContent>");
            }

            if (errors.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Note: Some images or sheets could not be generated:");
                foreach (var error in errors)
                {
                    builder.AppendLine(error);
                }
            }

            return SkyweaverToolResult.Success(
                builder.ToString().TrimEnd(),
                BuildData(paths.Length, validatedImages.Count, generationResult.SheetPaths.Count, generationResult.ImagesRendered, errors));
        }

        private static Task<ThumbnailSheetGenerationResult> GenerateThumbnailSheetsAsync(
            IReadOnlyList<ValidatedImagePath> imagePaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completionSource = new TaskCompletionSource<ThumbnailSheetGenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    completionSource.TrySetResult(CreateThumbnailSheets(imagePaths, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    completionSource.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return completionSource.Task;
        }

        private static ThumbnailSheetGenerationResult CreateThumbnailSheets(
            IReadOnlyList<ValidatedImagePath> imagePaths,
            CancellationToken cancellationToken)
        {
            const int imagesPerSheet = 25;
            const int columns = 5;
            const int rows = 5;
            const int cellWidth = 256;
            const int cellHeight = 256;
            const int sheetWidth = columns * cellWidth;
            const int sheetHeight = rows * cellHeight;

            var generatedSheets = new List<string>();
            var errors = new List<string>();
            var imagesRendered = 0;

            var totalSheets = (int)Math.Ceiling(imagePaths.Count / (double)imagesPerSheet);
            var sheetsToGenerate = Math.Min(3, totalSheets);

            for (var sheetIndex = 0; sheetIndex < sheetsToGenerate; sheetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startIndex = sheetIndex * imagesPerSheet;
                var count = Math.Min(imagesPerSheet, imagePaths.Count - startIndex);
                var currentBatch = imagePaths.Skip(startIndex).Take(count).ToList();

                var drawingVisual = new DrawingVisual();
                var renderedInSheet = 0;
                using (var drawingContext = drawingVisual.RenderOpen())
                {
                    drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, sheetWidth, sheetHeight));

                    for (var index = 0; index < currentBatch.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var frame = LoadFrozenFrame(currentBatch[index].ResolvedPath);
                            var column = index % columns;
                            var row = index / columns;
                            var x = column * cellWidth;
                            var y = row * cellHeight;

                            var scaleX = (double)cellWidth / frame.PixelWidth;
                            var scaleY = (double)cellHeight / frame.PixelHeight;
                            var scale = Math.Min(scaleX, scaleY);

                            var newWidth = frame.PixelWidth * scale;
                            var newHeight = frame.PixelHeight * scale;
                            var offsetX = x + (cellWidth - newWidth) / 2;
                            var offsetY = y + (cellHeight - newHeight) / 2;

                            drawingContext.DrawImage(frame, new Rect(offsetX, offsetY, newWidth, newHeight));
                            renderedInSheet++;
                            imagesRendered++;
                        }
                        catch (Exception ex) when (IsExpectedImageException(ex))
                        {
                            errors.Add($"Failed to draw image '{currentBatch[index].ResolvedPath}': {ex.Message}");
                        }
                    }
                }

                if (renderedInSheet == 0)
                {
                    errors.Add($"Skipped thumbnail sheet {sheetIndex + 1} because none of its images could be rendered.");
                    continue;
                }

                var renderTargetBitmap = new RenderTargetBitmap(sheetWidth, sheetHeight, 96, 96, PixelFormats.Pbgra32);
                renderTargetBitmap.Render(drawingVisual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));

                var tempPath = Path.Combine(Path.GetTempPath(), $"Skyweaver_Thumbnails_{Guid.NewGuid():N}.png");
                using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(fileStream);
                }

                generatedSheets.Add(tempPath);
            }

            return new ThumbnailSheetGenerationResult(generatedSheets, errors, imagesRendered);
        }

        private static BitmapSource LoadFrozenFrame(string resolvedPath)
        {
            using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault()
                ?? throw new InvalidOperationException("The file could not be decoded into an image frame.");
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                throw new InvalidOperationException("The decoded image has invalid dimensions.");
            }

            frame.Freeze();
            return frame;
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            int requestedCount,
            int validatedCount,
            int sheetCount,
            int imagesRendered,
            IReadOnlyList<string> errors)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["requestedCount"] = requestedCount,
                ["validatedCount"] = validatedCount,
                ["sheetCount"] = sheetCount,
                ["imagesRendered"] = imagesRendered,
                ["errorCount"] = errors.Count,
                ["errors"] = errors.ToArray()
            };
        }

        private static bool IsExpectedImageException(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or FileFormatException
                or NotSupportedException
                or ArgumentException
                or InvalidOperationException;
        }

        private sealed record ThumbnailSheetGenerationResult(
            IReadOnlyList<string> SheetPaths,
            IReadOnlyList<string> Errors,
            int ImagesRendered);
    }
}
