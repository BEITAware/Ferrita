using AerialCity.Core.Primitives;
using AerialCity.GraphStore.Model;

namespace AerialCity.GraphStore.Index;

/// <summary>
/// Interface for graph adjacency indexing, supporting efficient neighbor lookups.
/// </summary>
public interface IAdjacencyIndex
{
    /// <summary>Adds a node to the index.</summary>
    void AddNode(GraphNode node);

    /// <summary>Removes a node and all its incident edges.</summary>
    bool RemoveNode(AerialId nodeId);

    /// <summary>Adds a directed edge to the index.</summary>
    void AddEdge(GraphEdge edge);

    /// <summary>Removes an edge by ID.</summary>
    bool RemoveEdge(AerialId edgeId);

    /// <summary>Returns all outgoing edges from the given node.</summary>
    IReadOnlyList<GraphEdge> GetOutgoing(AerialId nodeId);

    /// <summary>Returns all incoming edges to the given node.</summary>
    IReadOnlyList<GraphEdge> GetIncoming(AerialId nodeId);

    /// <summary>Returns outgoing edges filtered by kind.</summary>
    IReadOnlyList<GraphEdge> GetOutgoing(AerialId nodeId, EdgeKind kind);

    /// <summary>Returns incoming edges filtered by kind.</summary>
    IReadOnlyList<GraphEdge> GetIncoming(AerialId nodeId, EdgeKind kind);

    /// <summary>Gets a node by its ID, or null if not found.</summary>
    GraphNode? GetNode(AerialId nodeId);

    /// <summary>Total number of nodes in the index.</summary>
    int NodeCount { get; }

    /// <summary>Total number of edges in the index.</summary>
    int EdgeCount { get; }
}

/// <summary>
/// In-memory adjacency list implementation of <see cref="IAdjacencyIndex"/>.
/// Uses dictionaries of hash sets for O(1) neighbor access.
/// Thread-safe via <see cref="ReaderWriterLockSlim"/>.
/// </summary>
public sealed class AdjacencyListIndex : IAdjacencyIndex, IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<AerialId, GraphNode> _nodes = [];
    private readonly Dictionary<AerialId, GraphEdge> _edges = [];
    private readonly Dictionary<AerialId, List<AerialId>> _outgoing = [];
    private readonly Dictionary<AerialId, List<AerialId>> _incoming = [];

    public int NodeCount { get { _lock.EnterReadLock(); try { return _nodes.Count; } finally { _lock.ExitReadLock(); } } }
    public int EdgeCount { get { _lock.EnterReadLock(); try { return _edges.Count; } finally { _lock.ExitReadLock(); } } }

    public void AddNode(GraphNode node)
    {
        _lock.EnterWriteLock();
        try
        {
            _nodes[node.Id] = node;
            _outgoing.TryAdd(node.Id, []);
            _incoming.TryAdd(node.Id, []);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool RemoveNode(AerialId nodeId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_nodes.Remove(nodeId)) return false;

            // Remove all incident edges
            if (_outgoing.Remove(nodeId, out var outEdges))
                foreach (var eId in outEdges) _edges.Remove(eId);
            if (_incoming.Remove(nodeId, out var inEdges))
                foreach (var eId in inEdges) _edges.Remove(eId);

            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddEdge(GraphEdge edge)
    {
        _lock.EnterWriteLock();
        try
        {
            _edges[edge.Id] = edge;
            if (!_outgoing.TryGetValue(edge.SourceId, out var outList))
            { outList = []; _outgoing[edge.SourceId] = outList; }
            outList.Add(edge.Id);

            if (!_incoming.TryGetValue(edge.TargetId, out var inList))
            { inList = []; _incoming[edge.TargetId] = inList; }
            inList.Add(edge.Id);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool RemoveEdge(AerialId edgeId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_edges.Remove(edgeId, out var edge)) return false;
            _outgoing.GetValueOrDefault(edge.SourceId)?.Remove(edgeId);
            _incoming.GetValueOrDefault(edge.TargetId)?.Remove(edgeId);
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public IReadOnlyList<GraphEdge> GetOutgoing(AerialId nodeId) =>
        GetEdgesLocked(nodeId, _outgoing);

    public IReadOnlyList<GraphEdge> GetIncoming(AerialId nodeId) =>
        GetEdgesLocked(nodeId, _incoming);

    public IReadOnlyList<GraphEdge> GetOutgoing(AerialId nodeId, EdgeKind kind) =>
        GetEdgesLocked(nodeId, _outgoing).Where(e => e.Kind == kind).ToList();

    public IReadOnlyList<GraphEdge> GetIncoming(AerialId nodeId, EdgeKind kind) =>
        GetEdgesLocked(nodeId, _incoming).Where(e => e.Kind == kind).ToList();

    public GraphNode? GetNode(AerialId nodeId)
    {
        _lock.EnterReadLock();
        try { return _nodes.GetValueOrDefault(nodeId); }
        finally { _lock.ExitReadLock(); }
    }

    private IReadOnlyList<GraphEdge> GetEdgesLocked(
        AerialId nodeId, Dictionary<AerialId, List<AerialId>> adjacency)
    {
        _lock.EnterReadLock();
        try
        {
            if (!adjacency.TryGetValue(nodeId, out var edgeIds)) return [];
            return edgeIds.Where(_edges.ContainsKey).Select(id => _edges[id]).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
