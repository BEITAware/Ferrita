using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;

namespace AerialCity.Segmentation;

/// <summary>
/// Splits a complete text file into paragraph-first chunks for embedding.
/// </summary>
public static class TextFileSegmenter
{
    public const double DefaultOverlapRatio = 0.25d;

    public static IReadOnlyList<Segment> SegmentText(
        string text,
        string? sourceUri,
        int maxInputTokens,
        double overlapRatio = DefaultOverlapRatio,
        IReadOnlyDictionary<string, object>? metadata = null,
        Func<string, int>? tokenCounter = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxInputTokens <= 0)
            throw new SegmentationException("Max input tokens must be greater than zero.");

        if (double.IsNaN(overlapRatio) || double.IsInfinity(overlapRatio) || overlapRatio < 0d || overlapRatio >= 1d)
            throw new SegmentationException("Overlap ratio must be greater than or equal to 0 and less than 1.");

        if (text.Length == 0)
            return [];

        var countTokens = tokenCounter ?? EstimateTokens;
        var paragraphs = SplitParagraphs(text);
        if (paragraphs.Count == 0)
            return [];

        var overlapTokens = Math.Min(
            maxInputTokens - 1,
            Math.Max(0, (int)Math.Floor(maxInputTokens * overlapRatio)));

        var chunks = new List<TextChunk>();
        foreach (var paragraph in paragraphs)
        {
            var paragraphChunks = CreateParagraphChunks(
                text,
                paragraph,
                maxInputTokens,
                overlapTokens,
                countTokens);

            for (var i = 0; i < paragraphChunks.Count; i++)
            {
                var chunk = paragraphChunks[i];
                chunks.Add(chunk with
                {
                    ParagraphChunkIndex = i,
                    ParagraphChunkCount = paragraphChunks.Count
                });
            }
        }

