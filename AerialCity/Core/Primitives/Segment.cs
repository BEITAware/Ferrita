namespace AerialCity.Core.Primitives;

/// <summary>
/// The fundamental unit of content in AerialCity.
/// A segment is a semantically meaningful slice of a larger document or media asset,
/// produced by the segmentation pipeline and stored in the database with optional
/// embedding vectors for retrieval and graph edges for tracing.
/// </summary>
/// <remarks>
/// <para>
/// Segments are created by <see cref="AerialCity.Segmentation.ISegmenter"/> implementations
/// and flow through the embedding pipeline before being indexed in both the vector store
/// (for similarity search) and the graph store (for relationship tracing).
/// </para>
/// <para>
/// <b>Lifecycle:</b> RawContent → Segmentation → Segment → Embedding → Indexed Segment
/// </para>
/// </remarks>
public sealed class Segment
{
    /// <summary>The unique identifier for this segment.</summary>
    public AerialId Id { get; }

    /// <summary>The kind/modality of content this segment represents.</summary>
    public SegmentKind Kind { get; }

    /// <summary>
    /// The textual content of this segment. For code, this is the source text.
    /// For text passages, this is the prose. For video clips, this may be a
    /// transcript or description. For images, this may be a caption or alt text.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// Optional binary payload for non-textual content (images, video frames, etc.).
    /// </summary>
    public ReadOnlyMemory<byte>? BinaryContent { get; init; }

    /// <summary>
    /// The embedding vector for this segment, populated after the embedding pipeline.
    /// <c>null</c> if the segment has not yet been embedded.
    /// </summary>
    public EmbeddingVector? Embedding { get; set; }

    /// <summary>
    /// The URI of the source document/file/media from which this segment was extracted.
    /// </summary>
    public string? SourceUri { get; init; }

    /// <summary>
    /// The zero-based character offset in the source document where this segment begins.
    /// </summary>
    public int StartOffset { get; init; }

    /// <summary>
    /// The zero-based character offset in the source document where this segment ends (exclusive).
    /// </summary>
    public int EndOffset { get; init; }

    /// <summary>
    /// The name of the collection this segment belongs to within the database.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// Extensible key-value metadata attached to this segment.
    /// Examples: language, author, function name, class name, timestamp range, etc.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>When this segment was first created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this segment was last modified, or <c>null</c> if never modified.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Creates a new segment with an auto-generated identifier and creation timestamp.
    /// </summary>
    /// <param name="kind">The modality of the content.</param>
    /// <param name="content">The textual content of the segment.</param>
    public Segment(SegmentKind kind, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Id = AerialId.NewId();
        Kind = kind;
        Content = content;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates a segment with an explicit identifier (used during deserialization / reconstruction).
    /// </summary>
    internal Segment(AerialId id, SegmentKind kind, string content, DateTimeOffset createdAt)
    {
        Id = id;
        Kind = kind;
        Content = content;
        CreatedAt = createdAt;
    }

    public override string ToString() =>
        $"Segment[{Id}, {Kind}, {Content.Length} chars, embedded={Embedding.HasValue}]";
}
