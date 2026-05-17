using AerialCity.Core.Primitives;
using AerialCity.GraphStore.Index;
using AerialCity.GraphStore.Model;

namespace AerialCity.GraphStore.Traversal;

/// <summary>
/// Result of a graph traversal, containing all visited nodes, edges, and paths.
/// </summary>
public sealed class TraversalResult
{
    /// <summary>All nodes visited during traversal, keyed by their ID.</summary>
    public IReadOnlyDictionary<AerialId, GraphNode> Nodes { get; init; } = new Dictionary<AerialId, GraphNode>();

    /// <summary>All edges traversed.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    /// <summary>Depth of each visited node from the start node.</summary>
    public IReadOnlyDictionary<AerialId, int> Depths { get; init; } = new Dictionary<AerialId, int>();
}

/// <summary>
/// Interface for graph traversal strategies.
/// </summary>
public interface IGraphTraverser
{
    /// <summary>
    /// Traverses the graph starting from <paramref name="startNodeId"/>,
    /// following edges matching the given <paramref name="edgeFilter"/>.
    /// </summary>
    /// <param name="index">The adjacency index to traverse.</param>
    /// <param name="startNodeId">The starting node.</param>
    /// <param name="maxDepth">Maximum traversal depth.</param>
    /// <param name="edgeFilter">Optional filter on which edge kinds to follow.</param>
    TraversalResult Traverse(
        IAdjacencyIndex index,
        AerialId startNodeId,
        int maxDepth = 3,
        Func<GraphEdge, bool>? edgeFilter = null);
}

/// <summary>
/// Breadth-first graph traversal. Visits nodes layer by layer from the start node.
/// </summary>
public sealed class BfsTraverser : IGraphTraverser
{
    public TraversalResult Traverse(
        IAdjacencyIndex index,
        AerialId startNodeId,
        int maxDepth = 3,
        Func<GraphEdge, bool>? edgeFilter = null)
    {
        var visited = new Dictionary<AerialId, GraphNode>();
        var depths = new Dictionary<AerialId, int>();
        var allEdges = new List<GraphEdge>();

        var startNode = index.GetNode(startNodeId);
        if (startNode is null) return new TraversalResult();

        var queue = new Queue<(AerialId NodeId, int Depth)>();
        queue.Enqueue((startNodeId, 0));
        visited[startNodeId] = startNode;
        depths[startNodeId] = 0;

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var outgoing = index.GetOutgoing(currentId);
            foreach (var edge in outgoing)
            {
                if (edgeFilter is not null && !edgeFilter(edge)) continue;

                allEdges.Add(edge);

                if (visited.ContainsKey(edge.TargetId)) continue;

                var targetNode = index.GetNode(edge.TargetId);
                if (targetNode is null) continue;

                visited[edge.TargetId] = targetNode;
                depths[edge.TargetId] = depth + 1;
                queue.Enqueue((edge.TargetId, depth + 1));
            }
        }

        return new TraversalResult
        {
            Nodes = visited,
            Edges = allEdges,
            Depths = depths
        };
    }
}
