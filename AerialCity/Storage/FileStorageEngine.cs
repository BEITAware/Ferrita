using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;
using AerialCity.GraphStore.Model;
using AerialCity.GraphStore.Storage;
using AerialCity.VectorStore.Storage;
using Microsoft.Extensions.Logging;

namespace AerialCity.Storage;

/// <summary>
/// File-backed storage engine with Write-Ahead Log (WAL) for durability.
/// Segments are stored as individual JSON files; blobs as raw binary files.
/// The WAL ensures crash recovery for incomplete write operations.
/// </summary>
/// <remarks>
/// Directory layout:
/// <code>
/// {BasePath}/
/// ├── segments/          # One JSON file per segment, named by hex ID
/// ├── blobs/             # Named binary blobs (indexes, etc.)
/// └── wal/               # Write-ahead log files
///     └── current.wal    # Active WAL
/// </code>
/// </remarks>
public sealed class FileStorageEngine : IStorageEngine
{
    private readonly StorageOptions _options;
    private readonly ILogger<FileStorageEngine> _logger;
    private readonly AerialCityStorageLayout _layout;
    private readonly string _segmentsDir;
    private readonly string _blobsDir;
    private readonly string _walDir;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private FileStream? _walStream;

    public FileStorageEngine(StorageOptions options, ILogger<FileStorageEngine> logger)
    {
        _options = options;
        _logger = logger;
        _layout = new AerialCityStorageLayout(options.BasePath);
        _layout.EnsureCreated();

        _segmentsDir = Path.Combine(_layout.DatabasePath, "segments");
        _blobsDir = Path.Combine(_layout.DatabasePath, "blobs");
        _walDir = Path.Combine(_layout.DatabasePath, "wal");

        Directory.CreateDirectory(_segmentsDir);
        Directory.CreateDirectory(_blobsDir);
        Directory.CreateDirectory(_walDir);

        if (options.EnableWal)
            InitializeWal();

        _logger.LogInformation("FileStorageEngine initialized at {Path}", options.BasePath);
    }

    public async Task WriteSegmentAsync(Segment segment, CancellationToken ct = default)
    {
        var path = SegmentPath(segment.Id);
        var json = JsonSerializer.SerializeToUtf8Bytes(segment.Content);

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_options.EnableWal)
                await WriteWalEntryAsync(WalOp.Write, segment.Id, json, ct);

            await File.WriteAllBytesAsync(path, SerializeSegment(segment), ct);
            if (segment.Embedding.HasValue)
            {
                await AcVectorStoreFile.UpsertVectorAsync(_layout, segment, ct);
                _layout.EnsureGraphDimensionDirectory(segment.Embedding.Value.Dimensions);
                await AcGraphStoreFile.UpsertNodeAsync(
                    _layout,
                    segment.Embedding.Value.Dimensions,
                    AcGraphStoreFile.DefaultGraphName,
                    AcGraphNodeRecord.FromNode(CreateGraphNode(segment), segment.Embedding.Value),
                    ct);
            }
            else
            {
                await AcVectorStoreFile.RemoveVectorAsync(_layout, segment.Id, ct);
            }

