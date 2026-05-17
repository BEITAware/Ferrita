using System.Text.Json.Serialization;
using AerialCity.Core.Primitives;
using AerialCity.GraphStore.Model;
using AerialCity.VectorStore.Storage;

namespace AerialCity.GraphStore.Storage;

/// <summary>
/// A graph node anchored to a vector-space position.
/// </summary>
internal sealed class AcGraphNodeRecord
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = string.Empty;

    public string SegmentId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public float[]? PositionVector { get; set; }

    public Dictionary<string, string[]> Properties { get; set; } = [];

    public static AcGraphNodeRecord FromNode(GraphNode node, EmbeddingVector? position = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var properties = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, value) in node.Properties)
        {
            var values = MetadataReader.ToStringList(value);
            if (values.Count > 0)
                properties[key] = [.. values];
        }

        return new AcGraphNodeRecord
        {
            Id = node.Id.ToString(),
            SegmentId = node.SegmentId.ToString(),
            Label = node.Label,
            PositionVector = position?.Span.ToArray(),
            Properties = properties
        };
    }
}

/// <summary>
/// A directed edge encoded as an origin vector plus a displacement vector.
/// </summary>
internal sealed class AcGraphEdgeRecord
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string Kind { get; set; } = EdgeKind.Custom.ToString();

    public int KindValue { get; set; } = (int)EdgeKind.Custom;

    public float Weight { get; set; } = 1.0f;

    /// <summary>Vector from the coordinate origin to the source node.</summary>
    public float[] OriginVector { get; set; } = [];

    /// <summary>Vector from the source node to the target node.</summary>
    public float[] DisplacementVector { get; set; } = [];

    public Dictionary<string, string[]> Properties { get; set; } = [];

    public static AcGraphEdgeRecord FromEdge(
        GraphEdge edge,
        in EmbeddingVector sourceVector,
        in EmbeddingVector targetVector)
    {
        ArgumentNullException.ThrowIfNull(edge);

        if (sourceVector.Dimensions != targetVector.Dimensions)
            throw new ArgumentException(
                $"Graph edge vectors must have the same dimensions: {sourceVector.Dimensions} vs {targetVector.Dimensions}.");

        var displacement = new float[sourceVector.Dimensions];
        for (var i = 0; i < displacement.Length; i++)
            displacement[i] = targetVector[i] - sourceVector[i];

        var properties = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, value) in edge.Properties)
        {
            var values = MetadataReader.ToStringList(value);
            if (values.Count > 0)
                properties[key] = [.. values];
        }

        return new AcGraphEdgeRecord
        {
            Id = edge.Id.ToString(),
            SourceId = edge.SourceId.ToString(),
            TargetId = edge.TargetId.ToString(),
            Kind = edge.Kind.ToString(),
            KindValue = (int)edge.Kind,
            Weight = edge.Weight,
            OriginVector = sourceVector.Span.ToArray(),
            DisplacementVector = displacement,
            Properties = properties
        };
    }
}

/// <summary>
/// JSON payload stored in each GraphName.acg file.
/// </summary>
internal sealed class AcGraphDatabaseFile
{
    public string Format { get; set; } = AcGraphStoreFile.FormatName;

    public int Version { get; set; } = AcGraphStoreFile.CurrentVersion;

    public string GraphName { get; set; } = "Default";

    public int Dimensions { get; set; }

    public string EdgeEncoding { get; set; } = "OriginVector + DisplacementVector";

    public List<AcGraphNodeRecord> Nodes { get; set; } = [];

    public List<AcGraphEdgeRecord> Edges { get; set; } = [];

    public Dictionary<string, string[]> Metadata { get; set; } = [];
}
