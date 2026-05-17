using AerialCity.Core.Primitives;
using AerialCity.Retrieval.Scoring;
using Microsoft.Extensions.Logging;

namespace AerialCity.Retrieval.Strategy;

/// <summary>Configuration for hybrid retrieval.</summary>
public sealed class RetrievalOptions
{
    /// <summary>Weight for BM25 scores in hybrid ranking. Default: 0.3.</summary>
    public float Bm25Weight { get; set; } = 0.3f;

    /// <summary>Weight for vector scores in hybrid ranking. Default: 0.7.</summary>
    public float VectorWeight { get; set; } = 0.7f;

    /// <summary>RRF constant k for Reciprocal Rank Fusion. Default: 60.</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Whether to use RRF instead of weighted sum. Default: true.</summary>
    public bool UseRrf { get; set; } = true;
}

/// <summary>Interface for retrieval strategies.</summary>
public interface IRetrievalStrategy
{
    /// <summary>Ranks and merges scored results from multiple scorers.</summary>
    IReadOnlyList<RetrievalResult> Rank(
        IReadOnlyList<Segment> candidates,
        RetrievalQuery query,
        IReadOnlyList<IScorer> scorers);
}

/// <summary>
/// Hybrid retrieval strategy combining BM25 and vector scores using
/// Reciprocal Rank Fusion (RRF) or weighted sum.
/// </summary>
public sealed class HybridRetrievalStrategy : IRetrievalStrategy
{
    private readonly RetrievalOptions _options;
    private readonly ILogger<HybridRetrievalStrategy> _logger;

    public HybridRetrievalStrategy(RetrievalOptions options, ILogger<HybridRetrievalStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<RetrievalResult> Rank(
        IReadOnlyList<Segment> candidates,
        RetrievalQuery query,
        IReadOnlyList<IScorer> scorers)
    {
        _logger.LogDebug("Hybrid ranking {Count} candidates with {Scorers} scorers",
            candidates.Count, scorers.Count);

        if (_options.UseRrf)
            return RankWithRrf(candidates, query, scorers);
        return RankWithWeightedSum(candidates, query, scorers);
    }

    private IReadOnlyList<RetrievalResult> RankWithRrf(
        IReadOnlyList<Segment> candidates, RetrievalQuery query, IReadOnlyList<IScorer> scorers)
    {
        var k = _options.RrfK;

        // Compute per-scorer rankings
        var rankings = new Dictionary<string, List<(Segment Seg, float Score, int Rank)>>();
        foreach (var scorer in scorers)
        {
            var scored = candidates
                .Select(c => (Seg: c, Score: scorer.Score(c, query)))
                .OrderByDescending(x => x.Score)
                .Select((x, i) => (x.Seg, x.Score, Rank: i + 1))
                .ToList();
            rankings[scorer.Name] = scored;
        }

        // RRF: score = Σ 1/(k + rank_i)
        var rrfScores = new Dictionary<AerialId, (Segment Seg, float Score, Dictionary<string, float> Breakdown)>();
        foreach (var (scorerName, ranked) in rankings)
        {
            foreach (var (seg, rawScore, rank) in ranked)
            {
                if (!rrfScores.TryGetValue(seg.Id, out var entry))
                {
                    entry = (seg, 0f, []);
                    rrfScores[seg.Id] = entry;
                }
                var rrfContribution = 1.0f / (k + rank);
                entry.Breakdown[scorerName] = rawScore;
                rrfScores[seg.Id] = (entry.Seg, entry.Score + rrfContribution, entry.Breakdown);
            }
        }

        return rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(query.TopK)
            .Where(x => x.Score >= query.MinScore)
            .Select(x => new RetrievalResult
            {
                Segment = x.Seg,
                Score = x.Score,
                ScoreBreakdown = x.Breakdown
            })
            .ToList();
    }

    private IReadOnlyList<RetrievalResult> RankWithWeightedSum(
        IReadOnlyList<Segment> candidates, RetrievalQuery query, IReadOnlyList<IScorer> scorers)
    {
        var weights = new Dictionary<string, float>
        {
            ["BM25"] = _options.Bm25Weight,
            ["Vector"] = _options.VectorWeight
        };

        return candidates
            .Select(seg =>
            {
                var breakdown = new Dictionary<string, float>();
                var total = 0f;
                foreach (var scorer in scorers)
                {
                    var s = scorer.Score(seg, query);
                    breakdown[scorer.Name] = s;
                    total += s * weights.GetValueOrDefault(scorer.Name, 1.0f);
                }
                return new RetrievalResult { Segment = seg, Score = total, ScoreBreakdown = breakdown };
            })
            .OrderByDescending(r => r.Score)
            .Take(query.TopK)
            .Where(r => r.Score >= query.MinScore)
            .ToList();
    }
}
