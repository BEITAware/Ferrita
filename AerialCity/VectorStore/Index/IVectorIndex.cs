using AerialCity.Core.Primitives;

namespace AerialCity.VectorStore.Index;

/// <summary>
/// Configuration for the HNSW (Hierarchical Navigable Small World) vector index.
/// </summary>
public sealed class HnswOptions
{
    /// <summary>Maximum number of bi-directional links per node per layer. Default: 16.</summary>
    public int M { get; set; } = 16;

    /// <summary>Size of the dynamic candidate list during construction. Default: 200.</summary>
    public int EfConstruction { get; set; } = 200;

    /// <summary>Size of the dynamic candidate list during search. Default: 50.</summary>
    public int EfSearch { get; set; } = 50;

    /// <summary>Expected dimensionality of vectors. Used for validation.</summary>
    public int Dimensions { get; set; }

    /// <summary>Scaling factor for layer assignment: 1/ln(M). Computed automatically.</summary>
    internal double LevelMultiplier => 1.0 / Math.Log(M);
}

/// <summary>
/// Interface for approximate nearest neighbor (ANN) vector indexes.
/// </summary>
public interface IVectorIndex
{
    /// <summary>Number of vectors currently in the index.</summary>
    int Count { get; }

    /// <summary>Adds a vector with the given identifier to the index.</summary>
    void Add(AerialId id, in EmbeddingVector vector);

    /// <summary>Removes a vector from the index. Returns true if it existed.</summary>
    bool Remove(AerialId id);

    /// <summary>
    /// Searches for the <paramref name="k"/> nearest neighbors of <paramref name="query"/>.
    /// Returns results ordered by descending similarity.
    /// </summary>
    IReadOnlyList<(AerialId Id, float Score)> Search(in EmbeddingVector query, int k);
}
