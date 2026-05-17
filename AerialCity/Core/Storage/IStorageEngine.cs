using AerialCity.Core.Primitives;

namespace AerialCity.Core.Storage;

/// <summary>
/// Low-level storage engine interface for persisting segments and their indexes.
/// Implementations handle the physical I/O (file-backed, in-memory, etc.).
/// </summary>
public interface IStorageEngine : IAsyncDisposable
{
    /// <summary>Writes a segment to persistent storage.</summary>
    Task WriteSegmentAsync(Segment segment, CancellationToken ct = default);

    /// <summary>Reads a segment by its identifier.</summary>
    Task<Segment?> ReadSegmentAsync(AerialId id, CancellationToken ct = default);

    /// <summary>Deletes a segment by its identifier. Returns true if it existed.</summary>
    Task<bool> DeleteSegmentAsync(AerialId id, CancellationToken ct = default);

    /// <summary>Enumerates all segment IDs in the store.</summary>
    IAsyncEnumerable<AerialId> ListSegmentIdsAsync(CancellationToken ct = default);

    /// <summary>Writes raw bytes to a named blob (used for index persistence, WAL, etc.).</summary>
    Task WriteBlobAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Reads a named blob. Returns null if not found.</summary>
    Task<ReadOnlyMemory<byte>?> ReadBlobAsync(string name, CancellationToken ct = default);

    /// <summary>Flushes all pending writes to durable storage.</summary>
    Task FlushAsync(CancellationToken ct = default);
}
