using AerialCity.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace AerialCity.Embedding;

/// <summary>
/// Orchestrates the embedding of segments by batching and routing
/// to the appropriate <see cref="IEmbeddingProvider"/> method based
/// on the segment's modality.
/// </summary>
internal sealed class EmbeddingPipeline
{
    private readonly IEmbeddingProvider _provider;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<EmbeddingPipeline> _logger;

    public EmbeddingPipeline(
        IEmbeddingProvider provider,
        EmbeddingOptions options,
        ILogger<EmbeddingPipeline> logger)
    {
        _provider = provider;
        _options = options;
        _logger = logger;
    }

    /// <summary>Embeds a single segment, setting its <see cref="Segment.Embedding"/> property.</summary>
    public async Task EmbedAsync(Segment segment, CancellationToken ct = default)
    {
        _logger.LogDebug("Embedding segment {Id} ({Kind})", segment.Id, segment.Kind);

        EmbeddingVector vector;

        if (segment.BinaryContent.HasValue && segment.Kind is SegmentKind.Image or SegmentKind.VideoClip)
        {
            var mime = segment.Kind == SegmentKind.Image ? "image/png" : "video/mp4";
            vector = await _provider.EmbedBinaryAsync(segment.BinaryContent.Value, mime, ct);
        }
        else
        {
            vector = await _provider.EmbedTextAsync(segment.Content, ct);
        }

        segment.Embedding = _options.Normalize ? vector.Normalize() : vector;
    }

    /// <summary>Embeds a batch of segments efficiently.</summary>
    public async Task EmbedBatchAsync(IReadOnlyList<Segment> segments, CancellationToken ct = default)
    {
        _logger.LogDebug("Batch embedding {Count} segments", segments.Count);

        // Separate text and binary segments
        var textSegments = segments.Where(s => !s.BinaryContent.HasValue).ToList();
        var binarySegments = segments.Where(s => s.BinaryContent.HasValue).ToList();

        // Batch text embeddings
        for (var i = 0; i < textSegments.Count; i += _options.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = textSegments.Skip(i).Take(_options.BatchSize).ToList();
            var texts = batch.Select(s => s.Content).ToList();
            var vectors = await _provider.EmbedBatchAsync(texts, ct);

            for (var j = 0; j < batch.Count; j++)
            {
                batch[j].Embedding = _options.Normalize ? vectors[j].Normalize() : vectors[j];
            }
        }

        // Binary embeddings one at a time
        foreach (var seg in binarySegments)
        {
            ct.ThrowIfCancellationRequested();
            await EmbedAsync(seg, ct);
        }

        _logger.LogDebug("Batch embedding complete");
    }
}
