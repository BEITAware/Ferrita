namespace AerialCity.Core.Exceptions;

/// <summary>Base exception for all AerialCity-specific errors.</summary>
public class AerialCityException : Exception
{
    public AerialCityException() { }
    public AerialCityException(string message) : base(message) { }
    public AerialCityException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a storage-level operation fails.</summary>
public sealed class StorageException : AerialCityException
{
    public StorageException(string message) : base(message) { }
    public StorageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a vector index operation fails.</summary>
public sealed class IndexException : AerialCityException
{
    public IndexException(string message) : base(message) { }
    public IndexException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a schema violation is detected.</summary>
public sealed class SchemaException : AerialCityException
{
    public SchemaException(string message) : base(message) { }
    public SchemaException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a requested entity is not found.</summary>
public sealed class NotFoundException : AerialCityException
{
    public string? EntityId { get; }
    public NotFoundException(string message, string? entityId = null) : base(message)
    {
        EntityId = entityId;
    }
}

/// <summary>Thrown when segmentation fails.</summary>
public sealed class SegmentationException : AerialCityException
{
    public SegmentationException(string message) : base(message) { }
    public SegmentationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when an embedding API request or response cannot be completed.</summary>
public sealed class EmbeddingException : AerialCityException
{
    /// <summary>Creates an embedding exception with an error message.</summary>
    public EmbeddingException(string message) : base(message) { }

    /// <summary>Creates an embedding exception with an error message and inner exception.</summary>
    public EmbeddingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when an API-backed retrieval request cannot be completed.</summary>
public sealed class RetrievalException : AerialCityException
{
    /// <summary>Creates a retrieval exception with an error message.</summary>
    public RetrievalException(string message) : base(message) { }

    /// <summary>Creates a retrieval exception with an error message and inner exception.</summary>
    public RetrievalException(string message, Exception inner) : base(message, inner) { }
}
