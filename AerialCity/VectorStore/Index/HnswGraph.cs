using System.Collections.Concurrent;
using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using AerialCity.VectorStore.Similarity;

namespace AerialCity.VectorStore.Index;

/// <summary>
/// Self-implemented Hierarchical Navigable Small World (HNSW) graph for
/// approximate nearest neighbor search. This is the primary vector index
/// in AerialCity.
/// </summary>
/// <remarks>
/// <para>
/// HNSW builds a multi-layered navigable small-world graph where the top layers
/// contain few "express-lane" nodes for long-range traversal, and the bottom layer
/// (layer 0) contains all nodes. Search descends from the top layer, greedily
/// finding the nearest neighbor at each layer, then refines at layer 0.
/// </para>
/// <para>
/// Thread safety: reads are concurrent via <see cref="ReaderWriterLockSlim"/>;
/// writes acquire an exclusive lock.
/// </para>
/// <para>
/// Reference: Malkov &amp; Yashunin, "Efficient and Robust Approximate Nearest
/// Neighbor using Hierarchical Navigable Small World Graphs", 2018.
/// </para>
/// </remarks>
public sealed class HnswGraph : IVectorIndex, IDisposable
{
    private readonly ISimilarityMetric _metric;
    private readonly HnswOptions _options;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Random _rng = new();

    // Node storage: maps AerialId → internal node index
    private readonly Dictionary<AerialId, int> _idToIndex = [];
    private readonly List<AerialId> _indexToId = [];
    private readonly List<EmbeddingVector> _vectors = [];

    // Graph layers: layers[layer][nodeIndex] = set of neighbor indices
    private readonly List<Dictionary<int, HashSet<int>>> _layers = [];
    private int _entryPoint = -1;
    private int _maxLevel = -1;

    public int Count => _idToIndex.Count;

