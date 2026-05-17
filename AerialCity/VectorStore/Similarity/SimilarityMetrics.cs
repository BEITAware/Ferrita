using AerialCity.Core.Primitives;

namespace AerialCity.VectorStore.Similarity;

/// <summary>
/// Cosine similarity: measures the cosine of the angle between two vectors.
/// Output range: [-1, 1]. Ideal for normalized embeddings.
/// </summary>
public sealed class CosineSimilarity : ISimilarityMetric
{
    public string Name => "Cosine";
    public float Compute(in EmbeddingVector a, in EmbeddingVector b) => a.CosineSimilarity(in b);
}

/// <summary>
/// Negative Euclidean distance: negated so that higher values = more similar.
/// Output range: (-∞, 0]. Ideal when absolute magnitude matters.
/// </summary>
public sealed class EuclideanDistance : ISimilarityMetric
{
    public string Name => "Euclidean";
    public float Compute(in EmbeddingVector a, in EmbeddingVector b) => -a.EuclideanDistance(in b);
}

/// <summary>
/// Dot product similarity. Output range: (-∞, +∞).
/// Ideal for Matryoshka or unnormalized embeddings.
/// </summary>
public sealed class DotProductSimilarity : ISimilarityMetric
{
    public string Name => "DotProduct";
    public float Compute(in EmbeddingVector a, in EmbeddingVector b) => a.DotProduct(in b);
}
