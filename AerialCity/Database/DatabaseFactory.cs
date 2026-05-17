using AerialCity.Core.Storage;
using AerialCity.GraphStore.Index;
using AerialCity.VectorStore.Index;
using AerialCity.VectorStore.Similarity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AerialCity.Database;

/// <summary>
/// Factory for creating and opening <see cref="AerialDatabase"/> instances.
/// </summary>
internal sealed class DatabaseFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DatabaseFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Creates a new AerialCity database with the given options.
    /// </summary>
    public async Task<AerialDatabase> CreateAsync(DatabaseOptions options, CancellationToken ct = default)
    {
        var logger = _loggerFactory.CreateLogger<AerialDatabase>();
        logger.LogInformation("Creating database '{Name}'", options.Name);

        // Adjust storage path to include database name
        var storageOptions = new StorageOptions
        {
            BasePath = Path.Combine(options.Storage.BasePath, options.Name),
            PageSizeBytes = options.Storage.PageSizeBytes,
            EnableWal = options.Storage.EnableWal,
            MaxWalSizeBytes = options.Storage.MaxWalSizeBytes,
            InMemory = options.Storage.InMemory
        };

        IStorageEngine storage = storageOptions.InMemory
            ? new Storage.MemoryStorageEngine()
            : new Storage.FileStorageEngine(
                storageOptions,
                _loggerFactory.CreateLogger<Storage.FileStorageEngine>());

        var vectorIndex = new HnswGraph(options.SimilarityMetric, options.HnswOptions);
        var graphIndex = new AdjacencyListIndex();
        var database = new AerialDatabase(options.Name, storage, vectorIndex, graphIndex, logger);

        await RebuildIndexesAsync(database, ct);
        return database;
    }

    /// <summary>
    /// Creates an in-memory database for testing.
    /// </summary>
    public async Task<AerialDatabase> CreateInMemoryAsync(string name = "test", CancellationToken ct = default)
    {
        return await CreateAsync(new DatabaseOptions
        {
            Name = name,
            Storage = new StorageOptions { InMemory = true }
        }, ct);
    }

    private static async Task RebuildIndexesAsync(AerialDatabase database, CancellationToken ct)
    {
        await foreach (var id in database.Storage.ListSegmentIdsAsync(ct))
        {
            var segment = await database.Storage.ReadSegmentAsync(id, ct);
            if (segment is not null)
                database.IndexStoredSegment(segment);
        }
    }
}
