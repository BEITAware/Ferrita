using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;
using AerialCity.VectorStore.Index;
using AerialCity.VectorStore.Similarity;
using Microsoft.Extensions.Logging;

namespace AerialCity.VectorStore.Search;

/// <summary>
/// Orchestrates vector similarity search by coordinating the vector index
/// and the storage engine to return fully hydrated search results.
/// </summary>
internal sealed class VectorSearchEngine
{
    private readonly IVectorIndex _index;
    private readonly IStorageEngine _storage;
    private readonly ISimilarityMetric _metric;
    private readonly ILogger<VectorSearchEngine> _logger;

    public VectorSearchEngine(
        IVectorIndex index,
        IStorageEngine storage,
        ISimilarityMetric metric,
        ILogger<VectorSearchEngine> logger)
    {
        _index = index;
        _storage = storage;
        _metric = metric;
        _logger = logger;
    }

    /// <summary>
    /// Searches for segments similar to the given query vector.
    /// </summary>
    /// <param name="query">The query embedding vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="minScore">Minimum similarity score threshold.</param>
    /// <param name="hydrateSegments">Whether to load full segment data from storage.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        EmbeddingVector query,
        int topK = 10,
        float minScore = float.NegativeInfinity,
        bool hydrateSegments = true,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Vector search: topK={TopK}, metric={Metric}", topK, _metric.Name);

        var raw = _index.Search(in query, topK);
        var results = new List<VectorSearchResult>(raw.Count);

        foreach (var (id, score) in raw)
        {
            if (score < minScore) break; // results are descending

            Segment? segment = null;
            if (hydrateSegments)
                segment = await _storage.ReadSegmentAsync(id, ct);

            results.Add(new VectorSearchResult
            {
                SegmentId = id,
                Score = score,
                Segment = segment
            });
        }

        _logger.LogDebug("Vector search returned {Count} results", results.Count);
        return results;
    }

    /// <summary>
    /// Indexes a segment's embedding vector. The segment must have a non-null embedding.
    /// </summary>
    public void IndexSegment(Segment segment)
    {
        if (segment.Embedding is not { } embVec)
            throw new InvalidOperationException($"Segment {segment.Id} has no embedding to index.");
        _index.Add(segment.Id, in embVec);
    }

    /// <summary>Removes a segment from the vector index.</summary>
    public bool RemoveSegment(AerialId id) => _index.Remove(id);
}
