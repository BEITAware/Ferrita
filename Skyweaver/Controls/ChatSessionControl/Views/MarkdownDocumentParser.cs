using System.Text;
using System.Text.RegularExpressions;

namespace Skyweaver.Controls.ChatSessionControl.Views
{
    internal static class MarkdownDocumentParser
    {
        private static readonly Regex HeadingPattern = new(@"^\s*(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex UnorderedListPattern = new(@"^\s*[-+*]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex OrderedListPattern = new(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.Compiled);

        public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return Array.Empty<MarkdownBlock>();
            }

            var normalized = NormalizeNewlines(markdown);
            var lines = normalized.Split('\n');
            var blocks = new List<MarkdownBlock>();
            var paragraphLines = new List<string>();

            for (var index = 0; index < lines.Length;)
            {
                var line = lines[index];
                var trimmed = line.Trim();

                if (trimmed.Length == 0)
                {
                    FlushParagraph(blocks, paragraphLines);
                    index++;
                    continue;
                }

                if (TryParseCodeBlock(lines, ref index, out var codeBlock))
                {
                    FlushParagraph(blocks, paragraphLines);
                    blocks.Add(codeBlock);
                    continue;
                }

                if (TryParseMathBlock(lines, ref index, out var mathBlock))
                {
                    FlushParagraph(blocks, paragraphLines);
                    blocks.Add(mathBlock);
                    continue;
                }

                if (TryParseHeading(trimmed, out var headingBlock))
                {
                    FlushParagraph(blocks, paragraphLines);
                    blocks.Add(headingBlock);
                    index++;
                    continue;
                }

                if (TryParseList(lines, ref index, out var listBlock))
                {
                    FlushParagraph(blocks, paragraphLines);
                    blocks.Add(listBlock);
                    continue;
                }

                if (TryParseQuote(lines, ref index, out var quoteBlock))
                {
                    FlushParagraph(blocks, paragraphLines);
                    blocks.Add(quoteBlock);
                    continue;
                }

                paragraphLines.Add(line);
                index++;
            }

            FlushParagraph(blocks, paragraphLines);
            return blocks;
        }

        private static void FlushParagraph(ICollection<MarkdownBlock> blocks, ICollection<string> paragraphLines)
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            var text = string.Join("\n", paragraphLines).Trim();
            paragraphLines.Clear();
            if (text.Length == 0)
            {
                return;
            }

