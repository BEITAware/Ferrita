using System.Net;
using System.Xml.Linq;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    internal static class LanguageModelChatTransportProjection
    {
        private const string LiteralStartToken = "<SkyweaverPreservedContent";
        private const string LiteralEndToken = "</SkyweaverPreservedContent>";
        private const string EscapedStartToken = "&lt;SkyweaverPreservedContent";
        private const string EscapedEndToken = "&lt;/SkyweaverPreservedContent&gt;";

        public static IReadOnlyList<LanguageModelChatMessage> ProjectMessages(
            IReadOnlyList<LanguageModelChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            if (messages.Count == 0)
            {
                return Array.Empty<LanguageModelChatMessage>();
            }

            var projected = new LanguageModelChatMessage[messages.Count];
            for (var index = 0; index < messages.Count; index++)
            {
                projected[index] = ProjectMessage(messages[index]);
            }

            return projected;
        }

        private static LanguageModelChatMessage ProjectMessage(LanguageModelChatMessage message)
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
                if (!block.IsTextLike ||
                    string.IsNullOrEmpty(block.Content) ||
                    !MayContainPreservedContent(block.Content))
                {
                    projectedBlocks.Add(block.Clone());
                    continue;
                }

                if (!TryExpandTextLikeBlock(block, out var expandedBlocks))
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
                    IsHostInjectedTail = message.IsHostInjectedTail
                }
                : message.Clone();
        }

        private static bool TryExpandTextLikeBlock(
            LanguageModelChatContentBlock source,
            out IReadOnlyList<LanguageModelChatContentBlock> expandedBlocks)
        {
            ArgumentNullException.ThrowIfNull(source);

            var content = source.Content;
            if (string.IsNullOrEmpty(content))
            {
                expandedBlocks = [source.Clone()];
                return false;
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
                if (TryCreateNativeResourceBlock(rawFragment, isEscaped, out var resourceBlock) &&
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
                expandedBlocks = [source.Clone()];
                return false;
            }

            expandedBlocks = projectedBlocks.ToArray();
            return true;
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
                        "SkyweaverPreservedContent",
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
                    resourceBlock = LanguageModelChatContentBlock.CreateImage(path, mediaType);
                    return true;
                }

                if (string.Equals(resourceElement.Name.LocalName, "Audio", StringComparison.OrdinalIgnoreCase))
                {
                    resourceBlock = LanguageModelChatContentBlock.CreateAudio(path, mediaType);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
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
    }
}
