using AerialCity.Core.Primitives;

namespace AerialCity.VectorStore.Search;

/// <summary>
/// Represents a single result from a vector similarity search.
/// </summary>
public sealed class VectorSearchResult
{
    /// <summary>The identifier of the matched segment.</summary>
    public required AerialId SegmentId { get; init; }

    /// <summary>The similarity score (higher = more similar).</summary>
    public required float Score { get; init; }

    /// <summary>The matched segment, if populated by the search engine.</summary>
    public Segment? Segment { get; init; }
}