            blocks.Add(new MarkdownParagraphBlock(ParseInlines(text)));
        }

        private static bool TryParseHeading(string line, out MarkdownHeadingBlock block)
        {
            var match = HeadingPattern.Match(line);
            if (!match.Success)
            {
                block = null!;
                return false;
            }

            var level = Math.Min(6, match.Groups[1].Value.Length);
            block = new MarkdownHeadingBlock(level, ParseInlines(match.Groups[2].Value.Trim()));
            return true;
        }

        private static bool TryParseCodeBlock(string[] lines, ref int index, out MarkdownCodeBlock block)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                block = null!;
                return false;
            }

            var language = trimmed.Length > 3 ? trimmed[3..].Trim() : string.Empty;
            var codeLines = new List<string>();
            index++;

            while (index < lines.Length)
            {
                if (lines[index].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    index++;
                    break;
                }

                codeLines.Add(lines[index]);
                index++;
            }

            block = new MarkdownCodeBlock(string.Join(Environment.NewLine, codeLines), NormalizeOptional(language));
            return true;
        }

        private static bool TryParseMathBlock(string[] lines, ref int index, out MarkdownMathBlock block)
        {
            var line = lines[index];
            var openIndex = line.IndexOf("\\[", StringComparison.Ordinal);
            if (openIndex < 0 || line[..openIndex].Trim().Length != 0)
            {
                block = null!;
                return false;
            }

            var contentLines = new List<string>();
            var remainder = line[(openIndex + 2)..];
            var closeIndex = remainder.IndexOf("\\]", StringComparison.Ordinal);
            if (closeIndex >= 0)
            {
                contentLines.Add(remainder[..closeIndex]);
                index++;
                block = new MarkdownMathBlock(string.Join(Environment.NewLine, contentLines));
                return true;
            }

            contentLines.Add(remainder);
            index++;

            while (index < lines.Length)
            {
                var currentLine = lines[index];
                closeIndex = currentLine.IndexOf("\\]", StringComparison.Ordinal);
                if (closeIndex >= 0)
                {
                    contentLines.Add(currentLine[..closeIndex]);
                    index++;
                    block = new MarkdownMathBlock(string.Join(Environment.NewLine, contentLines));
                    return true;
                }

                contentLines.Add(currentLine);
                index++;
            }

            block = new MarkdownMathBlock(string.Join(Environment.NewLine, contentLines));
            return true;
        }

        private static bool TryParseQuote(string[] lines, ref int index, out MarkdownQuoteBlock block)
        {
            if (!lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                block = null!;
                return false;
            }

            var quoteLines = new List<string>();
            while (index < lines.Length)
            {
                var trimmedStart = lines[index].TrimStart();
                if (!trimmedStart.StartsWith(">", StringComparison.Ordinal))
                {
                    break;
                }

                var content = trimmedStart[1..];
                if (content.StartsWith(" ", StringComparison.Ordinal))
                {
                    content = content[1..];
                }

                quoteLines.Add(content);
                index++;
            }

            block = new MarkdownQuoteBlock(ParseInlines(string.Join("\n", quoteLines).TrimEnd()));
            return true;
        }

        private static bool TryParseList(string[] lines, ref int index, out MarkdownListBlock block)
        {
            if (!TryGetListItem(lines[index], out var markerText, out var itemContent))
            {
                block = null!;
                return false;
            }

            var items = new List<MarkdownListItem>
            {
                new(markerText, ParseInlines(itemContent))
            };

            index++;
            while (index < lines.Length && TryGetListItem(lines[index], out markerText, out itemContent))
            {
                items.Add(new MarkdownListItem(markerText, ParseInlines(itemContent)));
                index++;
            }

            block = new MarkdownListBlock(items);
            return true;
        }

        private static bool TryGetListItem(string line, out string markerText, out string itemContent)
        {
            var orderedMatch = OrderedListPattern.Match(line);
            if (orderedMatch.Success)
            {
                markerText = $"{orderedMatch.Groups[1].Value}.";
                itemContent = orderedMatch.Groups[2].Value.TrimEnd();
                return true;
            }

            var unorderedMatch = UnorderedListPattern.Match(line);
            if (unorderedMatch.Success)
            {
                markerText = "-";
                itemContent = unorderedMatch.Groups[1].Value.TrimEnd();
                return true;
            }

            markerText = string.Empty;
            itemContent = string.Empty;
            return false;
        }

        private static IReadOnlyList<MarkdownInline> ParseInlines(string text)
        {
            var inlines = new List<MarkdownInline>();
            var builder = new StringBuilder();

            for (var index = 0; index < text.Length;)
            {
                if (TryParseMathInline(text, ref index, builder, inlines))
                {
                    continue;
                }

                if (TryParseCodeInline(text, ref index, builder, inlines))
                {
                    continue;
                }

                if (TryParseLinkInline(text, ref index, builder, inlines))
                {
                    continue;
                }

                if (TryParseStrongInline(text, ref index, builder, inlines))
                {
                    continue;
                }

                if (TryParseEmphasisInline(text, ref index, builder, inlines))
                {
                    continue;
                }

                if (text[index] == '\\' && index + 1 < text.Length)
                {
                    builder.Append(text[index + 1]);
                    index += 2;
                    continue;
                }

                if (text[index] == '\n')
                {
                    FlushTextInline(builder, inlines);
                    inlines.Add(MarkdownLineBreakInline.Instance);
                    index++;
                    continue;
                }

                builder.Append(text[index]);
                index++;
            }

            FlushTextInline(builder, inlines);
            return MergeAdjacentTextInlines(inlines);
        }

        private static bool TryParseMathInline(
            string text,
            ref int index,
            StringBuilder builder,
            ICollection<MarkdownInline> inlines)
        {
            var isDisplayStyle = false;
            string closingDelimiter;
            if (StartsWith(text, index, "\\("))
            {
                closingDelimiter = "\\)";
            }
            else if (StartsWith(text, index, "\\["))
            {
                closingDelimiter = "\\]";
                isDisplayStyle = true;
            }
            else
            {
                return false;
            }

            var contentStart = index + 2;
            var closingIndex = text.IndexOf(closingDelimiter, contentStart, StringComparison.Ordinal);
            if (closingIndex < 0)
            {
                return false;
            }

            FlushTextInline(builder, inlines);
            inlines.Add(new MarkdownMathInline(text[contentStart..closingIndex], isDisplayStyle));
            index = closingIndex + closingDelimiter.Length;
            return true;
        }

        private static bool TryParseCodeInline(
            string text,
            ref int index,
            StringBuilder builder,
            ICollection<MarkdownInline> inlines)
        {
            if (text[index] != '`')
            {
                return false;
            }

            var closingIndex = text.IndexOf('`', index + 1);
            if (closingIndex < 0)
            {
                return false;
            }

            FlushTextInline(builder, inlines);
            inlines.Add(new MarkdownCodeInline(text[(index + 1)..closingIndex]));
            index = closingIndex + 1;
            return true;
        }

        private static bool TryParseLinkInline(
            string text,
            ref int index,
            StringBuilder builder,
            ICollection<MarkdownInline> inlines)
        {
            if (text[index] != '[')
            {
                return false;
            }

            var closeLabelIndex = text.IndexOf("](", index, StringComparison.Ordinal);
            if (closeLabelIndex < 0)
            {
                return false;
            }

            var closeUrlIndex = text.IndexOf(')', closeLabelIndex + 2);
            if (closeUrlIndex < 0)
            {
                return false;
            }

            var label = text[(index + 1)..closeLabelIndex];
            var url = text[(closeLabelIndex + 2)..closeUrlIndex].Trim();
            if (label.Length == 0 || url.Length == 0)
            {
                return false;
            }

            FlushTextInline(builder, inlines);
            inlines.Add(new MarkdownLinkInline(ParseInlines(label), url));
            index = closeUrlIndex + 1;
            return true;
        }

        private static bool TryParseStrongInline(
            string text,
            ref int index,
            StringBuilder builder,
            ICollection<MarkdownInline> inlines)
        {
            if (!StartsWith(text, index, "**"))
            {
                return false;
            }

            var closingIndex = text.IndexOf("**", index + 2, StringComparison.Ordinal);
            if (closingIndex < 0)
            {
                return false;
            }

            var content = text[(index + 2)..closingIndex];
            if (content.Length == 0)
            {
                return false;
            }

            FlushTextInline(builder, inlines);
            inlines.Add(new MarkdownStrongInline(ParseInlines(content)));
            index = closingIndex + 2;
            return true;
        }

        private static bool TryParseEmphasisInline(
            string text,
            ref int index,
            StringBuilder builder,
            ICollection<MarkdownInline> inlines)
        {
            if (text[index] != '*')
            {
                return false;
            }

            if (index + 1 < text.Length && text[index + 1] == '*')
            {
                return false;
            }

            var closingIndex = text.IndexOf('*', index + 1);
            if (closingIndex < 0)
            {
                return false;
            }

            var content = text[(index + 1)..closingIndex];
            if (content.Length == 0)
            {
                return false;
            }

            FlushTextInline(builder, inlines);
            inlines.Add(new MarkdownEmphasisInline(ParseInlines(content)));
            index = closingIndex + 1;
            return true;
        }

        private static void FlushTextInline(StringBuilder builder, ICollection<MarkdownInline> inlines)
        {
            if (builder.Length == 0)
            {
                return;
            }

            inlines.Add(new MarkdownTextInline(builder.ToString()));
            builder.Clear();
        }

        private static IReadOnlyList<MarkdownInline> MergeAdjacentTextInlines(IReadOnlyList<MarkdownInline> inlines)
        {
            if (inlines.Count < 2)
            {
                return inlines;
            }

            var merged = new List<MarkdownInline>(inlines.Count);
            foreach (var inline in inlines)
            {
                if (inline is MarkdownTextInline currentText &&
                    merged.Count > 0 &&
                    merged[^1] is MarkdownTextInline previousText)
                {
                    merged[^1] = new MarkdownTextInline(previousText.Text + currentText.Text);
                    continue;
                }

                merged.Add(inline);
            }

            return merged;
        }

        private static bool StartsWith(string text, int index, string value)
        {
            return index + value.Length <= text.Length &&
                   string.Compare(text, index, value, 0, value.Length, StringComparison.Ordinal) == 0;
        }

        private static string NormalizeNewlines(string value)
        {
            return value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }

        private static string? NormalizeOptional(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    internal abstract class MarkdownBlock
    {
    }

    internal sealed class MarkdownParagraphBlock : MarkdownBlock
    {
        public MarkdownParagraphBlock(IReadOnlyList<MarkdownInline> inlines)
        {
            Inlines = inlines ?? throw new ArgumentNullException(nameof(inlines));
        }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    internal sealed class MarkdownHeadingBlock : MarkdownBlock
    {
        public MarkdownHeadingBlock(int level, IReadOnlyList<MarkdownInline> inlines)
        {
            Level = level;
            Inlines = inlines ?? throw new ArgumentNullException(nameof(inlines));
        }

        public int Level { get; }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    internal sealed class MarkdownCodeBlock : MarkdownBlock
    {
        public MarkdownCodeBlock(string content, string? language)
        {
            Content = content ?? string.Empty;
            Language = language;
        }

        public string Content { get; }

        public string? Language { get; }
    }

    internal sealed class MarkdownQuoteBlock : MarkdownBlock
    {
        public MarkdownQuoteBlock(IReadOnlyList<MarkdownInline> inlines)
        {
            Inlines = inlines ?? throw new ArgumentNullException(nameof(inlines));
        }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    internal sealed class MarkdownListBlock : MarkdownBlock
    {
        public MarkdownListBlock(IReadOnlyList<MarkdownListItem> items)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
        }

        public IReadOnlyList<MarkdownListItem> Items { get; }
    }

    internal sealed class MarkdownMathBlock : MarkdownBlock
    {
        public MarkdownMathBlock(string content)
        {
            Content = content ?? string.Empty;
        }

        public string Content { get; }
    }

    internal sealed class MarkdownListItem
    {
        public MarkdownListItem(string marker, IReadOnlyList<MarkdownInline> inlines)
        {
            Marker = marker ?? string.Empty;
            Inlines = inlines ?? throw new ArgumentNullException(nameof(inlines));
        }

        public string Marker { get; }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    internal abstract class MarkdownInline
    {
    }

    internal sealed class MarkdownTextInline : MarkdownInline
    {
        public MarkdownTextInline(string text)
        {
            Text = text ?? string.Empty;
        }

        public string Text { get; }
    }

    internal sealed class MarkdownStrongInline : MarkdownInline
    {
        public MarkdownStrongInline(IReadOnlyList<MarkdownInline> children)
        {
            Children = children ?? throw new ArgumentNullException(nameof(children));
        }

        public IReadOnlyList<MarkdownInline> Children { get; }
    }

    internal sealed class MarkdownEmphasisInline : MarkdownInline
    {
        public MarkdownEmphasisInline(IReadOnlyList<MarkdownInline> children)
        {
            Children = children ?? throw new ArgumentNullException(nameof(children));
        }

        public IReadOnlyList<MarkdownInline> Children { get; }
    }

    internal sealed class MarkdownCodeInline : MarkdownInline
    {
        public MarkdownCodeInline(string content)
        {
            Content = content ?? string.Empty;
        }

        public string Content { get; }
    }

    internal sealed class MarkdownLinkInline : MarkdownInline
    {
        public MarkdownLinkInline(IReadOnlyList<MarkdownInline> label, string url)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Url = url ?? string.Empty;
        }

        public IReadOnlyList<MarkdownInline> Label { get; }

        public string Url { get; }
    }

    internal sealed class MarkdownMathInline : MarkdownInline
    {
        public MarkdownMathInline(string content, bool isDisplayStyle)
        {
            Content = content ?? string.Empty;
            IsDisplayStyle = isDisplayStyle;
        }

        public string Content { get; }

        public bool IsDisplayStyle { get; }
    }

    internal sealed class MarkdownLineBreakInline : MarkdownInline
    {
        private MarkdownLineBreakInline()
        {
        }

        public static MarkdownLineBreakInline Instance { get; } = new();
    }
}
