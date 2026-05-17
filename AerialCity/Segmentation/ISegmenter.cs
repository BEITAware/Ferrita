using AerialCity.Core.Primitives;

namespace AerialCity.Segmentation;

/// <summary>
/// Configuration for the segmentation pipeline.
/// </summary>
public sealed class SegmentationOptions
{
    /// <summary>Target maximum number of tokens per text segment. Default: 512.</summary>
    public int MaxTokensPerSegment { get; set; } = 512;

    /// <summary>Number of overlapping tokens between adjacent text segments. Default: 64.</summary>
    public int OverlapTokens { get; set; } = 64;

    /// <summary>For AST segmentation: minimum lines of code per segment. Default: 5.</summary>
    public int MinCodeLines { get; set; } = 5;

    /// <summary>For video segmentation: minimum clip duration in seconds. Default: 5.0.</summary>
    public double MinClipDurationSeconds { get; set; } = 5.0;

    /// <summary>For video segmentation: maximum clip duration in seconds. Default: 120.0.</summary>
    public double MaxClipDurationSeconds { get; set; } = 120.0;
}

/// <summary>
/// Splits raw content into semantically meaningful <see cref="Segment"/> instances.
/// Each implementation handles a specific content modality.
/// </summary>
public interface ISegmenter
{
    /// <summary>The segment kind this segmenter produces.</summary>
    SegmentKind OutputKind { get; }

    /// <summary>
    /// Segments the given raw content into one or more segments.
    /// </summary>
    /// <param name="content">The raw content to segment.</param>
    /// <param name="options">Segmentation configuration.</param>
    /// <returns>An ordered list of segments extracted from the content.</returns>
    IReadOnlyList<Segment> Segment(RawContent content, SegmentationOptions options);
}
