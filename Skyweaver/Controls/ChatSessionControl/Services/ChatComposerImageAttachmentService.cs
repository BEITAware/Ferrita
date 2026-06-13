using System.IO;
using System.Text;
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
            var resourceKind = ResolveResourceKind(sourceFilePath, mediaType);
            var preservedTextXml = resourceKind == "Text"
                ? PreservedTextContentXml.Build(
                    ReadTextFile(filePath),
                    Path.GetFileName(sourceFilePath),
                    filePath,
                    mediaType)
                : null;

            session.Resources.Resources.Add(new ChatSessionResourceManifestEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = resourceKind,
                Path = filePath,
                MediaType = mediaType,
                CreatedAtUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(filePath).Length
            });

            return new ChatComposerAttachmentModel(
                filePath,
                mediaType,
                Path.GetFileName(sourceFilePath),
                resourceKind == "Text" ? ChatComposerAttachmentKind.Text : null,
                preservedTextXml);
        }

        private static string ResolveResourceKind(string filePath, string mediaType)
        {
            if (IsExplicitPlainTextFile(filePath, mediaType))
            {
                return "Text";
            }

            if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                return "Audio";
            }

            if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return "Video";
            }

            return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? "Image"
                : "Document";
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
                ".mp4" => "video/mp4",
                ".mpeg" or ".mpg" => "video/mpeg",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".webm" => "video/webm",
                ".mkv" => "video/x-matroska",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".markdown" => "text/markdown",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".xaml" => "application/xaml+xml",
                ".cs" => "text/x-csharp",
                ".csproj" => "application/xml",
                ".sln" => "text/plain",
                ".props" or ".targets" => "application/xml",
                ".js" => "text/javascript",
                ".jsx" => "text/javascript",
                ".ts" => "text/typescript",
                ".tsx" => "text/typescript",
                ".css" => "text/css",
                ".scss" => "text/x-scss",
                ".less" => "text/x-less",
                ".ps1" or ".psm1" or ".psd1" => "text/x-powershell",
                ".bat" or ".cmd" => "text/x-msdos-batch",
                ".sh" => "text/x-shellscript",
                ".py" => "text/x-python",
                ".java" => "text/x-java-source",
                ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" => "text/x-c",
                ".sql" => "application/sql",
                ".ini" => "text/plain",
                ".cfg" or ".conf" => "text/plain",
                ".log" => "text/plain",
                ".yaml" or ".yml" => "application/x-yaml",
                ".toml" => "application/toml",
                ".smd" => "text/markdown",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => string.Empty
            };
        }

        private static bool IsExplicitPlainTextFile(string filePath, string mediaType)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension is ".txt" or ".md" or ".markdown" or ".csv" or ".html" or ".htm" or ".json" or ".xml"
                or ".xaml" or ".cs" or ".csproj" or ".sln" or ".props" or ".targets" or ".js" or ".jsx"
                or ".ts" or ".tsx" or ".css" or ".scss" or ".less" or ".ps1" or ".psm1" or ".psd1"
                or ".bat" or ".cmd" or ".sh" or ".py" or ".java" or ".cpp" or ".cc" or ".cxx" or ".c"
                or ".h" or ".hpp" or ".sql" or ".ini" or ".cfg" or ".conf" or ".log" or ".yaml"
                or ".yml" or ".toml" or ".smd")
            {
                return true;
            }

            return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadTextFile(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}
