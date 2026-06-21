using System.Net;
using System.Xml.Linq;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.ChatSession;
using Ferrita.Services.Multimodal;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    internal static class LanguageModelChatTransportProjection
    {
        private const string LiteralStartToken = "<FerritaPreservedContent";
        private const string LiteralEndToken = "</FerritaPreservedContent>";
        private const string EscapedStartToken = "&lt;FerritaPreservedContent";
        private const string EscapedEndToken = "&lt;/FerritaPreservedContent&gt;";

        public static async Task<IReadOnlyList<LanguageModelChatMessage>> ProjectMessagesAsync(
            IReadOnlyList<LanguageModelChatMessage> messages,
            LanguageModelDefinition? model = null,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            if (messages.Count == 0)
            {
                return Array.Empty<LanguageModelChatMessage>();
            }

            var projected = new LanguageModelChatMessage[messages.Count];
            for (var index = 0; index < messages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                projected[index] = await ProjectMessageAsync(
                        messages[index],
                        model,
                        mediaProcessingProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return projected;
        }

        private static async Task<LanguageModelChatMessage> ProjectMessageAsync(
            LanguageModelChatMessage message,
            LanguageModelDefinition? model,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.ContentBlocks.Count == 0)
            {
                return message.Clone();
            }

            var changed = false;
            var projectedBlocks = new List<LanguageModelChatContentBlock>(message.ContentBlocks.Count);
            foreach (var block in message.ContentBlocks)
            {
                if (block.Kind == LanguageModelChatContentBlockKind.Image && model?.EnableImageInput == false)
                {
                    projectedBlocks.Add(LanguageModelChatContentBlock.CreateHostPreservedContent(
                        BuildPreservedResourceXml("Image", block)));
                    changed = true;
                    continue;
                }

                // 长图像自动解析：对比例超过 21:9 或 9:21 的图像进行切分
                if (block.Kind == LanguageModelChatContentBlockKind.Image &&
                    model?.EnableImageInput != false)
                {
                    var autoParseSlices = TryAutoParseImageBlock(block);
                    if (autoParseSlices != null)
                    {
                        projectedBlocks.AddRange(autoParseSlices);
                        changed = true;
                        continue;
                    }
                }

                if (block.Kind == LanguageModelChatContentBlockKind.Audio && model?.EnableAudioInput == false)
                {
                    projectedBlocks.Add(LanguageModelChatContentBlock.CreateHostPreservedContent(
                        BuildPreservedResourceXml("Audio", block)));
                    changed = true;
                    continue;
                }

                if (block.Kind == LanguageModelChatContentBlockKind.Video && model?.EnableVideoInput == false)
                {
                    projectedBlocks.Add(LanguageModelChatContentBlock.CreateHostPreservedContent(
                        BuildPreservedResourceXml("Video", block)));
                    changed = true;
                    continue;
                }

                if (block.Kind == LanguageModelChatContentBlockKind.Document && model?.EnableDocumentInput == false)
                {
                    projectedBlocks.AddRange(await LanguageModelDocumentProjectionFallback.ProjectDocumentAsync(
                            block,
                            BuildPreservedResourceXml("Document", block),
                            model.EnableImageInput,
                            mediaProcessingProgress,
                            cancellationToken)
                        .ConfigureAwait(false));
                    changed = true;
                    continue;
                }

                if (!block.IsTextLike ||
                    string.IsNullOrEmpty(block.Content) ||
                    !MayContainPreservedContent(block.Content))
                {
                    projectedBlocks.Add(block.Clone());
                    continue;
                }

                var expandedBlocks = await ExpandTextLikeBlockAsync(
                        block,
                        model,
                        mediaProcessingProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (expandedBlocks == null)
                {
                    projectedBlocks.Add(block.Clone());
                    continue;
                }

                changed = true;
                projectedBlocks.AddRange(expandedBlocks);
            }

            return changed
                ? new LanguageModelChatMessage(
                    message.Role,
                    projectedBlocks)
                {
                    AuthorName = message.AuthorName,
                    IsHostInjectedTail = message.IsHostInjectedTail,
                    ReasoningContent = message.ReasoningContent
                }
                : message.Clone();
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>?> ExpandTextLikeBlockAsync(
            LanguageModelChatContentBlock source,
            LanguageModelDefinition? model,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);

            var content = source.Content;
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            var projectedBlocks = new List<LanguageModelChatContentBlock>();
            var cursor = 0;
            var foundResource = false;

            while (cursor < content.Length)
            {
                if (!TryFindNextPreservedContent(
                        content,
                        cursor,
                        out var matchStart,
                        out var matchEnd,
                        out var isEscaped))
                {
                    AppendTextBlock(projectedBlocks, source.Kind, content[cursor..]);
                    break;
                }

                AppendTextBlock(projectedBlocks, source.Kind, content[cursor..matchStart]);

                var rawFragment = content[matchStart..matchEnd];
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedFragment = isEscaped
                    ? WebUtility.HtmlDecode(rawFragment)
                    : rawFragment;
                if (FerritaPreservedTextContentXml.TryParse(normalizedFragment, out var preservedText))
                {
                    AppendTextBlock(projectedBlocks, LanguageModelChatContentBlockKind.Text, preservedText.Text);
                    foundResource = true;
                    cursor = matchEnd;
                    continue;
                }

                var expandedPreservedDocument = await TryExpandPreservedDocumentFallbackAsync(
                    rawFragment,
                    isEscaped,
                    model,
                    mediaProcessingProgress,
                    cancellationToken).ConfigureAwait(false);
                if (expandedPreservedDocument != null &&
                    expandedPreservedDocument.Count > 0)
                {
                    projectedBlocks.AddRange(expandedPreservedDocument);
                    foundResource = true;
                }
                else if (TryCreateNativeResourceBlock(rawFragment, isEscaped, model, out var resourceBlock) &&
                    resourceBlock != null)
                {
                    projectedBlocks.Add(resourceBlock);
                    foundResource = true;
                }
                else
                {
                    AppendTextBlock(projectedBlocks, source.Kind, rawFragment);
                }

                cursor = matchEnd;
            }

            if (!foundResource)
            {
                return null;
            }

            return projectedBlocks.ToArray();
        }

        private static void AppendTextBlock(
            ICollection<LanguageModelChatContentBlock> target,
            LanguageModelChatContentBlockKind sourceKind,
            string content)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            target.Add(sourceKind == LanguageModelChatContentBlockKind.HostPreservedContent
                ? LanguageModelChatContentBlock.CreateHostPreservedContent(content)
                : LanguageModelChatContentBlock.CreateText(content));
        }

        private static bool TryFindNextPreservedContent(
            string content,
            int startIndex,
            out int matchStart,
            out int matchEnd,
            out bool isEscaped)
        {
            ArgumentNullException.ThrowIfNull(content);

            matchStart = -1;
            matchEnd = -1;
            isEscaped = false;

            var literalIndex = content.IndexOf(LiteralStartToken, startIndex, StringComparison.OrdinalIgnoreCase);
            var escapedIndex = content.IndexOf(EscapedStartToken, startIndex, StringComparison.OrdinalIgnoreCase);
            if (literalIndex < 0 && escapedIndex < 0)
            {
                return false;
            }

            isEscaped = escapedIndex >= 0 && (literalIndex < 0 || escapedIndex < literalIndex);
            matchStart = isEscaped ? escapedIndex : literalIndex;

            var endToken = isEscaped ? EscapedEndToken : LiteralEndToken;
            var endIndex = content.IndexOf(endToken, matchStart, StringComparison.OrdinalIgnoreCase);
            if (endIndex < 0)
            {
                matchStart = -1;
                return false;
            }

            matchEnd = endIndex + endToken.Length;
            return true;
        }

        private static bool TryCreateNativeResourceBlock(
            string rawFragment,
            bool isEscaped,
            LanguageModelDefinition? model,
            out LanguageModelChatContentBlock? resourceBlock)
        {
            resourceBlock = null;

            if (string.IsNullOrWhiteSpace(rawFragment))
            {
                return false;
            }

            try
            {
                var normalizedFragment = isEscaped
                    ? WebUtility.HtmlDecode(rawFragment)
                    : rawFragment;
                var preservedContent = XElement.Parse(normalizedFragment, LoadOptions.PreserveWhitespace);
                if (!string.Equals(
                        preservedContent.Name.LocalName,
                        "FerritaPreservedContent",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var resourceElement = preservedContent.Elements().SingleOrDefault();
                if (resourceElement == null)
                {
                    return false;
                }

                var path = GetAttributeValue(resourceElement, "Path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                var mediaType = GetAttributeValue(resourceElement, "MediaType")
                    ?? GetAttributeValue(resourceElement, "MimeType");
                if (string.Equals(resourceElement.Name.LocalName, "Image", StringComparison.OrdinalIgnoreCase))
                {
                    if (model?.EnableImageInput == false)
                    {
                        return false;
                    }

                    if (!LanguageModelMediaResourcePolicy.TryNormalizeMediaType(
                            LanguageModelChatContentBlockKind.Image,
                            path,
                            mediaType,
                            out var normalizedMediaType))
                    {
                        return false;
                    }

                    resourceBlock = LanguageModelChatContentBlock.CreateImage(path, normalizedMediaType);
                    return true;
                }

                if (string.Equals(resourceElement.Name.LocalName, "Audio", StringComparison.OrdinalIgnoreCase))
                {
                    if (model?.EnableAudioInput == false)
                    {
                        return false;
                    }

                    if (!LanguageModelMediaResourcePolicy.TryNormalizeMediaType(
                            LanguageModelChatContentBlockKind.Audio,
                            path,
                            mediaType,
                            out var normalizedMediaType))
                    {
                        return false;
                    }

                    resourceBlock = LanguageModelChatContentBlock.CreateAudio(path, normalizedMediaType);
                    return true;
                }

                if (string.Equals(resourceElement.Name.LocalName, "Video", StringComparison.OrdinalIgnoreCase))
                {
                    if (model?.EnableVideoInput == false)
                    {
                        return false;
                    }

                    if (!LanguageModelMediaResourcePolicy.TryNormalizeMediaType(
                            LanguageModelChatContentBlockKind.Video,
                            path,
                            mediaType,
                            out var normalizedMediaType))
                    {
                        return false;
                    }

                    resourceBlock = LanguageModelChatContentBlock.CreateVideo(path, normalizedMediaType);
                    return true;
                }

                if (string.Equals(resourceElement.Name.LocalName, "Document", StringComparison.OrdinalIgnoreCase))
                {
                    if (!LanguageModelMediaResourcePolicy.TryNormalizeMediaType(
                            LanguageModelChatContentBlockKind.Document,
                            path,
                            mediaType,
                            out var normalizedMediaType))
                    {
                        return false;
                    }

                    var documentBlock = LanguageModelChatContentBlock.CreateDocument(path, normalizedMediaType);
                    if (model?.EnableDocumentInput == false)
                    {
                        resourceBlock = LanguageModelChatContentBlock.CreateHostPreservedContent(normalizedFragment);
                        return true;
                    }

                    resourceBlock = documentBlock;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>?> TryExpandPreservedDocumentFallbackAsync(
            string rawFragment,
            bool isEscaped,
            LanguageModelDefinition? model,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress,
            CancellationToken cancellationToken)
        {
            if (model?.EnableDocumentInput != false)
            {
                return null;
            }

            try
            {
                var normalizedFragment = isEscaped
                    ? WebUtility.HtmlDecode(rawFragment)
                    : rawFragment;
                var preservedContent = XElement.Parse(normalizedFragment, LoadOptions.PreserveWhitespace);
                if (!string.Equals(
                        preservedContent.Name.LocalName,
                        "FerritaPreservedContent",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var resourceElement = preservedContent.Elements().SingleOrDefault();
                if (resourceElement == null ||
                    !string.Equals(resourceElement.Name.LocalName, "Document", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var path = GetAttributeValue(resourceElement, "Path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                var mediaType = GetAttributeValue(resourceElement, "MediaType")
                    ?? GetAttributeValue(resourceElement, "MimeType");
                if (!LanguageModelMediaResourcePolicy.TryNormalizeMediaType(
                        LanguageModelChatContentBlockKind.Document,
                        path,
                        mediaType,
                        out var normalizedMediaType))
                {
                    return null;
                }

                var documentBlock = LanguageModelChatContentBlock.CreateDocument(path, normalizedMediaType);
                return await LanguageModelDocumentProjectionFallback.ProjectDocumentAsync(
                        documentBlock,
                        normalizedFragment,
                        model.EnableImageInput,
                        mediaProcessingProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException
                or InvalidOperationException
                or ArgumentException
                or FormatException)
            {
                return null;
            }
        }

        private static string? GetAttributeValue(XElement element, string attributeName)
        {
            ArgumentNullException.ThrowIfNull(element);
            ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);

            return element.Attributes()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
        }

        private static bool MayContainPreservedContent(string content)
        {
            ArgumentNullException.ThrowIfNull(content);

            return content.IndexOf(LiteralStartToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   content.IndexOf(EscapedStartToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildPreservedResourceXml(string elementName, LanguageModelChatContentBlock block)
        {
            var path = block.ResourcePath ?? block.Content;
            var normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
            if (normalizedPath.Length == 0)
            {
                return string.Empty;
            }

            var element = new XElement(
                elementName,
                new XAttribute("Path", normalizedPath));
            if (!string.IsNullOrWhiteSpace(block.MediaType))
            {
                element.Add(new XAttribute("MediaType", block.MediaType.Trim()));
            }

            return new XElement("FerritaPreservedContent", element).ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// 尝试对图像块执行长图自动解析。
        /// 若功能未启用或图像无需切分，返回 null。
        /// </summary>
        private static IReadOnlyList<LanguageModelChatContentBlock>? TryAutoParseImageBlock(
            LanguageModelChatContentBlock block)
        {
            if (!MultimodalRuntime.Instance.GetConfiguration().EnableLongImageAutoParse)
            {
                return null;
            }

            var imagePath = block.ResourcePath ?? block.Content;
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var slicePaths = LongImageAutoParseService.TryAutoParse(imagePath.Trim());
            if (slicePaths == null || slicePaths.Count == 0)
            {
                return null;
            }

            var blocks = new List<LanguageModelChatContentBlock>(slicePaths.Count);
            foreach (var slicePath in slicePaths)
            {
                blocks.Add(LanguageModelChatContentBlock.CreateImage(slicePath, "image/png"));
            }

            return blocks;
        }
    }
}