    /// <summary>
    /// Creates a new HNSW index with the given similarity metric and options.
    /// </summary>
    public HnswGraph(ISimilarityMetric metric, HnswOptions options)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public void Add(AerialId id, in EmbeddingVector vector)
    {
        if (_options.Dimensions > 0 && vector.Dimensions != _options.Dimensions)
            throw new IndexException($"Expected {_options.Dimensions}d vector, got {vector.Dimensions}d.");

        _lock.EnterWriteLock();
        try
        {
            if (_idToIndex.ContainsKey(id))
                throw new IndexException($"Duplicate vector ID: {id}");

            var nodeIndex = _vectors.Count;
            _idToIndex[id] = nodeIndex;
            _indexToId.Add(id);
            _vectors.Add(vector);

            // Assign a random level using exponential distribution
            var nodeLevel = RandomLevel();

            // Ensure we have enough layers
            while (_layers.Count <= nodeLevel)
                _layers.Add([]);

            // Register this node in all layers up to nodeLevel
            for (var l = 0; l <= nodeLevel; l++)
                _layers[l][nodeIndex] = [];

            if (_entryPoint == -1)
            {
                _entryPoint = nodeIndex;
                _maxLevel = nodeLevel;
                return;
            }

            // Greedy descent from top to nodeLevel+1
            var currentBest = _entryPoint;
            for (var l = _maxLevel; l > nodeLevel; l--)
            {
                currentBest = GreedyClosest(vector, currentBest, l);
            }

            // Insert at each layer from nodeLevel down to 0
            for (var l = Math.Min(nodeLevel, _maxLevel); l >= 0; l--)
            {
                var neighbors = SearchLayer(vector, currentBest, _options.EfConstruction, l);
                var selected = SelectNeighbors(neighbors, _options.M);

                _layers[l][nodeIndex] = new HashSet<int>(selected.Select(n => n.Index));

                foreach (var (nIdx, _) in selected)
                {
                    if (!_layers[l].TryGetValue(nIdx, out var nNeighbors))
                    {
                        nNeighbors = [];
                        _layers[l][nIdx] = nNeighbors;
                    }
                    nNeighbors.Add(nodeIndex);

                    // Trim if over capacity
                    var maxConn = l == 0 ? _options.M * 2 : _options.M;
                    if (nNeighbors.Count > maxConn)
                        TrimConnections(nIdx, nNeighbors, maxConn, l);
                }

                if (neighbors.Count > 0)
                    currentBest = neighbors[0].Index;
            }

            if (nodeLevel > _maxLevel)
            {
                _maxLevel = nodeLevel;
                _entryPoint = nodeIndex;
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <inheritdoc />
    public bool Remove(AerialId id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_idToIndex.TryGetValue(id, out var nodeIndex))
                return false;

            // Remove from all layers
            for (var l = 0; l < _layers.Count; l++)
            {
                if (_layers[l].Remove(nodeIndex, out var neighbors))
                {
                    foreach (var n in neighbors)
                    {
                        if (_layers[l].TryGetValue(n, out var nn))
                            nn.Remove(nodeIndex);
                    }
                }
            }

            _idToIndex.Remove(id);

            // If we removed the entry point, find a new one
            if (nodeIndex == _entryPoint)
            {
                _entryPoint = _idToIndex.Count > 0 ? _idToIndex.Values.First() : -1;
                _maxLevel = _entryPoint == -1 ? -1 : GetNodeLevel(_entryPoint);
            }

            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<(AerialId Id, float Score)> Search(in EmbeddingVector query, int k)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));

        _lock.EnterReadLock();
        try
        {
            if (_entryPoint == -1) return [];

            var currentBest = _entryPoint;

            // Greedy descent through upper layers
            for (var l = _maxLevel; l > 0; l--)
                currentBest = GreedyClosest(query, currentBest, l);

            // Search layer 0 with efSearch candidates
            var candidates = SearchLayer(query, currentBest, Math.Max(k, _options.EfSearch), 0);

            return candidates
                .Take(k)
                .Select(c => (_indexToId[c.Index], c.Score))
                .ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Internal algorithms ──────────────────────────────────────────

    private int RandomLevel()
    {
        var level = 0;
        while (_rng.NextDouble() < _options.LevelMultiplier && level < 32)
            level++;
        return level;
    }

    private int GetNodeLevel(int nodeIndex)
    {
        for (var l = _layers.Count - 1; l >= 0; l--)
            if (_layers[l].ContainsKey(nodeIndex))
                return l;
        return 0;
    }

    private float Similarity(in EmbeddingVector a, int bIndex)
    {
        var b = _vectors[bIndex];
        return _metric.Compute(in a, in b);
    }

    private int GreedyClosest(in EmbeddingVector query, int entryIndex, int layer)
    {
        var best = entryIndex;
        var bestScore = Similarity(in query, best);
        var improved = true;

        while (improved)
        {
            improved = false;
            if (!_layers[layer].TryGetValue(best, out var neighbors)) break;
            foreach (var n in neighbors)
            {
                var score = Similarity(in query, n);
                if (score > bestScore)
                {
                    best = n;
                    bestScore = score;
                    improved = true;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Beam search within a single layer, returning up to ef nearest candidates.
    /// </summary>
    private List<(int Index, float Score)> SearchLayer(
        in EmbeddingVector query, int entryIndex, int ef, int layer)
    {
        var visited = new HashSet<int> { entryIndex };
        var entryScore = Similarity(in query, entryIndex);

        // Candidates: max-heap by score (best first)
        var candidates = new PriorityQueue<int, float>();
        candidates.Enqueue(entryIndex, -entryScore); // negate for min-heap → max-heap

        var results = new SortedList<float, int>(Comparer<float>.Create((a, b) => b.CompareTo(a)));
        results.Add(entryScore, entryIndex);

        while (candidates.Count > 0)
        {
            candidates.TryDequeue(out var current, out var negScore);
            var currentScore = -negScore;

            // Stop if the best candidate is worse than the worst result
            if (results.Count >= ef && currentScore < results.Keys[results.Count - 1])
                break;

            if (!_layers[layer].TryGetValue(current, out var neighbors)) continue;

            foreach (var n in neighbors)
            {
                if (!visited.Add(n)) continue;
                var nScore = Similarity(in query, n);

                if (results.Count < ef || nScore > results.Keys[results.Count - 1])
                {
                    candidates.Enqueue(n, -nScore);

                    // Handle duplicate scores by adding small perturbation
                    while (results.ContainsKey(nScore))
                        nScore = MathF.BitIncrement(nScore);
                    results.Add(nScore, n);

                    if (results.Count > ef)
                        results.RemoveAt(results.Count - 1);
                }
            }
        }

        return results.Select(kv => (kv.Value, kv.Key)).ToList();
    }

    private List<(int Index, float Score)> SelectNeighbors(
        List<(int Index, float Score)> candidates, int maxCount)
    {
        // Simple heuristic: take the top-M by score
        return candidates.Take(maxCount).ToList();
    }

    private void TrimConnections(int nodeIndex, HashSet<int> neighbors, int maxCount, int layer)
    {
        var vec = _vectors[nodeIndex];
        var scored = neighbors
            .Select(n => (Index: n, Score: Similarity(in vec, n)))
            .OrderByDescending(x => x.Score)
            .Take(maxCount)
            .Select(x => x.Index)
            .ToHashSet();

        neighbors.IntersectWith(scored);
    }

    public void Dispose() => _lock.Dispose();
}
