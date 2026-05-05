using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Skyweaver.Services.SkyweaverTools
{
    public enum SkyweaverLineDiffEntryKind
    {
        Anchor = 0,
        Added = 1,
        Removed = 2,
        Separator = 3
    }

    public sealed class SkyweaverLineDiffEntry
    {
        public SkyweaverLineDiffEntry(
            string lineNumberText,
            string marker,
            string text,
            SkyweaverLineDiffEntryKind kind)
        {
            LineNumberText = lineNumberText ?? string.Empty;
            Marker = marker ?? string.Empty;
            Text = text ?? string.Empty;
            Kind = kind;
        }

        public string LineNumberText { get; }

        public string Marker { get; }

        public string Text { get; }

        public SkyweaverLineDiffEntryKind Kind { get; }

        public string KindName => Kind.ToString();

        public bool IsSeparator => Kind == SkyweaverLineDiffEntryKind.Separator;
    }

    public static class SkyweaverLineDiffPresentation
    {
        private static readonly Regex s_diffLinePattern = new(
            @"^(?<number>\d+)\s+(?<marker>[=+-])\s+\|\s?(?<text>.*)$",
            RegexOptions.Compiled);

        public static string BuildContent(
            string originalText,
            string updatedText,
            int contextLineCount = 1)
        {
            var originalLines = SplitLines(originalText);
            var updatedLines = SplitLines(updatedText);
            var operations = ComputeDiff(originalLines, updatedLines);
            var ranges = BuildDisplayRanges(operations, Math.Max(0, contextLineCount));
            if (ranges.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                if (rangeIndex > 0)
                {
                    builder.AppendLine();
                }

                var range = ranges[rangeIndex];
                for (var index = range.StartIndex; index < range.EndIndexExclusive; index++)
                {
                    var operation = operations[index];
                    builder
                        .Append(FormatLineNumber(operation.DisplayLineNumber))
                        .Append(' ')
                        .Append(GetMarker(operation.Kind))
                        .Append(" | ")
                        .Append(operation.Text);

                    if (!(rangeIndex == ranges.Count - 1 && index == range.EndIndexExclusive - 1))
                    {
                        builder.AppendLine();
                    }
                }
            }

            return builder.ToString();
        }

        public static bool TryParseToolOutputXml(
            string? toolOutputXml,
            out IReadOnlyList<SkyweaverLineDiffEntry> entries)
        {
            return TryParseContent(ExtractPrimaryMessageOrRawContent(toolOutputXml), out entries);
        }

        public static bool TryParseContent(
            string? content,
            out IReadOnlyList<SkyweaverLineDiffEntry> entries)
        {
            entries = Array.Empty<SkyweaverLineDiffEntry>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var parsedEntries = new List<SkyweaverLineDiffEntry>();
            var lines = EnumerateRawLines(content);
            foreach (var rawLine in lines)
            {
                if (rawLine.Length == 0)
                {
                    parsedEntries.Add(new SkyweaverLineDiffEntry(string.Empty, string.Empty, string.Empty, SkyweaverLineDiffEntryKind.Separator));
                    continue;
                }

                var match = s_diffLinePattern.Match(rawLine);
                if (!match.Success)
                {
                    entries = Array.Empty<SkyweaverLineDiffEntry>();
                    return false;
                }

                var marker = match.Groups["marker"].Value;
                parsedEntries.Add(new SkyweaverLineDiffEntry(
                    match.Groups["number"].Value,
                    marker,
                    match.Groups["text"].Value,
                    ParseKind(marker)));
            }

            while (parsedEntries.Count > 0 && parsedEntries[0].IsSeparator)
            {
                parsedEntries.RemoveAt(0);
            }

            while (parsedEntries.Count > 0 && parsedEntries[^1].IsSeparator)
            {
                parsedEntries.RemoveAt(parsedEntries.Count - 1);
            }

            entries = parsedEntries.Count == 0
                ? Array.Empty<SkyweaverLineDiffEntry>()
                : parsedEntries.ToArray();
            return entries.Count > 0;
        }

        public static string ExtractPrimaryMessageOrRawContent(string? toolOutputXml)
        {
            return TryExtractPrimaryMessage(toolOutputXml, out var primaryMessage)
                ? primaryMessage
                : string.IsNullOrWhiteSpace(toolOutputXml)
                    ? string.Empty
                    : toolOutputXml.Trim();
        }

        public static bool TryExtractPrimaryMessage(string? toolOutputXml, out string primaryMessage)
        {
            primaryMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(toolOutputXml))
            {
                return false;
            }

            try
            {
                var document = XDocument.Parse(toolOutputXml, LoadOptions.PreserveWhitespace);
                var toolReturn = document.Descendants()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "ToolReturn", StringComparison.OrdinalIgnoreCase));
                if (toolReturn == null)
                {
                    return false;
                }

                var stringReturn = toolReturn.Elements()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "StringReturn1", StringComparison.OrdinalIgnoreCase));
                if (stringReturn == null)
                {
                    return false;
                }

                primaryMessage = stringReturn.Value ?? string.Empty;
                return primaryMessage.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<string> SplitLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            return EnumerateRawLines(text).ToArray();
        }

        private static IEnumerable<string> EnumerateRawLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            var lineStart = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\r' && text[index] != '\n')
                {
                    continue;
                }

                yield return text[lineStart..index];
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                lineStart = index + 1;
            }

            if (lineStart < text.Length)
            {
                yield return text[lineStart..];
            }
        }

        private static IReadOnlyList<DiffOperation> ComputeDiff(
            IReadOnlyList<string> originalLines,
            IReadOnlyList<string> updatedLines)
        {
            var originalCount = originalLines.Count;
            var updatedCount = updatedLines.Count;
            var maximumEditDistance = originalCount + updatedCount;
            var trace = new List<Dictionary<int, int>>();
            var frontier = new Dictionary<int, int> { [1] = 0 };

            for (var distance = 0; distance <= maximumEditDistance; distance++)
            {
                var nextFrontier = new Dictionary<int, int>();
                for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
                {
                    int originalIndex;
                    if (diagonal == -distance ||
                        diagonal != distance && GetFrontierValue(frontier, diagonal - 1) < GetFrontierValue(frontier, diagonal + 1))
                    {
                        originalIndex = GetFrontierValue(frontier, diagonal + 1);
                    }
                    else
                    {
                        originalIndex = GetFrontierValue(frontier, diagonal - 1) + 1;
                    }

                    var updatedIndex = originalIndex - diagonal;
                    while (originalIndex < originalCount &&
                           updatedIndex < updatedCount &&
                           string.Equals(originalLines[originalIndex], updatedLines[updatedIndex], StringComparison.Ordinal))
                    {
                        originalIndex++;
                        updatedIndex++;
                    }

                    nextFrontier[diagonal] = originalIndex;
                    if (originalIndex >= originalCount && updatedIndex >= updatedCount)
                    {
                        trace.Add(nextFrontier);
                        return AnnotateLineNumbers(Backtrack(trace, originalLines, updatedLines));
                    }
                }

                trace.Add(nextFrontier);
                frontier = nextFrontier;
            }

            return AnnotateLineNumbers(BuildFallbackOperations(originalLines, updatedLines));
        }

        private static IReadOnlyList<DiffOperationSeed> Backtrack(
            IReadOnlyList<Dictionary<int, int>> trace,
            IReadOnlyList<string> originalLines,
            IReadOnlyList<string> updatedLines)
        {
            var operations = new List<DiffOperationSeed>();
            var originalIndex = originalLines.Count;
            var updatedIndex = updatedLines.Count;

            for (var distance = trace.Count - 1; distance >= 0; distance--)
            {
                if (distance == 0)
                {
                    while (originalIndex > 0 && updatedIndex > 0)
                    {
                        operations.Add(new DiffOperationSeed(DiffOperationKind.Anchor, originalLines[originalIndex - 1]));
                        originalIndex--;
                        updatedIndex--;
                    }

                    while (originalIndex > 0)
                    {
                        operations.Add(new DiffOperationSeed(DiffOperationKind.Removed, originalLines[originalIndex - 1]));
                        originalIndex--;
                    }

                    while (updatedIndex > 0)
                    {
                        operations.Add(new DiffOperationSeed(DiffOperationKind.Added, updatedLines[updatedIndex - 1]));
                        updatedIndex--;
                    }

                    break;
                }

                var previousFrontier = trace[distance - 1];
                var diagonal = originalIndex - updatedIndex;
                var previousDiagonal = diagonal == -distance ||
                                       diagonal != distance &&
                                       GetFrontierValue(previousFrontier, diagonal - 1) < GetFrontierValue(previousFrontier, diagonal + 1)
                    ? diagonal + 1
                    : diagonal - 1;

                var previousOriginalIndex = GetFrontierValue(previousFrontier, previousDiagonal);
                var previousUpdatedIndex = previousOriginalIndex - previousDiagonal;

                while (originalIndex > previousOriginalIndex && updatedIndex > previousUpdatedIndex)
                {
                    operations.Add(new DiffOperationSeed(DiffOperationKind.Anchor, originalLines[originalIndex - 1]));
                    originalIndex--;
                    updatedIndex--;
                }

                if (originalIndex == previousOriginalIndex && updatedIndex > previousUpdatedIndex)
                {
                    operations.Add(new DiffOperationSeed(DiffOperationKind.Added, updatedLines[updatedIndex - 1]));
                    updatedIndex--;
                }
                else if (updatedIndex == previousUpdatedIndex && originalIndex > previousOriginalIndex)
                {
                    operations.Add(new DiffOperationSeed(DiffOperationKind.Removed, originalLines[originalIndex - 1]));
                    originalIndex--;
                }
            }

            operations.Reverse();
            return operations;
        }

        private static IReadOnlyList<DiffOperationSeed> BuildFallbackOperations(
            IReadOnlyList<string> originalLines,
            IReadOnlyList<string> updatedLines)
        {
            return originalLines
                .Select(line => new DiffOperationSeed(DiffOperationKind.Removed, line))
                .Concat(updatedLines.Select(line => new DiffOperationSeed(DiffOperationKind.Added, line)))
                .ToArray();
        }

        private static IReadOnlyList<DiffOperation> AnnotateLineNumbers(
            IReadOnlyList<DiffOperationSeed> operations)
        {
            if (operations.Count == 0)
            {
                return Array.Empty<DiffOperation>();
            }

            var annotatedOperations = new List<DiffOperation>(operations.Count);
            var originalLineNumber = 1;
            var updatedLineNumber = 1;

            foreach (var operation in operations)
            {
                var originalNumber = default(int?);
                var updatedNumber = default(int?);
                var displayNumber = default(int?);

                switch (operation.Kind)
                {
                    case DiffOperationKind.Anchor:
                        originalNumber = originalLineNumber++;
                        updatedNumber = updatedLineNumber++;
                        displayNumber = updatedNumber;
                        break;

                    case DiffOperationKind.Added:
                        updatedNumber = updatedLineNumber++;
                        displayNumber = updatedNumber;
                        break;

                    case DiffOperationKind.Removed:
                        originalNumber = originalLineNumber++;
                        displayNumber = originalNumber;
                        break;
                }

                annotatedOperations.Add(new DiffOperation(
                    operation.Kind,
                    operation.Text,
                    displayNumber,
                    originalNumber,
                    updatedNumber));
            }

            return annotatedOperations;
        }

        private static IReadOnlyList<DisplayRange> BuildDisplayRanges(
            IReadOnlyList<DiffOperation> operations,
            int contextLineCount)
        {
            var changeIndexes = operations
                .Select((operation, index) => new { operation.Kind, Index = index })
                .Where(item => item.Kind != DiffOperationKind.Anchor)
                .Select(item => item.Index)
                .ToArray();

            if (changeIndexes.Length == 0)
            {
                return operations.Count == 0
                    ? Array.Empty<DisplayRange>()
                    : new[] { new DisplayRange(0, operations.Count) };
            }

            var ranges = new List<DisplayRange>();
            foreach (var changeIndex in changeIndexes)
            {
                var startIndex = Math.Max(0, changeIndex - contextLineCount);
                var endIndexExclusive = Math.Min(operations.Count, changeIndex + contextLineCount + 1);
                if (ranges.Count == 0)
                {
                    ranges.Add(new DisplayRange(startIndex, endIndexExclusive));
                    continue;
                }

                var previous = ranges[^1];
                if (startIndex <= previous.EndIndexExclusive)
                {
                    ranges[^1] = new DisplayRange(previous.StartIndex, Math.Max(previous.EndIndexExclusive, endIndexExclusive));
                    continue;
                }

                ranges.Add(new DisplayRange(startIndex, endIndexExclusive));
            }

            return ranges;
        }

        private static string FormatLineNumber(int? lineNumber)
        {
            return lineNumber.HasValue
                ? lineNumber.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static int GetFrontierValue(
            IReadOnlyDictionary<int, int> frontier,
            int diagonal)
        {
            return frontier.TryGetValue(diagonal, out var value) ? value : 0;
        }

        private static string GetMarker(DiffOperationKind kind)
        {
            return kind switch
            {
                DiffOperationKind.Added => "+",
                DiffOperationKind.Removed => "-",
                _ => "="
            };
        }

        private static SkyweaverLineDiffEntryKind ParseKind(string marker)
        {
            return marker switch
            {
                "+" => SkyweaverLineDiffEntryKind.Added,
                "-" => SkyweaverLineDiffEntryKind.Removed,
                _ => SkyweaverLineDiffEntryKind.Anchor
            };
        }

        private sealed record DisplayRange(
            int StartIndex,
            int EndIndexExclusive);

        private sealed record DiffOperationSeed(
            DiffOperationKind Kind,
            string Text);

        private sealed record DiffOperation(
            DiffOperationKind Kind,
            string Text,
            int? DisplayLineNumber,
            int? OriginalLineNumber,
            int? UpdatedLineNumber);

        private enum DiffOperationKind
        {
            Anchor = 0,
            Added = 1,
            Removed = 2
        }
    }
}
