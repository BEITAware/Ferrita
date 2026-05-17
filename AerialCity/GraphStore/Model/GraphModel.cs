using AerialCity.Core.Primitives;

namespace AerialCity.GraphStore.Model;

/// <summary>
/// Classifies the type of relationship between two graph nodes.
/// These edges form the basis of the Trace functionality in AerialCity.
/// </summary>
public enum EdgeKind
{
    /// <summary>Source node calls or invokes the target node (code: function call).</summary>
    Calls = 0,
    /// <summary>Inverse of Calls — the target is called by the source.</summary>
    CalledBy = 1,
    /// <summary>Source node mentions or references the target (text: entity mention).</summary>
    Mentions = 2,
    /// <summary>Inverse of Mentions.</summary>
    MentionedBy = 3,
    /// <summary>Source node imports or depends on the target.</summary>
    DependsOn = 4,
    /// <summary>Source node is a parent/container of the target (class→method, chapter→paragraph).</summary>
    Contains = 5,
    /// <summary>Inverse of Contains.</summary>
    ContainedBy = 6,
    /// <summary>Two nodes are semantically similar (auto-detected).</summary>
    SimilarTo = 7,
    /// <summary>Source node is a temporal successor of the target (video: next clip).</summary>
    FollowedBy = 8,
    /// <summary>A user-defined relationship.</summary>
    Custom = 255
}

/// <summary>
/// A node in the AerialCity graph, wrapping a reference to a <see cref="Segment"/>.
/// </summary>
public sealed class GraphNode
{
    /// <summary>The unique identifier for this graph node (same as the segment ID).</summary>
    public AerialId Id { get; }

    /// <summary>The ID of the segment this node represents.</summary>
    public AerialId SegmentId { get; }

    /// <summary>A human-readable label (e.g., function name, character name).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Extensible properties attached to this node.</summary>
    public Dictionary<string, object> Properties { get; init; } = [];

    public GraphNode(AerialId segmentId)
    {
        Id = AerialId.NewId();
        SegmentId = segmentId;
    }

    internal GraphNode(AerialId id, AerialId segmentId)
    {
        Id = id;
        SegmentId = segmentId;
    }
}

/// <summary>
/// A directed, typed edge between two graph nodes.
/// </summary>
public sealed class GraphEdge
{
    /// <summary>Unique identifier for this edge.</summary>
    public AerialId Id { get; }

    /// <summary>The source node of the relationship.</summary>
    public AerialId SourceId { get; }

    /// <summary>The target node of the relationship.</summary>
    public AerialId TargetId { get; }

    /// <summary>The kind of relationship this edge represents.</summary>
    public EdgeKind Kind { get; }

    /// <summary>Edge weight / confidence score in [0, 1].</summary>
    public float Weight { get; init; } = 1.0f;

    /// <summary>Extensible properties attached to this edge.</summary>
    public Dictionary<string, object> Properties { get; init; } = [];

    public GraphEdge(AerialId sourceId, AerialId targetId, EdgeKind kind)
    {
        Id = AerialId.NewId();
        SourceId = sourceId;
        TargetId = targetId;
        Kind = kind;
    }

    internal GraphEdge(AerialId id, AerialId sourceId, AerialId targetId, EdgeKind kind)
    {
        Id = id;
        SourceId = sourceId;
        TargetId = targetId;
        Kind = kind;
    }
}
