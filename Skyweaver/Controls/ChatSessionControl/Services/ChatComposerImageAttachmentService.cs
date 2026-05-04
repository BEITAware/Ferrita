using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Skyweaver.Controls.ChatSessionControl.Models;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.ChatSession;

namespace Skyweaver.Controls.ChatSessionControl.Services
{
    public sealed class ChatComposerImageAttachmentService
    {
        private const string ComposerImagesFolderName = "ComposerImages";
        private const string ComposerAttachmentsFolderName = "ComposerAttachments";
        private const int MaxStoredPixelSize = 768;

        public ChatComposerAttachmentModel SavePastedImage(
            ChatSessionModel session,
            BitmapSource image)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(image);

            var resourcesFolder = ChatSessionResourceLayout.EnsureResources(session);
            var imageFolder = Path.Combine(resourcesFolder, ComposerImagesFolderName);
            Directory.CreateDirectory(imageFolder);

            var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.png";
            var filePath = Path.Combine(imageFolder, fileName);
            SaveScaledPng(image, filePath);
            session.Resources.Resources.Add(new ChatSessionResourceManifestEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = "Image",
                Path = filePath,
                MediaType = "image/png",
                CreatedAtUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(filePath).Length
            });
            return new ChatComposerAttachmentModel(filePath, "image/png", fileName);
        }

        public ChatComposerAttachmentModel SaveMediaFile(
            ChatSessionModel session,
            string sourceFilePath)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

            if (!File.Exists(sourceFilePath))
            {
                throw new FileNotFoundException("Attachment file was not found.", sourceFilePath);
            }

            var mediaType = ResolveMediaType(sourceFilePath);
            if (mediaType.Length == 0)
            {
                throw new InvalidOperationException($"Unsupported attachment type: {Path.GetExtension(sourceFilePath)}");
            }

            var resourcesFolder = ChatSessionResourceLayout.EnsureResources(session);
            var attachmentFolder = Path.Combine(resourcesFolder, ComposerAttachmentsFolderName);
            Directory.CreateDirectory(attachmentFolder);

            var extension = Path.GetExtension(sourceFilePath);
            var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{extension}";
            var filePath = Path.Combine(attachmentFolder, fileName);
            File.Copy(sourceFilePath, filePath, overwrite: false);

            session.Resources.Resources.Add(new ChatSessionResourceManifestEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "Audio" : "Image",
                Path = filePath,
                MediaType = mediaType,
                CreatedAtUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(filePath).Length
            });

            return new ChatComposerAttachmentModel(filePath, mediaType, Path.GetFileName(sourceFilePath));
        }

        private static void SaveScaledPng(BitmapSource source, string filePath)
        {
            var scaledSource = ScaleToMaxSize(source, MaxStoredPixelSize);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaledSource));

            using var stream = File.Create(filePath);
            encoder.Save(stream);
        }

        private static BitmapSource ScaleToMaxSize(BitmapSource source, int maxPixelSize)
        {
            if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return source;
            }

            var largestSide = Math.Max(source.PixelWidth, source.PixelHeight);
            if (largestSide <= maxPixelSize)
            {
                return EnsurePbgra32(source);
            }

            var scale = (double)maxPixelSize / largestSide;
            var transformed = new TransformedBitmap(
                source,
                new ScaleTransform(scale, scale));
            transformed.Freeze();
            return EnsurePbgra32(transformed);
        }

        private static BitmapSource EnsurePbgra32(BitmapSource source)
        {
            if (source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32)
            {
                if (source.CanFreeze)
                {
                    source.Freeze();
                }

                return source;
            }

            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            converted.Freeze();
            return converted;
        }

        private static string ResolveMediaType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                _ => string.Empty
            };
        }
    }
}
