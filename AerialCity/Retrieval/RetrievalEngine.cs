using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;
using AerialCity.Retrieval.Scoring;
using AerialCity.Retrieval.Strategy;
using AerialCity.VectorStore.Search;
using AerialCity.VectorStore.Similarity;
using Microsoft.Extensions.Logging;

namespace AerialCity.Retrieval;

/// <summary>
/// Main retrieval orchestrator. Coordinates vector search, BM25 scoring,
/// and hybrid ranking to produce final retrieval results.
/// </summary>
internal sealed class RetrievalEngine
{
    private readonly VectorSearchEngine _vectorSearch;
    private readonly Bm25Scorer _bm25;
    private readonly IRetrievalStrategy _strategy;
    private readonly IStorageEngine _storage;
    private readonly ISimilarityMetric _vectorMetric;
    private readonly ILogger<RetrievalEngine> _logger;

    public RetrievalEngine(
        VectorSearchEngine vectorSearch,
        Bm25Scorer bm25,
        IRetrievalStrategy strategy,
        IStorageEngine storage,
        ILogger<RetrievalEngine> logger,
        ISimilarityMetric? vectorMetric = null)
    {
        _vectorSearch = vectorSearch;
        _bm25 = bm25;
        _strategy = strategy;
        _storage = storage;
        _vectorMetric = vectorMetric ?? new CosineSimilarity();
        _logger = logger;
    }

    /// <summary>
    /// Executes a retrieval query, combining vector search and BM25 scoring.
    /// </summary>
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalQuery query, CancellationToken ct = default)
    {
        _logger.LogDebug("Retrieval: text={HasText}, vector={HasVec}, topK={K}",
            query.TextQuery is not null, query.QueryVector.HasValue, query.TopK);

        var candidates = new Dictionary<AerialId, Segment>();

        // Phase 1: Vector search for initial candidates
        if (query.QueryVector.HasValue)
        {
            var vectorResults = await _vectorSearch.SearchAsync(
                query.QueryVector.Value, query.TopK * 2, ct: ct);

            foreach (var r in vectorResults)
            {
                if (r.Segment is not null)
                    candidates.TryAdd(r.SegmentId, r.Segment);
            }
        }

        // Phase 2: If text query but no vector results, scan storage
        if (query.TextQuery is not null && candidates.Count == 0)
        {
            await foreach (var id in _storage.ListSegmentIdsAsync(ct))
            {
                var seg = await _storage.ReadSegmentAsync(id, ct);
                if (seg is not null) candidates.TryAdd(id, seg);
            }
        }

        // Phase 3: Hybrid ranking
        var scorers = new List<IScorer>();
        if (query.TextQuery is not null) scorers.Add(_bm25);
        if (query.QueryVector.HasValue) scorers.Add(new VectorScorer(_vectorMetric));

        var candidateList = candidates.Values
            .Where(s => query.KindFilter is null || s.Kind == query.KindFilter)
            .Where(s => query.CollectionFilter is null || s.CollectionName == query.CollectionFilter)
            .ToList();

        var results = _strategy.Rank(candidateList, query, scorers);

        _logger.LogDebug("Retrieval returned {Count} results", results.Count);
        return results;
    }
}
