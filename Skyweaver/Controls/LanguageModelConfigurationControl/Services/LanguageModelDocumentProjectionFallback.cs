using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Collections.Concurrent;
using WpfImaging = System.Windows.Media.Imaging;
using WpfMedia = System.Windows.Media;
using Docnet.Core;
using Docnet.Core.Models;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Details;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    internal static class LanguageModelDocumentProjectionFallback
    {
        private const int PdfRenderDpi = 144;
        private const int TextRenderWidth = 1440;
        private const int TextRenderHeight = 1920;
        private const int TextRenderMargin = 72;
        private const int MaxTextRenderPages = 80;
        private const string ProjectionFolderName = "DocumentProjections";

        private static readonly object s_ocrLock = new();
        private static PaddleOcrAll? s_ocr;

        private static PaddleOcrAll GetOcrInstance()
        {
            if (s_ocr == null)
            {
                lock (s_ocrLock)
                {
                    if (s_ocr == null)
                    {
                        s_ocr = CreatePaddleOcr();
                    }
                }
            }
            return s_ocr;
        }

        public static async Task<IReadOnlyList<LanguageModelChatContentBlock>> ProjectDocumentAsync(
            LanguageModelChatContentBlock block,
            string preservedContentXml,
            bool imageInputEnabled,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(block);

            return await Task.Run(async () =>
            {
                var path = block.ResourcePath ?? block.Content;
                await ReportProgressAsync(
                    block,
                    path,
                    "Preparing",
                    "Preparing document projection.",
                    progressCallback,
                    cancellationToken).ConfigureAwait(false);

                if (!TryPrepareDocumentPath(path, out var localPath, out var failureBlock))
                {
                    return [CreatePreservedText(preservedContentXml, failureBlock)];
                }

                var extension = Path.GetExtension(localPath).ToLowerInvariant();
                var isPdf = extension == ".pdf";
                var isOffice = extension is ".doc" or ".docx" or ".ppt" or ".pptx" or ".xls" or ".xlsx";
                var isText = extension is ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".html" or ".htm" || IsTextMediaType(block.MediaType);

                if (isPdf)
                {
                    if (imageInputEnabled)
                    {
                        return await ProjectPdfAsImagesAsync(block, localPath, preservedContentXml, progressCallback, cancellationToken).ConfigureAwait(false);
                    }
                    return await ProjectPdfAsOcrAsync(block, localPath, preservedContentXml, progressCallback, cancellationToken).ConfigureAwait(false);
                }
                else if (isOffice)
                {
                    return await ProjectOfficeDocumentAsync(block, localPath, preservedContentXml, progressCallback, cancellationToken).ConfigureAwait(false);
                }
                else if (isText)
                {
                    return await ProjectTextDocumentAsync(block, localPath, preservedContentXml, progressCallback, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        return await ProjectTextDocumentAsync(block, localPath, preservedContentXml, progressCallback, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return [CreatePreservedText(
                            preservedContentXml,
                            CreateProjectionNotice(
                                "DocumentProjectionFailed",
                                localPath,
                                $"Unsupported document format: {extension}. {ex.Message}"))];
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>> ProjectPdfAsImagesAsync(
            LanguageModelChatContentBlock block,
            string localPath,
            string preservedContentXml,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                var pageImagePaths = await RenderDocumentPagesAsync(
                        localPath,
                        block.MediaType,
                        block,
                        progressCallback,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (pageImagePaths.Count == 0)
                {
                    return [CreatePreservedText(
                        preservedContentXml,
                        CreateProjectionNotice(
                            "DocumentImageProjectionUnavailable",
                            localPath,
                            "Skyweaver could not render any pages from this document."))];
                }

                var blocks = new List<LanguageModelChatContentBlock>(pageImagePaths.Count + 1)
                {
                    CreatePreservedText(
                        preservedContentXml,
                        CreateProjectionNotice(
                            "DocumentImageProjection",
                            localPath,
                            $"Document input is disabled, so Skyweaver rendered {pageImagePaths.Count} page image(s) and passed them through image input."))
                };

                blocks.AddRange(pageImagePaths.Select(path => LanguageModelChatContentBlock.CreateImage(path, "image/png")));
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Ready",
                    $"Rendered {pageImagePaths.Count} page image(s).",
                    progressCallback,
                    cancellationToken,
                    pageImagePaths.Count,
                    pageImagePaths.Count,
                    1d,
                    isCompleted: true).ConfigureAwait(false);
                return blocks;
            }
            catch (Exception ex) when (IsProjectionException(ex))
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Failed",
                    $"Document image projection failed: {ex.Message}",
                    progressCallback,
                    cancellationToken,
                    isCompleted: true).ConfigureAwait(false);
                return [CreatePreservedText(
                    preservedContentXml,
                    CreateProjectionNotice(
                        "DocumentImageProjectionFailed",
                        localPath,
                        $"Skyweaver could not render this document to images: {ex.Message}"))];
            }
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>> ProjectPdfAsOcrAsync(
            LanguageModelChatContentBlock block,
            string localPath,
            string preservedContentXml,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                var cachedOcr = TryGetCachedOcr(localPath);
                if (cachedOcr != null)
                {
                    await ReportProgressAsync(
                        block,
                        localPath,
                        "Ready",
                        "Using cached OCR projection.",
                        progressCallback,
                        cancellationToken,
                        isCompleted: true).ConfigureAwait(false);
                    return [CreatePreservedText(preservedContentXml, cachedOcr)];
                }

                var pageImagePaths = await RenderDocumentPagesAsync(
                        localPath,
                        block.MediaType,
                        block,
                        progressCallback,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (pageImagePaths.Count == 0)
                {
                    return [CreatePreservedText(
                        preservedContentXml,
                        CreateProjectionNotice(
                            "DocumentOcrProjectionUnavailable",
                            localPath,
                            "Skyweaver could not render any pages for OCR."))];
                }

                var pageTexts = new string[pageImagePaths.Count];
                for (var i = 0; i < pageImagePaths.Count; i++)
                {
                    var index = i;
                    cancellationToken.ThrowIfCancellationRequested();

                    var cachedOcrPath = pageImagePaths[index] + ".ocr.txt";
                    string pageText;

                    if (File.Exists(cachedOcrPath))
                    {
                        await ReportProgressAsync(
                            block,
                            localPath,
                            "OCR",
                            $"Loading OCR result from cache for page {index + 1} of {pageImagePaths.Count}.",
                            progressCallback,
                            cancellationToken,
                            index,
                            pageImagePaths.Count,
                            pageImagePaths.Count == 0 ? null : index / (double)pageImagePaths.Count,
                            activeItems: [Path.GetFileName(pageImagePaths[index])]).ConfigureAwait(false);

                        pageText = await File.ReadAllTextAsync(cachedOcrPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReportProgressAsync(
                            block,
                            localPath,
                            "OCR",
                            $"Running PaddleOCR on page {index + 1} of {pageImagePaths.Count}.",
                            progressCallback,
                            cancellationToken,
                            index,
                            pageImagePaths.Count,
                            pageImagePaths.Count == 0 ? null : index / (double)pageImagePaths.Count,
                            activeItems: [Path.GetFileName(pageImagePaths[index])]).ConfigureAwait(false);

                        pageText = RunPaddleOcr(pageImagePaths[index]);

                        try
                        {
                            await File.WriteAllTextAsync(cachedOcrPath, pageText, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    pageTexts[index] = pageText;

                    await ReportProgressAsync(
                        block,
                        localPath,
                        "OCR",
                        $"Completed OCR for page {index + 1} of {pageImagePaths.Count}.",
                        progressCallback,
                        cancellationToken,
                        index + 1,
                        pageImagePaths.Count,
                        (index + 1) / (double)pageImagePaths.Count).ConfigureAwait(false);
                }

                var builder = new StringBuilder();
                builder.AppendLine("<DocumentOcrProjection>");
                builder.AppendLine($"  <Source Path=\"{Escape(localPath)}\" MediaType=\"{Escape(block.MediaType)}\" Pages=\"{pageImagePaths.Count.ToString(CultureInfo.InvariantCulture)}\" />");

                for (var index = 0; index < pageImagePaths.Count; index++)
                {
                    builder.AppendLine($"  <Page Index=\"{(index + 1).ToString(CultureInfo.InvariantCulture)}\" ImagePath=\"{Escape(pageImagePaths[index])}\">");
                    builder.AppendLine(Escape(pageTexts[index]));
                    builder.AppendLine("  </Page>");
                }

                builder.AppendLine("</DocumentOcrProjection>");
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Ready",
                    $"OCR completed for {pageImagePaths.Count} page(s).",
                    progressCallback,
                    cancellationToken,
                    pageImagePaths.Count,
                    pageImagePaths.Count,
                    1d,
                    isCompleted: true).ConfigureAwait(false);

                var finalProjection = builder.ToString();
                TrySaveOcrCache(localPath, finalProjection);
                return [CreatePreservedText(preservedContentXml, finalProjection)];
            }
            catch (Exception ex) when (IsProjectionException(ex))
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Failed",
                    $"Document OCR projection failed: {ex.Message}",
                    progressCallback,
                    cancellationToken,
                    isCompleted: true).ConfigureAwait(false);
                return [CreatePreservedText(
                    preservedContentXml,
                    CreateProjectionNotice(
                        "DocumentOcrProjectionFailed",
                        localPath,
                        $"Skyweaver could not OCR this document with PaddlePaddle: {ex.Message}"))];
            }
        }

        private static string? TryGetCachedOcr(string localPath)
        {
            try
            {
                var directory = DocumentProjectionContext.CurrentResourcesPath;
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = Path.GetDirectoryName(localPath);
                }
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return null;
                }

                var sourceStamp = GetSourceStamp(localPath);
                var stampHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sourceStamp)))[..16];
                var cacheFile = Path.Combine(directory, ProjectionFolderName, $"{Path.GetFileNameWithoutExtension(localPath)}-{stampHash}-ocr.xml");
                if (File.Exists(cacheFile))
                {
                    return File.ReadAllText(cacheFile, Encoding.UTF8);
                }
            }
            catch
            {
            }
            return null;
        }

        private static void TrySaveOcrCache(string localPath, string content)
        {
            try
            {
                var directory = DocumentProjectionContext.CurrentResourcesPath;
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = Path.GetDirectoryName(localPath);
                }
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                var sourceStamp = GetSourceStamp(localPath);
                var stampHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sourceStamp)))[..16];
                var cacheFolder = Path.Combine(directory, ProjectionFolderName);
                Directory.CreateDirectory(cacheFolder);
                var cacheFile = Path.Combine(cacheFolder, $"{Path.GetFileNameWithoutExtension(localPath)}-{stampHash}-ocr.xml");
                File.WriteAllText(cacheFile, content, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>> ProjectOfficeDocumentAsync(
            LanguageModelChatContentBlock block,
            string localPath,
            string preservedContentXml,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Decoding",
                    "Converting Office document to Markdown using MarkItDown.",
                    progressCallback,
                    cancellationToken).ConfigureAwait(false);

                var converter = new MarkItDown.MarkItDownClient();
                await using var result = await converter.ConvertAsync(localPath, cancellationToken).ConfigureAwait(false);
                var markdownText = result.Markdown ?? string.Empty;

                var builder = new StringBuilder();
                builder.AppendLine("<DocumentMarkdownProjection>");
                builder.AppendLine($"  <Source Path=\"{Escape(localPath)}\" MediaType=\"{Escape(block.MediaType)}\" />");
                builder.AppendLine(markdownText);
                builder.AppendLine("</DocumentMarkdownProjection>");

                await ReportProgressAsync(
                    block,
                    localPath,
                    "Ready",
                    "Office document conversion completed.",
                    progressCallback,
                    cancellationToken,
                    isCompleted: true).ConfigureAwait(false);

                return [CreatePreservedText(preservedContentXml, builder.ToString())];
            }
            catch (Exception ex) when (IsProjectionException(ex))
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Failed",
                    $"Office document conversion failed: {ex.Message}",
                    progressCallback,
                    cancellationToken,
                    isCompleted: true).ConfigureAwait(false);

                return [CreatePreservedText(
                    preservedContentXml,
                    CreateProjectionNotice(
                        "DocumentOfficeProjectionFailed",
                        localPath,
                        $"Skyweaver could not convert this Office document to Markdown: {ex.Message}"))];
            }
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>> ProjectTextDocumentAsync(
            LanguageModelChatContentBlock block,
            string localPath,
            string preservedContentXml,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Decoding",
                    "Reading text document content.",
                    progressCallback,
                    cancellationToken).ConfigureAwait(false);

                var text = await File.ReadAllTextAsync(localPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

                var builder = new StringBuilder();
                builder.AppendLine("<DocumentTextProjection>");
                builder.AppendLine($"  <Source Path=\"{Escape(localPath)}\" MediaType=\"{Escape(block.MediaType)}\" />");
                builder.AppendLine(text);
                builder.AppendLine("</DocumentTextProjection>");

                await ReportProgressAsync(
                    block,
                    localPath,
                    "Ready",
                    "Text document projection completed.",
                    progressCallback,
                    cancellationToken,
                    isCompleted: true).ConfigureAwait(false);

                return [CreatePreservedText(preservedContentXml, builder.ToString())];
            }
            catch (Exception ex) when (IsProjectionException(ex))
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Failed",
                    $"Text document projection failed: {ex.Message}",
                    progressCallback,
                    cancellationToken,
                    isCompleted: true).ConfigureAwait(false);

                return [CreatePreservedText(
                    preservedContentXml,
                    CreateProjectionNotice(
                        "DocumentTextProjectionFailed",
                        localPath,
                        $"Skyweaver could not project this text document: {ex.Message}"))];
            }
        }

        private static bool TryPrepareDocumentPath(
            string? path,
            out string localPath,
            out string failureBlock)
        {
            localPath = string.Empty;
            failureBlock = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                failureBlock = CreateProjectionNotice(
                    "DocumentProjectionFailed",
                    string.Empty,
                    "Document input is disabled, but the preserved document block did not include a path.");
                return false;
            }

            var normalizedPath = path.Trim();
            if (Uri.TryCreate(normalizedPath, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile)
                {
                    failureBlock = CreateProjectionNotice(
                        "DocumentProjectionRemoteUnsupported",
                        normalizedPath,
                        "Document input is disabled, and Skyweaver can only render or OCR local document files.");
                    return false;
                }

                normalizedPath = uri.LocalPath;
            }

            if (!File.Exists(normalizedPath))
            {
                failureBlock = CreateProjectionNotice(
                    "DocumentProjectionFileMissing",
                    normalizedPath,
                    "Document input is disabled, but the document file was not found.");
                return false;
            }

            localPath = Path.GetFullPath(normalizedPath);
            return true;
        }

        private static async Task<IReadOnlyList<string>> RenderDocumentPagesAsync(
            string localPath,
            string? mediaType,
            LanguageModelChatContentBlock block,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(localPath).ToLowerInvariant();
            if (extension == ".pdf")
            {
                return await RenderPdfPagesAsync(localPath, block, progressCallback, cancellationToken).ConfigureAwait(false);
            }
            throw new NotSupportedException($"Unsupported document rendering type: {extension}");
        }

        private static async Task<IReadOnlyList<string>> RenderPdfPagesAsync(
            string localPath,
            LanguageModelChatContentBlock block,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken)
        {
            var outputFolder = EnsureProjectionFolder(localPath, "pdf-pages");
            var pdfStamp = GetSourceStamp(localPath);
            var stampPath = Path.Combine(outputFolder, ".source-stamp");
            var existingPages = GetExistingPageImages(outputFolder);

            await ReportProgressAsync(
                block,
                localPath,
                "Decoding",
                "Opening PDF document.",
                progressCallback,
                cancellationToken).ConfigureAwait(false);
            using var docReader = DocLib.Instance.GetDocReader(localPath, new PageDimensions(PdfRenderDpi / 72.0));
            var pageCount = docReader.GetPageCount();
            if (existingPages.Count > 0 &&
                existingPages.Count == pageCount &&
                File.Exists(stampPath) &&
                string.Equals(File.ReadAllText(stampPath), pdfStamp, StringComparison.Ordinal))
            {
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Decoding",
                    $"Using {existingPages.Count} cached rendered page image(s).",
                    progressCallback,
                    cancellationToken,
                    existingPages.Count,
                    existingPages.Count,
                    1d).ConfigureAwait(false);
                return existingPages;
            }

            ClearPngFiles(outputFolder);

            var renderedPages = new List<string>(pageCount);
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Decoding",
                    $"Rendering PDF page {pageIndex + 1} of {pageCount}.",
                    progressCallback,
                    cancellationToken,
                    pageIndex,
                    pageCount,
                    pageCount == 0 ? null : pageIndex / (double)pageCount).ConfigureAwait(false);
                using var pageReader = docReader.GetPageReader(pageIndex);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                var bgraBytes = pageReader.GetImage();
                var outputPath = Path.Combine(outputFolder, BuildPageImageFileName(pageIndex));
                SaveBgraPng(bgraBytes, width, height, outputPath);
                renderedPages.Add(outputPath);
                await ReportProgressAsync(
                    block,
                    localPath,
                    "Decoding",
                    $"Rendered PDF page {pageIndex + 1} of {pageCount}.",
                    progressCallback,
                    cancellationToken,
                    pageIndex + 1,
                    pageCount,
                    (pageIndex + 1) / (double)pageCount).ConfigureAwait(false);
            }

            File.WriteAllText(stampPath, pdfStamp);
            return renderedPages;
        }



        private static string RunPaddleOcr(string imagePath)
        {
            var ocr = GetOcrInstance();
            lock (s_ocrLock)
            {
                using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
                if (source.Empty())
                {
                    return string.Empty;
                }

                var result = ocr.Run(source);
                return result.Text ?? string.Empty;
            }
        }

        private static PaddleOcrAll CreatePaddleOcr()
        {
            var model = CreateLocalV5ChineseModel();
            return new PaddleOcrAll(model, PaddleDevice.Blas())
            {
                AllowRotateDetection = true,
                Enable180Classification = false
            };
        }

        private static FullOcrModel CreateLocalV5ChineseModel()
        {
            var assembly = typeof(Sdcb.PaddleOCR.Models.LocalV5.KnownModels).Assembly;
            var modelFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Skyweaver",
                "PaddleOCR",
                "LocalV5");
            Directory.CreateDirectory(modelFolder);

            var detectionFolder = ExtractPaddleModel(assembly, modelFolder, "mobile_zh_det");
            var recognitionFolder = ExtractPaddleModel(assembly, modelFolder, "mobile_zh_rec");
            var labelsPath = EnsureRecognitionLabels(assembly, modelFolder);
            return new FullOcrModel(
                DetectionModel.FromDirectory(detectionFolder, ModelVersion.V5),
                new FileRecognizationModel(recognitionFolder, labelsPath, ModelVersion.V5));
        }

        private static string EnsureRecognitionLabels(
            System.Reflection.Assembly assembly,
            string modelFolder)
        {
            var labelsPath = Path.Combine(modelFolder, "mobile_zh_rec_labels.txt");
            if (File.Exists(labelsPath) && new FileInfo(labelsPath).Length > 0)
            {
                return labelsPath;
            }

            var ymlResourceName = "Sdcb.PaddleOCR.Models.LocalV5.models.mobile_zh_rec.inference.yml";
            using var input = assembly.GetManifestResourceStream(ymlResourceName)
                ?? throw new InvalidOperationException($"Missing embedded PaddleOCR model resource: {ymlResourceName}");
            using var reader = new StreamReader(input, Encoding.UTF8);
            var labels = ExtractRecognitionLabels(reader.ReadToEnd());
            if (labels.Count == 0)
            {
                throw new InvalidOperationException("PaddleOCR LocalV5 recognition labels were not found in the embedded model metadata.");
            }

            File.WriteAllLines(labelsPath, labels);
            return labelsPath;
        }

        private static IReadOnlyList<string> ExtractRecognitionLabels(string ymlText)
        {
            var labels = new List<string>();
            var inDictionary = false;
            foreach (var rawLine in ymlText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmedStart = rawLine.TrimStart();
                if (!inDictionary)
                {
                    inDictionary = string.Equals(trimmedStart.TrimEnd(), "character_dict:", StringComparison.Ordinal);
                    continue;
                }

                if (trimmedStart.StartsWith("-", StringComparison.Ordinal))
                {
                    labels.Add(trimmedStart.Length > 1 && trimmedStart[1] == ' '
                        ? trimmedStart[2..]
                        : trimmedStart[1..]);
                    continue;
                }

                if (trimmedStart.Length > 0)
                {
                    break;
                }
            }

            return labels;
        }

        private static string ExtractPaddleModel(
            System.Reflection.Assembly assembly,
            string modelRoot,
            string modelName)
        {
            var modelFolder = Path.Combine(modelRoot, modelName);
            Directory.CreateDirectory(modelFolder);

            var resourcePrefix = $"Sdcb.PaddleOCR.Models.LocalV5.models.{modelName}.";
            foreach (var resourceName in assembly.GetManifestResourceNames()
                         .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal)))
            {
                var fileName = resourceName[resourcePrefix.Length..];
                var outputPath = Path.Combine(modelFolder, fileName);
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    continue;
                }

                using var input = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Missing embedded PaddleOCR model resource: {resourceName}");
                using var output = File.Create(outputPath);
                input.CopyTo(output);
            }

            return modelFolder;
        }

        private static string EnsureProjectionFolder(
            string localPath,
            string suffix)
        {
            var directory = DocumentProjectionContext.CurrentResourcesPath;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetDirectoryName(localPath);
            }
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetTempPath();
            }

            var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(localPath)))[..16];
            var outputFolder = Path.Combine(directory, ProjectionFolderName, $"{Path.GetFileNameWithoutExtension(localPath)}-{sourceHash}-{suffix}");
            Directory.CreateDirectory(outputFolder);
            return outputFolder;
        }

        private static IReadOnlyList<string> GetExistingPageImages(string outputFolder)
        {
            return Directory.EnumerateFiles(outputFolder, "page-*.png", SearchOption.TopDirectoryOnly)
                .Where(IsReadablePng)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsReadablePng(string filePath)
        {
            try
            {
                if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                {
                    return false;
                }

                using var image = Cv2.ImRead(filePath, ImreadModes.Unchanged);
                return !image.Empty();
            }
            catch
            {
                return false;
            }
        }

        private static void ClearPngFiles(string outputFolder)
        {
            foreach (var filePath in Directory.EnumerateFiles(outputFolder, "*.png", SearchOption.TopDirectoryOnly))
            {
                File.Delete(filePath);
            }
        }

        private static string GetSourceStamp(string localPath)
        {
            var fileInfo = new FileInfo(localPath);
            return $"{fileInfo.FullName}|{fileInfo.Length.ToString(CultureInfo.InvariantCulture)}|{fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string BuildPageImageFileName(int pageIndex)
        {
            return $"page-{(pageIndex + 1).ToString("0000", CultureInfo.InvariantCulture)}.png";
        }

        private static void SaveBgraPng(
            byte[] bgraBytes,
            int width,
            int height,
            string outputPath)
        {
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"PDF page rendered an invalid image size: {width}x{height}.");
            }

            var expectedByteCount = checked(width * height * 4);
            if (bgraBytes.Length < expectedByteCount)
            {
                throw new InvalidOperationException(
                    $"PDF page rendered {bgraBytes.Length} bytes, expected at least {expectedByteCount} bytes for {width}x{height} BGRA.");
            }

            using var mat = new Mat(height, width, MatType.CV_8UC4);
            Marshal.Copy(bgraBytes, 0, mat.Data, expectedByteCount);
            if (!Cv2.ImEncode(".png", mat, out var encodedBytes) || encodedBytes.Length == 0)
            {
                throw new InvalidOperationException("OpenCV failed to encode the rendered PDF page as PNG.");
            }

            File.WriteAllBytes(outputPath, encodedBytes);
        }



        private static bool IsTextMediaType(string? mediaType)
        {
            return !string.IsNullOrWhiteSpace(mediaType) &&
                   mediaType.Trim().StartsWith("text/", StringComparison.OrdinalIgnoreCase);
        }

        private static LanguageModelChatContentBlock CreatePreservedText(
            string preservedContentXml,
            string projectionXml)
        {
            var preservedXml = string.IsNullOrWhiteSpace(preservedContentXml)
                ? string.Empty
                : preservedContentXml.Trim();

            var projection = string.IsNullOrWhiteSpace(projectionXml)
                ? string.Empty
                : projectionXml.Trim();

            var content = string.Join(
                Environment.NewLine,
                new[] { preservedXml, projection }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return LanguageModelChatContentBlock.CreateHostPreservedContent(content);
        }

        private static string CreateProjectionNotice(
            string elementName,
            string? path,
            string message)
        {
            return $"<{elementName} Path=\"{Escape(path)}\">{Escape(message)}</{elementName}>";
        }

        private static async ValueTask ReportProgressAsync(
            LanguageModelChatContentBlock block,
            string? resourcePath,
            string phase,
            string statusText,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback,
            CancellationToken cancellationToken,
            int? completedItems = null,
            int? totalItems = null,
            double? progressFraction = null,
            bool isCompleted = false,
            IReadOnlyList<string>? activeItems = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (progressCallback == null)
            {
                return;
            }

            var progress = new LanguageModelMediaProcessingProgress
            {
                Kind = LanguageModelChatContentBlockKind.Document,
                ResourcePath = block.ResourcePath ?? block.Content ?? resourcePath ?? string.Empty,
                MediaType = block.MediaType,
                Phase = phase,
                StatusText = statusText,
                CompletedItems = completedItems,
                TotalItems = totalItems,
                ProgressFraction = progressFraction,
                IsCompleted = isCompleted,
                ActiveItems = activeItems ?? Array.Empty<string>()
            }.Normalize();

            await progressCallback(progress, cancellationToken).ConfigureAwait(false);
        }

        private static string Escape(string? value)
        {
            return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        }

        private static bool IsProjectionException(Exception ex)
        {
            return ex is not OperationCanceledException and not OutOfMemoryException;
        }
    }
}
