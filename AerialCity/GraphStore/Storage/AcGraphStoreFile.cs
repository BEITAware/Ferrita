using System.Text.Json;
using AerialCity.Core.Exceptions;
using AerialCity.Core.Storage;

namespace AerialCity.GraphStore.Storage;

/// <summary>
/// Reads and writes Dimension_n_graph/GraphName.acg graph database files.
/// </summary>
internal static class AcGraphStoreFile
{
    public const string FormatName = "AerialCity.ACGraph";
    public const int CurrentVersion = 1;
    public const string DefaultGraphName = "Default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task EnsureAsync(
        AerialCityStorageLayout layout,
        int dimensions,
        string graphName = DefaultGraphName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = layout.GetGraphFilePath(dimensions, graphName);
        _ = await ReadOrCreateAsync(path, dimensions, graphName, ct);
    }

    public static async Task UpsertNodeAsync(
        AerialCityStorageLayout layout,
        int dimensions,
        string graphName,
        AcGraphNodeRecord node,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(node);

        var path = layout.GetGraphFilePath(dimensions, graphName);
        var file = await ReadOrCreateAsync(path, dimensions, graphName, ct);
        var index = file.Nodes.FindIndex(n => string.Equals(n.Id, node.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            file.Nodes[index] = node;
        else
            file.Nodes.Add(node);

        await WriteAsync(path, file, ct);
    }

    public static async Task UpsertEdgeAsync(
        AerialCityStorageLayout layout,
        int dimensions,
        string graphName,
        AcGraphEdgeRecord edge,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(edge);

        if (edge.OriginVector.Length != dimensions || edge.DisplacementVector.Length != dimensions)
            throw new StorageException(
                $"Graph edge {edge.Id} does not match the {dimensions}d graph space.");

        var path = layout.GetGraphFilePath(dimensions, graphName);
        var file = await ReadOrCreateAsync(path, dimensions, graphName, ct);
        var index = file.Edges.FindIndex(e => string.Equals(e.Id, edge.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            file.Edges[index] = edge;
        else
            file.Edges.Add(edge);

        await WriteAsync(path, file, ct);
    }

    public static async Task<AcGraphDatabaseFile?> ReadExistingAsync(
        string path,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllBytesAsync(path, ct);
            var file = JsonSerializer.Deserialize<AcGraphDatabaseFile>(json, JsonOptions);
            if (file is null)
                throw new StorageException($"Graph database file '{path}' is empty or invalid.");

            Normalize(file);
            return file;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StorageException($"Failed to read graph database file '{path}'.", ex);
        }
    }

    private static async Task<AcGraphDatabaseFile> ReadOrCreateAsync(
        string path,
        int dimensions,
        string graphName,
        CancellationToken ct)
    {
        var existing = await ReadExistingAsync(path, ct);
        if (existing is not null)
        {
            if (existing.Dimensions != dimensions)
                throw new StorageException(
                    $"Graph database '{path}' stores {existing.Dimensions}d vectors, not {dimensions}d.");

            return existing;
        }

        var created = new AcGraphDatabaseFile
        {
            Dimensions = dimensions,
            GraphName = AerialCityStorageLayout.SanitizeFileName(graphName)
        };

        await WriteAsync(path, created, ct);
        return created;
    }

    private static async Task WriteAsync(
        string path,
        AcGraphDatabaseFile file,
        CancellationToken ct)
    {
        Normalize(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        await File.WriteAllBytesAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void Normalize(AcGraphDatabaseFile file)
    {
        file.Format = string.IsNullOrWhiteSpace(file.Format) ? FormatName : file.Format;
        file.Version = file.Version <= 0 ? CurrentVersion : file.Version;
        file.GraphName = string.IsNullOrWhiteSpace(file.GraphName)
            ? DefaultGraphName
            : AerialCityStorageLayout.SanitizeFileName(file.GraphName);
        file.EdgeEncoding = string.IsNullOrWhiteSpace(file.EdgeEncoding)
            ? "OriginVector + DisplacementVector"
            : file.EdgeEncoding;
        file.Nodes ??= [];
        file.Edges ??= [];
        file.Metadata ??= [];
    }
}