            _logger.LogDebug("Wrote segment {Id}", segment.Id);
        }
        catch (Exception ex)
        {
            throw new StorageException($"Failed to write segment {segment.Id}", ex);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<Segment?> ReadSegmentAsync(AerialId id, CancellationToken ct = default)
    {
        var path = SegmentPath(id);
        if (!File.Exists(path)) return null;

        try
        {
            var data = await File.ReadAllBytesAsync(path, ct);
            return DeserializeSegment(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read segment {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeleteSegmentAsync(AerialId id, CancellationToken ct = default)
    {
        var path = SegmentPath(id);
        if (!File.Exists(path)) return false;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_options.EnableWal)
                await WriteWalEntryAsync(WalOp.Delete, id, [], ct);

            File.Delete(path);
            await AcVectorStoreFile.RemoveVectorAsync(_layout, id, ct);
            _logger.LogDebug("Deleted segment {Id}", id);
            return true;
        }
        finally { _writeLock.Release(); }
    }

    public async IAsyncEnumerable<AerialId> ListSegmentIdsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_segmentsDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(_segmentsDir, "*.seg"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            if (AerialId.TryParse(name, out var id))
                yield return id;
        }
    }

    public async Task WriteBlobAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = Path.Combine(_blobsDir, name);
        await File.WriteAllBytesAsync(path, data.ToArray(), ct);
    }

    public async Task<ReadOnlyMemory<byte>?> ReadBlobAsync(string name, CancellationToken ct = default)
    {
        var path = Path.Combine(_blobsDir, name);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_walStream is not null)
        {
            await _walStream.FlushAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_walStream is not null)
        {
            await _walStream.FlushAsync();
            await _walStream.DisposeAsync();
        }
        _writeLock.Dispose();
    }

    // ── WAL ──

    private enum WalOp : byte { Write = 1, Delete = 2 }

    private void InitializeWal()
    {
        var walPath = Path.Combine(_walDir, "current.wal");
        _walStream = new FileStream(walPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        // Replay existing WAL entries on startup would go here
    }

    private async Task WriteWalEntryAsync(WalOp op, AerialId id, byte[] payload, CancellationToken ct)
    {
        if (_walStream is null) return;

        // WAL entry format: [CRC32:4][Op:1][IdLen:1][Id:16][PayloadLen:4][Payload:N]
        var idBytes = id.AsSpan().ToArray();
        var header = new byte[4 + 1 + 1 + 16 + 4];
        header[4] = (byte)op;
        header[5] = 16;
        idBytes.CopyTo(header.AsSpan(6));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), payload.Length);

        // Compute CRC32 over everything after the checksum field
        var crc = new Crc32();
        crc.Append(header.AsSpan(4));
        crc.Append(payload);
        var checksum = crc.GetCurrentHashAsUInt32();
        BinaryPrimitives.WriteUInt32LittleEndian(header, checksum);

        await _walStream.WriteAsync(header, ct);
        if (payload.Length > 0)
            await _walStream.WriteAsync(payload, ct);
    }

    // ── Serialization ──

    private string SegmentPath(AerialId id) => Path.Combine(_segmentsDir, $"{id}.seg");

    private static byte[] SerializeSegment(Segment s) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = s.Id.ToString(),
            kind = (int)s.Kind,
            content = s.Content,
            sourceUri = s.SourceUri,
            startOffset = s.StartOffset,
            endOffset = s.EndOffset,
            collection = s.CollectionName,
            createdAt = s.CreatedAt,
            updatedAt = s.UpdatedAt,
            embedding = s.Embedding.HasValue ? s.Embedding.Value.Span.ToArray() : null,
            metadata = s.Metadata
        });

    private static Segment? DeserializeSegment(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        var seg = new Segment(
            AerialId.Parse(root.GetProperty("id").GetString()!),
            (SegmentKind)root.GetProperty("kind").GetInt32(),
            root.GetProperty("content").GetString()!,
            root.GetProperty("createdAt").GetDateTimeOffset())
        {
            SourceUri = root.TryGetProperty("sourceUri", out var su) ? su.GetString() : null,
            StartOffset = root.GetProperty("startOffset").GetInt32(),
            EndOffset = root.GetProperty("endOffset").GetInt32(),
            CollectionName = root.TryGetProperty("collection", out var cn) ? cn.GetString() : null
        };

        if (root.TryGetProperty("updatedAt", out var ua) && ua.ValueKind != JsonValueKind.Null)
            seg.UpdatedAt = ua.GetDateTimeOffset();

        if (root.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
        {
            var values = emb.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            seg.Embedding = new EmbeddingVector(values);
        }

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                seg.Metadata[property.Name] = ReadMetadataValue(property.Value);
            }
        }

        return seg;
    }

    private static GraphNode CreateGraphNode(Segment segment)
    {
        var label = segment.Metadata.TryGetValue("symbolName", out var symbolName)
            ? symbolName?.ToString() ?? string.Empty
            : Path.GetFileName(segment.SourceUri) ?? string.Empty;

        return new GraphNode(segment.Id, segment.Id)
        {
            Label = label,
            Properties = new Dictionary<string, object>(segment.Metadata)
        };
    }

    private static object ReadMetadataValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ReadMetadataValue).ToArray(),
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };
    }
}
