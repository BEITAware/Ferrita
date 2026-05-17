using AerialCity.Core.Primitives;
using System.Text;

namespace AerialCity.Embedding;

/// <summary>
/// Supported remote embedding API dialects.
/// </summary>
public enum EmbeddingApiType
{
    /// <summary>OpenAI-compatible embeddings endpoint.</summary>
    OpenAI = 0,

    /// <summary>Google Gemini embeddings endpoint.</summary>
    Google = 1
}

/// <summary>
/// A single text or binary part to embed.
/// </summary>
public sealed class EmbeddingContentPart
{
    /// <summary>Optional text, caption, transcript, or semantic description.</summary>
    public string? Text { get; init; }

    /// <summary>Optional binary payload for multimodal content.</summary>
    public ReadOnlyMemory<byte>? Binary { get; init; }

    /// <summary>MIME type for <see cref="Binary"/>.</summary>
    public string? MimeType { get; init; }

    /// <summary>Optional source URI or file path for this part.</summary>
    public string? SourceUri { get; init; }

    /// <summary>Optional display name for this part.</summary>
    public string? Name { get; init; }

    /// <summary>Extensible metadata associated with this part.</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];

    internal bool HasBinary => Binary.HasValue && !Binary.Value.IsEmpty;

    internal bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Content to embed. Use one or more parts for multimodal input.
/// </summary>
public sealed class EmbeddingInput
{
    /// <summary>Ordered content parts that should be represented in the embedding input.</summary>
    public IReadOnlyList<EmbeddingContentPart> Parts { get; init; } = [];

    /// <summary>Creates a text-only embedding input.</summary>
    public static EmbeddingInput FromText(string text, string? sourceUri = null) =>
        new()
        {
            Parts =
            [
                new EmbeddingContentPart
                {
                    Text = text,
                    SourceUri = sourceUri
                }
            ]
        };

    /// <summary>Creates a binary or multimodal embedding input.</summary>
    public static EmbeddingInput FromBinary(
        ReadOnlyMemory<byte> binary,
        string mimeType,
        string? text = null,
        string? sourceUri = null,
        string? name = null) =>
        new()
        {
            Parts =
            [
                new EmbeddingContentPart
                {
                    Text = text,
                    Binary = binary,
                    MimeType = mimeType,
                    SourceUri = sourceUri,
                    Name = name
                }
            ]
        };

    /// <summary>Creates an embedding input from an AerialCity segment.</summary>
    public static EmbeddingInput FromSegment(Segment segment, string? mimeType = null)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return new EmbeddingInput
        {
            Parts =
            [
                new EmbeddingContentPart
                {
                    Text = segment.Content,
                    Binary = segment.BinaryContent,
                    MimeType = mimeType ?? ResolveMimeType(segment.Kind),
                    SourceUri = segment.SourceUri,
                    Metadata = segment.Metadata.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
                }
            ]
        };
    }

    private static string? ResolveMimeType(SegmentKind kind) =>
        kind switch
        {
            SegmentKind.Image => "image/png",
            SegmentKind.VideoClip => "video/mp4",
            _ => null
        };
}

/// <summary>
/// Request data for the API-backed embedding delegate.
/// </summary>
public sealed class ApiEmbeddingRequest
{
    /// <summary>API key used by the selected provider.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base URL for the provider. If omitted, AerialCity uses the provider's public API base URL.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>Provider API dialect to call.</summary>
    public EmbeddingApiType ApiType { get; init; }

    /// <summary>Embedding model name, such as an OpenAI model ID or Google model path.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Content to embed when the caller is not embedding an existing segment.
    /// </summary>
    public EmbeddingInput? Content { get; init; }

    /// <summary>
    /// Optional segment to embed. When supplied, the returned vector is also assigned to
    /// <see cref="Segment.Embedding"/>, so the segment can be passed to the Insert delegate.
    /// </summary>
    public Segment? Segment { get; init; }

    /// <summary>
    /// Provider-specific request parameters. For example: dimensions, taskType, title,
    /// outputDimensionality, user, or encoding_format.
    /// </summary>
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /// <summary>
    /// Optional dimensionality convenience property. It maps to OpenAI dimensions and
    /// Google outputDimensionality unless the same provider-specific parameter is present.
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>Whether to L2-normalize the returned vector before assigning or returning it.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>
    /// Includes base64 binary payloads in SkyweaverPreservedContent XML blocks.
    /// The default keeps binary metadata and text only, avoiding huge embedding prompts.
    /// </summary>
    public bool IncludeBinaryDataInTextProjection { get; init; }
}

/// <summary>
/// Request data for embedding a complete source code file as AST-aware chunks.
/// </summary>
public sealed class ApiCodeFileEmbeddingRequest
{
    /// <summary>API key used by the selected provider.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base URL for the provider. If omitted, AerialCity uses the provider's public API base URL.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>Provider API dialect to call.</summary>
    public EmbeddingApiType ApiType { get; init; }

