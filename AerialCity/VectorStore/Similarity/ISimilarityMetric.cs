using AerialCity.Core.Primitives;

namespace AerialCity.VectorStore.Similarity;

/// <summary>
/// Defines a similarity (or distance) metric between two embedding vectors.
/// Implementations must be stateless and thread-safe.
/// </summary>
public interface ISimilarityMetric
{
    /// <summary>Human-readable name of this metric (e.g., "Cosine", "Euclidean").</summary>
    string Name { get; }

    /// <summary>
    /// Computes the similarity score between two vectors.
    /// Higher values always indicate greater similarity, regardless of the
    /// underlying metric (distance metrics are negated or inverted).
    /// </summary>
    float Compute(in EmbeddingVector a, in EmbeddingVector b);
}
