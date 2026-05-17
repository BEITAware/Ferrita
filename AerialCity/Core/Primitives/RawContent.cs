namespace AerialCity.Core.Primitives;

/// <summary>
/// Represents raw, unsegmented content that will be fed into the segmentation pipeline.
/// This is the input to <see cref="AerialCity.Segmentation.ISegmenter"/> implementations,
/// which will split it into one or more <see cref="Segment"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Example sources:</b> a .cs source file, a chapter of a novel, a video file reference,
/// a JSON document, an image file path.
/// </para>
/// </remarks>
public sealed class RawContent
{
    /// <summary>The textual representation of the content (source code, prose, transcript, etc.).</summary>
    public string Text { get; }

    /// <summary>Optional binary payload for non-textual content.</summary>
    public ReadOnlyMemory<byte>? Binary { get; init; }

    /// <summary>The URI identifying the source of this content (file path, URL, etc.).</summary>
    public string? SourceUri { get; init; }

    /// <summary>
    /// The suggested modality for segmentation. The segmenter may override this
    /// if it can auto-detect the content type.
    /// </summary>
    public SegmentKind SuggestedKind { get; init; } = SegmentKind.TextPassage;

    /// <summary>
    /// Optional language hint for code content (e.g., "csharp", "python").
    /// Used by AST segmenters to select the appropriate parser.
    /// </summary>
    public string? LanguageHint { get; init; }

    /// <summary>
    /// Extensible metadata about the source content.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Creates a new raw content instance.
    /// </summary>
    /// <param name="text">The textual content to segment.</param>
    public RawContent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>
    /// Convenience factory for source code content.
    /// </summary>
    public static RawContent FromCode(string sourceCode, string language, string? sourceUri = null) =>
        new(sourceCode)
        {
            SuggestedKind = SegmentKind.CodeBlock,
            LanguageHint = language,
            SourceUri = sourceUri
        };

    /// <summary>
    /// Convenience factory for text/prose content.
    /// </summary>
    public static RawContent FromText(string text, string? sourceUri = null) =>
        new(text)
        {
            SuggestedKind = SegmentKind.TextPassage,
            SourceUri = sourceUri
        };
}
