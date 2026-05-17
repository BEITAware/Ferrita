using AerialCity.Core.Primitives;

namespace AerialCity.Embedding;

/// <summary>Configuration for the embedding pipeline.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Expected embedding dimensionality. Used for validation.</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>Maximum tokens the embedding model can process. Default: 8192.</summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>Batch size for embedding multiple segments. Default: 32.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Whether to L2-normalize embeddings after generation.</summary>
    public bool Normalize { get; set; } = true;
}

/// <summary>
/// Interface for embedding model providers. Callers implement this to plug in
/// their preferred embedding model (OpenAI, local ONNX, etc.).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>The dimensionality of vectors produced by this provider.</summary>
    int Dimensions { get; }

    /// <summary>Generates an embedding for a single text input.</summary>
    Task<EmbeddingVector> EmbedTextAsync(string text, CancellationToken ct = default);

    /// <summary>Generates embeddings for a batch of text inputs.</summary>
    Task<IReadOnlyList<EmbeddingVector>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>Generates an embedding for binary content (images, audio frames).</summary>
    Task<EmbeddingVector> EmbedBinaryAsync(
        ReadOnlyMemory<byte> data, string mimeType, CancellationToken ct = default);
}
