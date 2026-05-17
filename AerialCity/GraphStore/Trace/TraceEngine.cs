using AerialCity.Core.Primitives;
using AerialCity.GraphStore.Index;
using AerialCity.GraphStore.Model;
using AerialCity.GraphStore.Traversal;
using Microsoft.Extensions.Logging;

namespace AerialCity.GraphStore.Trace;

/// <summary>
/// Defines what to trace and how.
/// </summary>
public sealed class TraceQuery
{
    /// <summary>The segment ID to start tracing from.</summary>
    public required AerialId SegmentId { get; init; }

    /// <summary>Which edge kinds to follow. If empty, follows all kinds.</summary>
    public HashSet<EdgeKind> EdgeKinds { get; init; } = [];

    /// <summary>Maximum depth of the trace (number of hops).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>If true, also traverse incoming edges (bidirectional trace).</summary>
    public bool Bidirectional { get; init; }
}

/// <summary>
/// A single hit in a trace result.
/// </summary>
public sealed class TraceHit
{
    /// <summary>The node that was found.</summary>
    public required GraphNode Node { get; init; }

    /// <summary>The relationship through which this node was reached.</summary>
    public required EdgeKind Relationship { get; init; }

    /// <summary>Number of hops from the query node.</summary>
    public required int Depth { get; init; }

    /// <summary>The edge weight / confidence.</summary>
    public float Weight { get; init; } = 1.0f;
}

/// <summary>
/// The result of a trace operation.
/// </summary>
public sealed class TraceResult
{
    /// <summary>The segment ID that was traced.</summary>
    public required AerialId QuerySegmentId { get; init; }

    /// <summary>All trace hits found.</summary>
    public IReadOnlyList<TraceHit> Hits { get; init; } = [];

    /// <summary>All edges in the traversal path.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];
}

/// <summary>
/// High-level engine for the Trace functionality.
/// Traces relationships (callers/callees, entity mentions) through the graph store.
/// </summary>
internal sealed class TraceEngine
{
    private readonly IAdjacencyIndex _index;
    private readonly IGraphTraverser _traverser;
    private readonly ILogger<TraceEngine> _logger;

    public TraceEngine(
        IAdjacencyIndex index,
        IGraphTraverser traverser,
        ILogger<TraceEngine> logger)
    {
        _index = index;
        _traverser = traverser;
        _logger = logger;
    }

    /// <summary>
    /// Executes a trace query, finding all related nodes reachable from the query segment.
    /// </summary>
    public TraceResult Execute(TraceQuery query)
    {
        _logger.LogDebug("Tracing segment {SegmentId}, depth={Depth}, kinds={Kinds}",
            query.SegmentId, query.MaxDepth,
            query.EdgeKinds.Count > 0 ? string.Join(",", query.EdgeKinds) : "all");

        // Find the graph node for this segment
        // We need to search by segment ID across nodes
        var startNodeId = FindNodeBySegmentId(query.SegmentId);
        if (startNodeId is null)
        {
            _logger.LogWarning("No graph node found for segment {SegmentId}", query.SegmentId);
            return new TraceResult { QuerySegmentId = query.SegmentId };
        }

        Func<GraphEdge, bool>? filter = query.EdgeKinds.Count > 0
            ? edge => query.EdgeKinds.Contains(edge.Kind)
            : null;

        var forward = _traverser.Traverse(_index, startNodeId.Value, query.MaxDepth, filter);

        var allHits = new List<TraceHit>();
        var allEdges = new List<GraphEdge>(forward.Edges);

        foreach (var (nodeId, node) in forward.Nodes)
        {
            if (nodeId == startNodeId.Value) continue;
            var depth = forward.Depths[nodeId];
            var incomingEdge = forward.Edges.LastOrDefault(e => e.TargetId == nodeId);
            allHits.Add(new TraceHit
            {
                Node = node,
                Relationship = incomingEdge?.Kind ?? EdgeKind.Custom,
                Depth = depth,
                Weight = incomingEdge?.Weight ?? 1.0f
            });
        }

        // Bidirectional: also traverse incoming edges
        if (query.Bidirectional)
        {
            var reverseResult = TraverseIncoming(startNodeId.Value, query.MaxDepth, filter);
            allHits.AddRange(reverseResult.hits);
            allEdges.AddRange(reverseResult.edges);
        }

        _logger.LogDebug("Trace found {Count} hits", allHits.Count);
        return new TraceResult
        {
            QuerySegmentId = query.SegmentId,
            Hits = allHits,
            Edges = allEdges
        };
    }

    private AerialId? FindNodeBySegmentId(AerialId segmentId)
    {
        // Linear scan — in production, maintain a segmentId→nodeId index
        // This is a known optimization point
        // For now, we rely on node.SegmentId matching
        // We iterate through nodes via edges starting from any known entry
        // TODO: Add a dedicated segment→node lookup index
        return null; // Will be wired up when collection manages nodes
    }

    private (List<TraceHit> hits, List<GraphEdge> edges) TraverseIncoming(
        AerialId nodeId, int maxDepth, Func<GraphEdge, bool>? filter)
    {
        var hits = new List<TraceHit>();
        var edges = new List<GraphEdge>();
        var visited = new HashSet<AerialId> { nodeId };
        var queue = new Queue<(AerialId Id, int Depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var incoming = _index.GetIncoming(current);
            foreach (var edge in incoming)
            {
                if (filter is not null && !filter(edge)) continue;
                edges.Add(edge);

                if (!visited.Add(edge.SourceId)) continue;
                var sourceNode = _index.GetNode(edge.SourceId);
                if (sourceNode is null) continue;

                hits.Add(new TraceHit
                {
                    Node = sourceNode,
                    Relationship = edge.Kind,
                    Depth = depth + 1,
                    Weight = edge.Weight
                });
                queue.Enqueue((edge.SourceId, depth + 1));
            }
        }

        return (hits, edges);
    }
}