    /// <summary>Embedding model name, such as an OpenAI model ID or Google model path.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Path to the code file that should be read, segmented, and embedded.
    /// Ignored when <see cref="SourceCode"/> is supplied.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Source code text to embed. When omitted, AerialCity reads <see cref="FilePath"/>.
    /// </summary>
    public string? SourceCode { get; init; }

    /// <summary>Optional source URI stored on every generated segment.</summary>
    public string? SourceUri { get; init; }

    /// <summary>
    /// Optional Tree-sitter language hint, such as csharp, python, javascript, or cpp.
    /// When omitted, AerialCity infers the language from the file extension.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Optional file encoding used when reading <see cref="FilePath"/>.
    /// When omitted, .NET's default UTF-8 reader with BOM detection is used.
    /// </summary>
    public Encoding? FileEncoding { get; init; }

    /// <summary>Maximum estimated input tokens allowed for each embedded chunk.</summary>
    public int MaxInputTokens { get; init; } = 8192;

    /// <summary>
    /// Optional exact token counter. If omitted, AerialCity uses a conservative code-token estimate.
    /// </summary>
    public Func<string, int>? TokenCounter { get; init; }

    /// <summary>
    /// Provider-specific request parameters. For example: dimensions, taskType, title,
    /// outputDimensionality, user, or encoding_format.
    /// </summary>
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /// <summary>
    /// Optional dimensionality convenience property. It maps to OpenAI dimensions and
    /// Google outputDimensionality unless the same provider-specific parameter is present.
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>Whether to L2-normalize each returned vector before assigning or returning it.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>Metadata copied to every generated code segment before embedding.</summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Includes base64 binary payloads in SkyweaverPreservedContent XML blocks.
    /// Code file embedding normally sends text only, so this is reserved for future mixed inputs.
    /// </summary>
    public bool IncludeBinaryDataInTextProjection { get; init; }
}

/// <summary>
/// Request data for embedding a complete text file as paragraph-based chunks.
/// </summary>
public sealed class ApiTextFileEmbeddingRequest
{
    /// <summary>API key used by the selected provider.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base URL for the provider. If omitted, AerialCity uses the provider's public API base URL.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>Provider API dialect to call.</summary>
    public EmbeddingApiType ApiType { get; init; }

    /// <summary>Embedding model name, such as an OpenAI model ID or Google model path.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Path to the text file that should be read, segmented, and embedded.
    /// Ignored when <see cref="Text"/> is supplied.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Text to embed. When omitted, AerialCity reads <see cref="FilePath"/>.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Optional source URI stored on every generated segment.</summary>
    public string? SourceUri { get; init; }

    /// <summary>
    /// Optional file encoding used when reading <see cref="FilePath"/>.
    /// When omitted, .NET's default UTF-8 reader with BOM detection is used.
    /// </summary>
    public Encoding? FileEncoding { get; init; }

    /// <summary>Maximum estimated input tokens allowed for each embedded chunk.</summary>
    public int MaxInputTokens { get; init; } = 8192;

    /// <summary>
    /// Fraction of each oversized paragraph chunk repeated into the next chunk. Default: 0.25.
    /// </summary>
    public double OverlapRatio { get; init; } = 0.25d;

    /// <summary>
    /// Optional exact token counter. If omitted, AerialCity uses a conservative text-token estimate.
    /// </summary>
    public Func<string, int>? TokenCounter { get; init; }

    /// <summary>
    /// Provider-specific request parameters. For example: dimensions, taskType, title,
    /// outputDimensionality, user, or encoding_format.
    /// </summary>
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /// <summary>
    /// Optional dimensionality convenience property. It maps to OpenAI dimensions and
    /// Google outputDimensionality unless the same provider-specific parameter is present.
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>Whether to L2-normalize each returned vector before assigning or returning it.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>Metadata copied to every generated text segment before embedding.</summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Includes base64 binary payloads in SkyweaverPreservedContent XML blocks.
    /// Text file embedding normally sends text only, so this is reserved for future mixed inputs.
    /// </summary>
    public bool IncludeBinaryDataInTextProjection { get; init; }
}

/// <summary>
/// Result returned by the API-backed embedding delegate.
/// </summary>
public sealed class EmbeddingResult
{
    /// <summary>The generated embedding vector.</summary>
    public required EmbeddingVector Vector { get; init; }

    /// <summary>The segment that was embedded, when the request supplied one.</summary>
    public Segment? Segment { get; init; }

    /// <summary>The model used for the request.</summary>
    public required string Model { get; init; }

    /// <summary>The API dialect used for the request.</summary>
    public EmbeddingApiType ApiType { get; init; }
}
