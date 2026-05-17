using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;

namespace AerialCity.Storage;

/// <summary>
/// In-memory storage engine for testing and lightweight usage.
/// All data is held in concurrent dictionaries; nothing is persisted to disk.
/// </summary>
public sealed class MemoryStorageEngine : IStorageEngine
{
    private readonly ConcurrentDictionary<AerialId, byte[]> _segments = new();
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public Task WriteSegmentAsync(Segment segment, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(ToDto(segment));
        _segments[segment.Id] = json;
        return Task.CompletedTask;
    }

    public Task<Segment?> ReadSegmentAsync(AerialId id, CancellationToken ct = default)
    {
        if (!_segments.TryGetValue(id, out var json)) return Task.FromResult<Segment?>(null);
        var dto = JsonSerializer.Deserialize<SegmentDto>(json);
        return Task.FromResult<Segment?>(dto is not null ? FromDto(dto) : null);
    }

    public Task<bool> DeleteSegmentAsync(AerialId id, CancellationToken ct = default) =>
        Task.FromResult(_segments.TryRemove(id, out _));

    public async IAsyncEnumerable<AerialId> ListSegmentIdsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in _segments.Keys)
        {
            ct.ThrowIfCancellationRequested();
            yield return key;
        }
    }

    public Task WriteBlobAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        _blobs[name] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task<ReadOnlyMemory<byte>?> ReadBlobAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<ReadOnlyMemory<byte>?>(_blobs.TryGetValue(name, out var data) ? data : null);

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Serialization DTOs ──

    private static SegmentDto ToDto(Segment s) => new()
    {
        Id = s.Id.ToString(),
        Kind = (int)s.Kind,
        Content = s.Content,
        SourceUri = s.SourceUri,
        StartOffset = s.StartOffset,
        EndOffset = s.EndOffset,
        CollectionName = s.CollectionName,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        EmbeddingValues = s.Embedding.HasValue ? s.Embedding.Value.Span.ToArray() : null,
        Metadata = s.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString())
    };

    private static Segment FromDto(SegmentDto dto)
    {
        var seg = new Segment(
            AerialId.Parse(dto.Id!),
            (SegmentKind)dto.Kind,
            dto.Content!,
            dto.CreatedAt)
        {
            SourceUri = dto.SourceUri,
            StartOffset = dto.StartOffset,
            EndOffset = dto.EndOffset,
            CollectionName = dto.CollectionName,
            UpdatedAt = dto.UpdatedAt
        };

        if (dto.EmbeddingValues is not null)
            seg.Embedding = new EmbeddingVector(dto.EmbeddingValues);

        if (dto.Metadata is not null)
            foreach (var (k, v) in dto.Metadata)
                if (v is not null) seg.Metadata[k] = v;

        return seg;
    }

    private sealed class SegmentDto
    {
        public string? Id { get; set; }
        public int Kind { get; set; }
        public string? Content { get; set; }
        public string? SourceUri { get; set; }
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string? CollectionName { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public float[]? EmbeddingValues { get; set; }
        public Dictionary<string, string?>? Metadata { get; set; }
    }
}
