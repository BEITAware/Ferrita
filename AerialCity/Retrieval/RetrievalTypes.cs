using AerialCity.Core.Primitives;

namespace AerialCity.Retrieval;

/// <summary>A single retrieval result with its score and source information.</summary>
public sealed class RetrievalResult
{
    /// <summary>The matched segment.</summary>
    public required Segment Segment { get; init; }

    /// <summary>Combined relevance score (higher = more relevant).</summary>
    public required float Score { get; init; }

    /// <summary>Individual scores from each scoring strategy.</summary>
    public Dictionary<string, float> ScoreBreakdown { get; init; } = [];
}

/// <summary>A retrieval query combining text and/or vector search.</summary>
public sealed class RetrievalQuery
{
    /// <summary>The text query for BM25 scoring.</summary>
    public string? TextQuery { get; init; }

    /// <summary>A pre-computed query embedding vector for vector search.</summary>
    public EmbeddingVector? QueryVector { get; init; }

    /// <summary>Maximum number of results.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Minimum score threshold.</summary>
    public float MinScore { get; init; } = float.NegativeInfinity;

    /// <summary>Filter by collection name.</summary>
    public string? CollectionFilter { get; init; }

    /// <summary>Filter by segment kind.</summary>
    public SegmentKind? KindFilter { get; init; }
}
