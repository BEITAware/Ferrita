using System.IO;
using System.Text;
using Skyweaver.Controls.ChatSessionControl.Models;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Controls.ChatSessionControl.Services
{
    internal enum ChatMessageCopyFormat
    {
        Full = 0,
        Markdown = 1,
        PlainText = 2
    }

    internal static class ChatMessageClipboardExporter
    {
        public static string Build(ChatMessageModel? message, ChatMessageCopyFormat format)
        {
            if (message == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var part in message.Parts.Where(part => ShouldIncludePart(part, format)))
            {
                var partText = BuildPartText(part, format);
                if (string.IsNullOrWhiteSpace(partText))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.Append(partText.TrimEnd());
            }

            return builder.ToString().Trim();
        }

        private static bool ShouldIncludePart(ChatMessagePartModel? part, ChatMessageCopyFormat format)
        {
            if (part == null || !part.IsUserVisible)
            {
                return false;
            }

            return format == ChatMessageCopyFormat.Full || !IsToolPart(part);
        }

        private static string BuildPartText(ChatMessagePartModel part, ChatMessageCopyFormat format)
        {
            return format switch
            {
                ChatMessageCopyFormat.PlainText => BuildPlainTextPart(part),
                _ => BuildMarkdownPart(part)
            };
        }

        private static string BuildMarkdownPart(ChatMessagePartModel part)
        {
            var builder = new StringBuilder();
            AppendMarkdownTitle(builder, part);

            switch (part.PartType)
            {
                case ChatMessagePartType.Text:
                case ChatMessagePartType.Status:
                case ChatMessagePartType.Placeholder:
                case ChatMessagePartType.Reasoning:
                case ChatMessagePartType.HostPreservedContent:
                    builder.Append(part.Content);
                    break;

                case ChatMessagePartType.Code:
                    AppendFencedBlock(builder, part.Content, string.IsNullOrWhiteSpace(part.Language) ? null : part.Language.Trim());
                    break;

                case ChatMessagePartType.StructuredXml:
                    AppendFencedBlock(builder, part.Content, "xml");
                    break;

                case ChatMessagePartType.ToolCall:
                    AppendFencedBlock(builder, part.Content, "xml");
                    AppendMarkdownToolResult(builder, part);
                    break;

                case ChatMessagePartType.ToolOutput:
                    AppendFencedBlock(builder, part.Content, ResolveFenceLanguage(part));
                    break;

                case ChatMessagePartType.Image:
                    builder.Append("![")
                        .Append(ResolveMediaAltText(part, "image"))
                        .Append("](")
                        .Append(ResolveMediaPath(part))
                        .Append(')');
                    break;

                case ChatMessagePartType.Audio:
                    builder.Append("[音频：")
                        .Append(ResolveMediaAltText(part, "audio"))
                        .Append("](")
                        .Append(ResolveMediaPath(part))
                        .Append(')');
                    break;

                default:
                    builder.Append(part.Content);
                    break;
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildPlainTextPart(ChatMessagePartModel part)
        {
            var builder = new StringBuilder();
            AppendPlainTextTitle(builder, part);

            switch (part.PartType)
            {
                case ChatMessagePartType.Text:
                    builder.Append(ConvertMarkdownToPlainText(part.Content));
                    break;

                case ChatMessagePartType.Image:
                    builder.Append("[图片] ").Append(ResolveMediaPath(part));
                    break;

                case ChatMessagePartType.Audio:
                    builder.Append("[音频] ").Append(ResolveMediaPath(part));
                    break;

                default:
                    builder.Append(part.Content);
                    break;
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendMarkdownTitle(StringBuilder builder, ChatMessagePartModel part)
        {
            var title = ResolvePartHeading(part, includeBadgeForToolParts: true);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            builder.Append("**")
                .Append(title)
                .AppendLine("**")
                .AppendLine();
        }

        private static void AppendPlainTextTitle(StringBuilder builder, ChatMessagePartModel part)
        {
            var title = ResolvePartHeading(part, includeBadgeForToolParts: false);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            builder.AppendLine(title);
            builder.AppendLine();
        }

        private static string? ResolvePartHeading(ChatMessagePartModel part, bool includeBadgeForToolParts)
        {
            var title = string.IsNullOrWhiteSpace(part.Title) ? null : part.Title.Trim();
            if (IsToolPart(part) && includeBadgeForToolParts)
            {
                var badge = string.IsNullOrWhiteSpace(part.BadgeText) ? "工具调用" : part.BadgeText.Trim();
                return string.IsNullOrWhiteSpace(title) ? badge : $"{badge} - {title}";
            }

            return title;
        }

        private static void AppendFencedBlock(StringBuilder builder, string content, string? language)
        {
            builder.Append("```");
            if (!string.IsNullOrWhiteSpace(language))
            {
                builder.Append(language);
            }

            builder.AppendLine();
            builder.Append(content ?? string.Empty);
            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.AppendLine();
            }

            builder.Append("```");
        }

        private static string ResolveFenceLanguage(ChatMessagePartModel part)
        {
            if (!string.IsNullOrWhiteSpace(part.Language))
            {
                return part.Language.Trim();
            }

            return part.Content.TrimStart().StartsWith("<", StringComparison.Ordinal) ? "xml" : "text";
        }

        private static string ResolveMediaAltText(ChatMessagePartModel part, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(part.Title))
            {
                return part.Title.Trim();
            }

            var fileName = Path.GetFileName(ResolveMediaPath(part));
            return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
        }

        private static string ResolveMediaPath(ChatMessagePartModel part)
        {
            return string.IsNullOrWhiteSpace(part.ResourcePath)
                ? part.Content
                : part.ResourcePath;
        }

        private static bool IsToolPart(ChatMessagePartModel part)
        {
            return part.PartType is ChatMessagePartType.ToolCall or ChatMessagePartType.ToolOutput;
        }

        private static void AppendMarkdownToolResult(StringBuilder builder, ChatMessagePartModel part)
        {
            if (!part.HasToolResult)
            {
                return;
            }

            builder.AppendLine()
                .AppendLine()
                .AppendLine("Result")
                .AppendLine();
            AppendFencedBlock(builder, part.ToolResultContent, ResolveToolResultFenceLanguage(part));
        }

        private static string ResolveToolResultFenceLanguage(ChatMessagePartModel part)
        {
            return string.Equals(
                part.ToolResultPresentationKind,
                SkyweaverToolResultPresentationKinds.LineDiffV1,
                StringComparison.OrdinalIgnoreCase)
                ? "diff"
                : "text";
        }

        private static string ConvertMarkdownToPlainText(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var block in MarkdownDocumentParser.Parse(markdown))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                AppendPlainTextBlock(builder, block);
            }

            return builder.ToString().Trim();
        }

        private static void AppendPlainTextBlock(StringBuilder builder, MarkdownBlock block)
        {
            switch (block)
            {
                case MarkdownParagraphBlock paragraph:
                    AppendPlainTextInlines(builder, paragraph.Inlines);
                    break;

                case MarkdownHeadingBlock heading:
                    AppendPlainTextInlines(builder, heading.Inlines);
                    break;

                case MarkdownQuoteBlock quote:
                    AppendPlainTextInlines(builder, quote.Inlines);
                    break;

                case MarkdownListBlock list:
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.AppendLine();
                        }

                        builder.Append(list.Items[index].Marker).Append(' ');
                        AppendPlainTextInlines(builder, list.Items[index].Inlines);
                    }

                    break;

                case MarkdownCodeBlock code:
                    builder.Append(code.Content);
                    break;

                case MarkdownMathBlock math:
                    builder.Append(math.Content);
                    break;

                case MarkdownTableBlock table:
                    AppendPlainTextTable(builder, table);
                    break;
            }
        }

        private static void AppendPlainTextTable(StringBuilder builder, MarkdownTableBlock table)
        {
            builder.AppendLine(string.Join(" | ", table.Columns.Select(column => column.Header)));
            foreach (var row in table.Rows)
            {
                builder.AppendLine(string.Join(" | ", row.Cells));
            }

            var newLine = Environment.NewLine;
            if (builder.Length >= newLine.Length &&
                string.Equals(
                    builder.ToString(builder.Length - newLine.Length, newLine.Length),
                    newLine,
                    StringComparison.Ordinal))
            {
                builder.Length -= newLine.Length;
            }
        }

        private static void AppendPlainTextInlines(StringBuilder builder, IEnumerable<MarkdownInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case MarkdownTextInline text:
                        builder.Append(text.Text);
                        break;

                    case MarkdownStrongInline strong:
                        AppendPlainTextInlines(builder, strong.Children);
                        break;

                    case MarkdownEmphasisInline emphasis:
                        AppendPlainTextInlines(builder, emphasis.Children);
                        break;

                    case MarkdownCodeInline code:
                        builder.Append(code.Content);
                        break;

                    case MarkdownLinkInline link:
                        var labelBuilder = new StringBuilder();
                        AppendPlainTextInlines(labelBuilder, link.Label);
                        var label = labelBuilder.ToString();
                        builder.Append(label);
                        if (!string.IsNullOrWhiteSpace(link.Url) &&
                            !string.Equals(label, link.Url, StringComparison.OrdinalIgnoreCase))
                        {
                            builder.Append(" (").Append(link.Url).Append(')');
                        }

                        break;

                    case MarkdownMathInline math:
                        builder.Append(math.Content);
                        break;

                    case MarkdownLineBreakInline:
                        builder.AppendLine();
                        break;
                }
            }
        }
    }
}