        var segments = new List<Segment>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var content = text[chunk.Start..chunk.End].Trim();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var segment = new Segment(SegmentKind.TextPassage, content)
            {
                SourceUri = sourceUri,
                StartOffset = chunk.Start,
                EndOffset = chunk.End
            };

            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                    segment.Metadata[key] = value;
            }

            segment.Metadata["sourceKind"] = "text-file";
            segment.Metadata["paragraphIndex"] = chunk.ParagraphIndex;
            segment.Metadata["paragraphCount"] = paragraphs.Count;
            segment.Metadata["paragraphChunkIndex"] = chunk.ParagraphChunkIndex;
            segment.Metadata["paragraphChunkCount"] = chunk.ParagraphChunkCount;
            segment.Metadata["chunkIndex"] = i;
            segment.Metadata["chunkCount"] = chunks.Count;
            segment.Metadata["estimatedTokens"] = countTokens(content);
            segment.Metadata["maxInputTokens"] = maxInputTokens;
            segment.Metadata["overlapRatio"] = overlapRatio;

            segments.Add(segment);
        }

        return segments;
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var lexicalTokens = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                if (!inWord)
                    lexicalTokens++;

                inWord = true;
                continue;
            }

            inWord = false;

            if (!char.IsWhiteSpace(ch))
                lexicalTokens++;
        }

        var charBasedTokens = (text.Length + 3) / 4;
        return Math.Max(1, Math.Max(lexicalTokens, charBasedTokens));
    }

    private static IReadOnlyList<TextParagraph> SplitParagraphs(string text)
    {
        var paragraphs = new List<TextParagraph>();
        var paragraphStart = -1;
        var paragraphEnd = -1;
        var lineStart = 0;

        while (lineStart < text.Length)
        {
            var lineEnd = FindLineEnd(text, lineStart, out var nextLineStart);
            var line = text[lineStart..lineEnd];

            if (string.IsNullOrWhiteSpace(line))
            {
                AddParagraph(text, paragraphStart, paragraphEnd, paragraphs);
                paragraphStart = -1;
                paragraphEnd = -1;
            }
            else
            {
                if (paragraphStart < 0)
                    paragraphStart = lineStart;

                paragraphEnd = lineEnd;
            }

            lineStart = nextLineStart;
        }

        AddParagraph(text, paragraphStart, paragraphEnd, paragraphs);
        return paragraphs;
    }

    private static int FindLineEnd(string text, int lineStart, out int nextLineStart)
    {
        var lf = text.IndexOf('\n', lineStart);
        if (lf < 0)
        {
            nextLineStart = text.Length;
            return text.Length;
        }

        nextLineStart = lf + 1;
        return lf > lineStart && text[lf - 1] == '\r'
            ? lf - 1
            : lf;
    }

    private static void AddParagraph(
        string text,
        int paragraphStart,
        int paragraphEnd,
        List<TextParagraph> paragraphs)
    {
        if (paragraphStart < 0 || paragraphEnd <= paragraphStart)
            return;

        while (paragraphStart < paragraphEnd && char.IsWhiteSpace(text[paragraphStart]))
            paragraphStart++;

        while (paragraphEnd > paragraphStart && char.IsWhiteSpace(text[paragraphEnd - 1]))
            paragraphEnd--;

        if (paragraphEnd <= paragraphStart)
            return;

        paragraphs.Add(new TextParagraph(
            paragraphs.Count,
            paragraphStart,
            paragraphEnd));
    }

    private static IReadOnlyList<TextChunk> CreateParagraphChunks(
        string text,
        TextParagraph paragraph,
        int maxInputTokens,
        int overlapTokens,
        Func<string, int> countTokens)
    {
        var paragraphText = text[paragraph.Start..paragraph.End];
        if (countTokens(paragraphText) <= maxInputTokens)
        {
            return
            [
                new TextChunk(
                    paragraph.Index,
                    paragraph.Start,
                    paragraph.End,
                    ParagraphChunkIndex: 0,
                    ParagraphChunkCount: 1)
            ];
        }

        var chunks = new List<TextChunk>();
        var cursor = paragraph.Start;

        while (cursor < paragraph.End)
        {
            cursor = SkipForwardWhitespace(text, cursor, paragraph.End);
            if (cursor >= paragraph.End)
                break;

            var chunkEnd = FindLargestEndWithinLimit(text, cursor, paragraph.End, maxInputTokens, countTokens);
            if (chunkEnd <= cursor)
                throw new SegmentationException("Could not create a text chunk within the requested token limit.");

            chunkEnd = PreferNaturalBreak(text, cursor, chunkEnd);
            chunkEnd = TrimBackwardWhitespace(text, cursor, chunkEnd);
            if (chunkEnd <= cursor)
                break;

            chunks.Add(new TextChunk(
                paragraph.Index,
                cursor,
                chunkEnd,
                ParagraphChunkIndex: 0,
                ParagraphChunkCount: 0));

            if (chunkEnd >= paragraph.End)
                break;

            var nextStart = FindOverlapStart(text, cursor, chunkEnd, overlapTokens, countTokens);
            nextStart = PreferNaturalStart(text, nextStart, chunkEnd);

            if (nextStart <= cursor)
                nextStart = chunkEnd;

            cursor = nextStart;
        }

        return chunks;
    }

    private static int FindLargestEndWithinLimit(
        string text,
        int start,
        int maxEnd,
        int maxInputTokens,
        Func<string, int> countTokens)
    {
        var low = start + 1;
        var high = maxEnd;
        var best = start;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var tokens = countTokens(text[start..mid]);
            if (tokens <= maxInputTokens)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static int PreferNaturalBreak(string text, int start, int end)
    {
        var minEnd = start + Math.Max(1, (end - start) / 2);
        for (var i = end - 1; i >= minEnd; i--)
        {
            if (IsNaturalBreak(text[i]))
                return i + 1;
        }

        return end;
    }

    private static int FindOverlapStart(
        string text,
        int chunkStart,
        int chunkEnd,
        int overlapTokens,
        Func<string, int> countTokens)
    {
        if (overlapTokens <= 0)
            return chunkEnd;

        var low = chunkStart;
        var high = chunkEnd;
        var best = chunkEnd;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var tokens = countTokens(text[mid..chunkEnd]);
            if (tokens <= overlapTokens)
            {
                best = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return best;
    }

    private static int PreferNaturalStart(string text, int start, int chunkEnd)
    {
        if (start >= chunkEnd)
            return chunkEnd;

        for (var i = start; i < chunkEnd; i++)
        {
            if (IsNaturalBreak(text[i]))
                return SkipForwardWhitespace(text, i + 1, chunkEnd);
        }

        return SkipForwardWhitespace(text, start, chunkEnd);
    }

    private static bool IsNaturalBreak(char ch) =>
        char.IsWhiteSpace(ch) ||
        ch is '.' or '!' or '?' or ';' or ':' or ',' or ')' or ']' or '}';

    private static int SkipForwardWhitespace(string text, int start, int maxEnd)
    {
        while (start < maxEnd && char.IsWhiteSpace(text[start]))
            start++;

        return start;
    }

    private static int TrimBackwardWhitespace(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        return end;
    }

    private readonly record struct TextParagraph(int Index, int Start, int End);

    private readonly record struct TextChunk(
        int ParagraphIndex,
        int Start,
        int End,
        int ParagraphChunkIndex,
        int ParagraphChunkCount);
}
