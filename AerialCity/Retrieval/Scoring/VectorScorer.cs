using AerialCity.Core.Primitives;
using AerialCity.VectorStore.Similarity;

namespace AerialCity.Retrieval.Scoring;

/// <summary>Interface for scoring segments against a query.</summary>
public interface IScorer
{
    /// <summary>Human-readable name of this scorer.</summary>
    string Name { get; }

    /// <summary>Scores a segment against the query. Higher = more relevant.</summary>
    float Score(Segment segment, RetrievalQuery query);
}

/// <summary>
/// Vector similarity scorer. Uses cosine similarity between query vector
/// and segment embedding.
/// </summary>
public sealed class VectorScorer : IScorer
{
    private readonly ISimilarityMetric _metric;

    /// <summary>Creates a vector scorer that uses cosine similarity.</summary>
    public VectorScorer()
        : this(new CosineSimilarity())
    {
    }

    /// <summary>Creates a vector scorer that uses the supplied similarity metric.</summary>
    public VectorScorer(ISimilarityMetric metric)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
    }

    /// <inheritdoc />
    public string Name => "Vector";

    /// <inheritdoc />
    public float Score(Segment segment, RetrievalQuery query)
    {
        if (query.QueryVector is not { } qv || segment.Embedding is not { } emb)
            return 0f;
        return _metric.Compute(in qv, in emb);
    }
}
