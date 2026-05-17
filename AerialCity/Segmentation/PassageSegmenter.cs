using AerialCity.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace AerialCity.Segmentation;

/// <summary>
/// Splits natural language text into passages using paragraph boundaries
/// and token-based chunking with configurable overlap.
/// </summary>
public sealed class PassageSegmenter : ISegmenter
{
    private readonly ILogger<PassageSegmenter> _logger;
    private static readonly char[] SentenceEnders = ['.', '!', '?', '。', '！', '？'];

    public SegmentKind OutputKind => SegmentKind.TextPassage;

    public PassageSegmenter(ILogger<PassageSegmenter> logger) => _logger = logger;

    public IReadOnlyList<Segment> Segment(RawContent content, SegmentationOptions options)
    {
        _logger.LogDebug("Passage segmenting {Len} chars", content.Text.Length);

        var paragraphs = SplitParagraphs(content.Text);
        var segments = new List<Segment>();
        var buffer = new List<(string Text, int Start, int End)>();
        var bufferTokens = 0;

        foreach (var para in paragraphs)
        {
            var tokens = EstimateTokens(para.Text);

            if (tokens > options.MaxTokensPerSegment)
            {
                if (buffer.Count > 0)
                {
                    Emit(segments, buffer, content);
                    buffer = Overlap(buffer, options.OverlapTokens);
                    bufferTokens = buffer.Sum(b => EstimateTokens(b.Text));
                }
                // Split large paragraph by sentences
                var sents = SplitSentences(para.Text, para.Start);
                var sb = new List<(string Text, int Start, int End)>();
                var st = 0;
                foreach (var s in sents)
                {
                    var t = EstimateTokens(s.Text);
                    if (st + t > options.MaxTokensPerSegment && sb.Count > 0)
                    {
                        Emit(segments, sb, content);
                        sb = Overlap(sb, options.OverlapTokens);
                        st = sb.Sum(b => EstimateTokens(b.Text));
                    }
                    sb.Add(s);
                    st += t;
                }
                if (sb.Count > 0) Emit(segments, sb, content);
                continue;
            }

            if (bufferTokens + tokens > options.MaxTokensPerSegment && buffer.Count > 0)
            {
                Emit(segments, buffer, content);
                buffer = Overlap(buffer, options.OverlapTokens);
                bufferTokens = buffer.Sum(b => EstimateTokens(b.Text));
            }
            buffer.Add(para);
            bufferTokens += tokens;
        }

        if (buffer.Count > 0) Emit(segments, buffer, content);

        _logger.LogDebug("Produced {Count} passages", segments.Count);
        return segments;
    }

    private static List<(string Text, int Start, int End)> SplitParagraphs(string text)
    {
        var result = new List<(string, int, int)>();
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (end == -1) end = text.Length;
            var t = text[start..end].Trim();
            if (t.Length > 0) result.Add((t, start, end));
            start = end + 2;
        }
        if (result.Count == 0 && text.Trim().Length > 0)
            result.Add((text.Trim(), 0, text.Length));
        return result;
    }

    private static List<(string Text, int Start, int End)> SplitSentences(string text, int baseOff)
    {
        var result = new List<(string, int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (SentenceEnders.Contains(text[i]) && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                var s = text[start..(i + 1)].Trim();
                if (s.Length > 0) result.Add((s, baseOff + start, baseOff + i + 1));
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            var r = text[start..].Trim();
            if (r.Length > 0) result.Add((r, baseOff + start, baseOff + text.Length));
        }
        return result;
    }

    private static void Emit(List<Segment> segs, List<(string Text, int Start, int End)> buf, RawContent c)
    {
        var combined = string.Join("\n\n", buf.Select(b => b.Text));
        segs.Add(new Segment(SegmentKind.TextPassage, combined)
        {
            SourceUri = c.SourceUri,
            StartOffset = buf[0].Start,
            EndOffset = buf[^1].End,
            Metadata = { ["paragraphCount"] = buf.Count }
        });
    }

    private static List<(string Text, int Start, int End)> Overlap(
        List<(string Text, int Start, int End)> buf, int overlapTokens)
    {
        if (overlapTokens <= 0 || buf.Count == 0) return [];
        var result = new List<(string, int, int)>();
        var tokens = 0;
        for (var i = buf.Count - 1; i >= 0; i--)
        {
            tokens += EstimateTokens(buf[i].Text);
            if (tokens > overlapTokens) break;
            result.Insert(0, buf[i]);
        }
        return result;
    }

    private static int EstimateTokens(string text) =>
        (int)(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 1.33);
}
