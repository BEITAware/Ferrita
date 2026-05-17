namespace AerialCity.Database.Schema;

/// <summary>
/// Defines a field within a collection schema.
/// </summary>
public sealed class FieldDefinition
{
    /// <summary>Field name.</summary>
    public required string Name { get; init; }

    /// <summary>Field data type.</summary>
    public required FieldType Type { get; init; }

    /// <summary>Whether this field is required.</summary>
    public bool Required { get; init; }

    /// <summary>Whether to build a secondary index on this field.</summary>
    public bool Indexed { get; init; }
}

/// <summary>Supported field types.</summary>
public enum FieldType
{
    String, Int32, Int64, Float32, Float64, Boolean, DateTime, Json
}

/// <summary>
/// Schema definition for a collection of segments.
/// </summary>
public sealed class CollectionSchema
{
    /// <summary>Collection name.</summary>
    public required string Name { get; init; }

    /// <summary>Embedding vector dimensionality for this collection.</summary>
    public int VectorDimensions { get; init; } = 768;

    /// <summary>User-defined metadata fields for segments in this collection.</summary>
    public List<FieldDefinition> Fields { get; init; } = [];

    /// <summary>Description of this collection.</summary>
    public string? Description { get; init; }
}
