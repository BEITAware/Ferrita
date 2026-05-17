using AerialCity.Core.Primitives;
using AerialCity.Embedding;
using AerialCity.Retrieval.Strategy;
using AerialCity.VectorStore.Index;

namespace AerialCity.Retrieval;

/// <summary>
/// Retrieval mode used by the API-backed retrieval delegate.
/// </summary>
public enum RetrievalMethod
{
    /// <summary>Combines BM25 text scoring and vector similarity when both are available.</summary>
    Hybrid = 0,

    /// <summary>Uses lexical BM25 scoring only.</summary>
    BM25 = 1,

    /// <summary>Uses cosine vector similarity only.</summary>
    Cosine = 2,

    /// <summary>Alias for cosine vector similarity.</summary>
    Vector = Cosine,

    /// <summary>Uses dot product vector similarity only.</summary>
    DotProduct = 3,

    /// <summary>Uses negative Euclidean distance as the vector score.</summary>
    Euclidean = 4
}

/// <summary>
/// Request data for the API-backed retrieval delegate.
/// </summary>
public sealed class ApiRetrievalRequest
{
    /// <summary>API key used by the selected embedding provider. Required for vector-based methods.</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Base URL for the embedding provider. If omitted, AerialCity uses the provider's public API base URL.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>Embedding provider API dialect used to embed the retrieval query.</summary>
    public EmbeddingApiType ApiType { get; init; }

    /// <summary>Embedding model name used to embed the retrieval query.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Content to retrieve against the database. If omitted, <see cref="TextQuery"/> is embedded for vector methods.
    /// </summary>
    public EmbeddingInput? Content { get; init; }

    /// <summary>
    /// Text query used for BM25 scoring and as a convenience embedding input when <see cref="Content"/> is omitted.
    /// </summary>
    public string? TextQuery { get; init; }

    /// <summary>
    /// Provider-specific embedding request parameters, such as dimensions, taskType, or outputDimensionality.
    /// </summary>
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /// <summary>
    /// Optional dimensionality convenience property for the embedding request.
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>Whether to L2-normalize the query embedding before retrieval.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>
    /// Includes base64 binary payloads in SkyweaverPreservedContent XML blocks for the embedding request.
    /// </summary>
    public bool IncludeBinaryDataInTextProjection { get; init; }

    /// <summary>The retrieval method to execute.</summary>
    public RetrievalMethod Method { get; init; } = RetrievalMethod.Hybrid;

    /// <summary>
    /// Database directory, or a storage base directory when <see cref="DatabaseName"/> is also supplied.
    /// </summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// Optional database name. When supplied, the opened database path is DatabasePath/DatabaseName.
    /// </summary>
    public string? DatabaseName { get; init; }

    /// <summary>Maximum number of results.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Minimum score threshold.</summary>
    public float MinScore { get; init; } = float.NegativeInfinity;

    /// <summary>Filter by collection name.</summary>
    public string? CollectionFilter { get; init; }

    /// <summary>Filter by segment kind.</summary>
    public SegmentKind? KindFilter { get; init; }

    /// <summary>Optional retrieval strategy options. If omitted, single-method retrieval returns raw scorer values.</summary>
    public RetrievalOptions? RetrievalOptions { get; init; }

    /// <summary>Optional HNSW options used while rebuilding the in-memory vector index.</summary>
    public HnswOptions? HnswOptions { get; init; }

    /// <summary>Optional file store page size for opening the database.</summary>
    public int? PageSizeBytes { get; init; }

    /// <summary>Whether to enable the write-ahead log while opening the database. Default is false for retrieval.</summary>
    public bool EnableWal { get; init; }

    /// <summary>Optional maximum WAL size when <see cref="EnableWal"/> is true.</summary>
    public long? MaxWalSizeBytes { get; init; }
}
