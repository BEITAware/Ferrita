using System.Text.Json;
using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;

namespace AerialCity.VectorStore.Storage;

/// <summary>
/// Reads and writes Dimension_n.acv vector database files.
/// </summary>
internal static class AcVectorStoreFile
{
    public const string FormatName = "AerialCity.ACVectorStore";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task EnsureAsync(
        AerialCityStorageLayout layout,
        int dimensions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = layout.GetVectorFilePath(dimensions);
        _ = await ReadOrCreateAsync(path, dimensions, ct);
    }

    public static async Task UpsertVectorAsync(
        AerialCityStorageLayout layout,
        Segment segment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(segment);

        if (segment.Embedding is not { } embedding)
            return;

        await RemoveVectorAsync(layout, segment.Id, ct);

        var dimensions = embedding.Dimensions;
        var path = layout.GetVectorFilePath(dimensions);
        var file = await ReadOrCreateAsync(path, dimensions, ct);
        var nextIndex = file.Vectors.Count == 0 ? 0 : file.Vectors.Max(v => v.Index) + 1;

        file.Vectors.Add(AcVectorRecord.FromSegment(segment, nextIndex));
        await WriteAsync(path, file, ct);
    }

    public static async Task<bool> RemoveVectorAsync(
        AerialCityStorageLayout layout,
        AerialId id,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!Directory.Exists(layout.VectorStorePath))
            return false;

        var removed = false;
        foreach (var path in Directory.EnumerateFiles(layout.VectorStorePath, $"*{AerialCityStorageLayout.VectorFileExtension}"))
        {
            ct.ThrowIfCancellationRequested();

            var file = await ReadExistingAsync(path, ct);
            if (file is null)
                continue;

            var before = file.Vectors.Count;
            file.Vectors.RemoveAll(v => string.Equals(v.Id, id.ToString(), StringComparison.OrdinalIgnoreCase));
            if (file.Vectors.Count == before)
                continue;

            await WriteAsync(path, file, ct);
            removed = true;
        }

        return removed;
    }

    public static async Task<bool> UpdateIndexesAsync(
        AerialCityStorageLayout layout,
        int dimensions,
        AerialId id,
        Action<AcVectorIndexSet> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(update);

        var path = layout.GetVectorFilePath(dimensions);
        var file = await ReadExistingAsync(path, ct);
        if (file is null)
            return false;

        var record = file.Vectors.FirstOrDefault(v => string.Equals(v.Id, id.ToString(), StringComparison.OrdinalIgnoreCase));
        if (record is null)
            return false;

        update(record.Indexes);
        record.WrittenAt = DateTimeOffset.UtcNow;
        await WriteAsync(path, file, ct);
        return true;
    }

    public static async Task<AcVectorDatabaseFile?> ReadExistingAsync(
        string path,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllBytesAsync(path, ct);
            var file = JsonSerializer.Deserialize<AcVectorDatabaseFile>(json, JsonOptions);
            if (file is null)
                throw new StorageException($"Vector database file '{path}' is empty or invalid.");

            Normalize(file);
            return file;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StorageException($"Failed to read vector database file '{path}'.", ex);
        }
    }

    public static List<AcIndexDefinition> CreateDefaultIndexCatalog() =>
    [
        new()
        {
            Name = "ID",
            Description = "Stable vector identifier.",
            CanBeEmpty = false
        },
        new()
        {
            Name = "Index",
            Description = "Ordinal index inside the dimension file.",
            CanBeEmpty = false
        },
        new()
        {
            Name = "Callers",
            Description = "Caller, mention, or ownership sources pointing to this vector.",
            MultiValue = true
        },
        new()
        {
            Name = "Callees",
            Description = "Callee, mentioned, or owned targets pointed to by this vector.",
            MultiValue = true
        },
        new()
        {
            Name = "Source",
            Description = "Original text, video clip, or image descriptor used to generate the embedding."
        },
        new()
        {
            Name = "Kind",
            Description = "Segment modality or semantic kind."
        },
        new()
        {
            Name = "Collection",
            Description = "Logical collection name."
        },
        new()
        {
            Name = "CreatedAt",
            Description = "Creation timestamp."
        },
        new()
        {
            Name = "UpdatedAt",
            Description = "Last update timestamp."
        },
        new()
        {
            Name = "Custom",
            Description = "Open-ended extension index namespace.",
            MultiValue = true
        }
    ];

    private static async Task<AcVectorDatabaseFile> ReadOrCreateAsync(
        string path,
        int dimensions,
        CancellationToken ct)
    {
        var existing = await ReadExistingAsync(path, ct);
        if (existing is not null)
        {
            if (existing.Dimensions != dimensions)
                throw new StorageException(
                    $"Vector database '{path}' stores {existing.Dimensions}d vectors, not {dimensions}d.");

            return existing;
        }

        var created = new AcVectorDatabaseFile
        {
            Dimensions = dimensions
        };

        await WriteAsync(path, created, ct);
        return created;
    }

    private static async Task WriteAsync(
        string path,
        AcVectorDatabaseFile file,
        CancellationToken ct)
    {
        Normalize(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        await File.WriteAllBytesAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void Normalize(AcVectorDatabaseFile file)
    {
        file.Format = string.IsNullOrWhiteSpace(file.Format) ? FormatName : file.Format;
        file.Version = file.Version <= 0 ? CurrentVersion : file.Version;
        file.IndexCatalog ??= CreateDefaultIndexCatalog();
        if (file.IndexCatalog.Count == 0)
            file.IndexCatalog = CreateDefaultIndexCatalog();
        file.Vectors ??= [];
    }
}
