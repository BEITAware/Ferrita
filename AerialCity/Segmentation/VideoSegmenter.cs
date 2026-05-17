using AerialCity.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace AerialCity.Segmentation;

/// <summary>
/// Segments video content metadata into temporal clip segments.
/// Expects structured text with timestamp markers (e.g., "[00:01:30 - 00:03:45] Scene description").
/// </summary>
public sealed class VideoSegmenter : ISegmenter
{
    private readonly ILogger<VideoSegmenter> _logger;

    public SegmentKind OutputKind => SegmentKind.VideoClip;

    public VideoSegmenter(ILogger<VideoSegmenter> logger) => _logger = logger;

    public IReadOnlyList<Segment> Segment(RawContent content, SegmentationOptions options)
    {
        _logger.LogDebug("Video segmenting {Len} chars", content.Text.Length);

        var segments = new List<Segment>();
        var lines = content.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var offset = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { offset += line.Length + 1; continue; }

            var (start, end, description) = ParseTimestampLine(trimmed);
            if (description is not null)
            {
                var duration = end - start;
                if (duration >= options.MinClipDurationSeconds && duration <= options.MaxClipDurationSeconds)
                {
                    var seg = new Segment(SegmentKind.VideoClip, description)
                    {
                        SourceUri = content.SourceUri,
                        StartOffset = offset,
                        EndOffset = offset + line.Length,
                        Metadata =
                        {
                            ["startTime"] = start,
                            ["endTime"] = end,
                            ["duration"] = duration
                        }
                    };
                    segments.Add(seg);
                }
            }
            offset += line.Length + 1;
        }

        _logger.LogDebug("Video segmentation produced {Count} clips", segments.Count);
        return segments;
    }

    /// <summary>Parses "[HH:MM:SS - HH:MM:SS] Description" format.</summary>
    private static (double Start, double End, string? Description) ParseTimestampLine(string line)
    {
        if (!line.StartsWith('[')) return (0, 0, null);
        var closeBracket = line.IndexOf(']');
        if (closeBracket < 0) return (0, 0, null);

        var timePart = line[1..closeBracket];
        var desc = line[(closeBracket + 1)..].Trim();

        var parts = timePart.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return (0, 0, null);

        if (!TryParseTimestamp(parts[0], out var start) || !TryParseTimestamp(parts[1], out var end))
            return (0, 0, null);

        return (start, end, desc.Length > 0 ? desc : null);
    }

    private static bool TryParseTimestamp(string ts, out double seconds)
    {
        seconds = 0;
        var parts = ts.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return false;

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)
                || !double.TryParse(parts[2], out var s)) return false;
            seconds = h * 3600 + m * 60 + s;
        }
        else
        {
            if (!int.TryParse(parts[0], out var m) || !double.TryParse(parts[1], out var s))
                return false;
            seconds = m * 60 + s;
        }
        return true;
    }
}
