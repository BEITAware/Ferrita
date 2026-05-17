namespace AerialCity.Core.Storage;

/// <summary>
/// Configuration for the storage engine layer.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Base directory for file-backed storage. Ignored for in-memory mode.</summary>
    public string BasePath { get; set; } = "./aerial_data";

    /// <summary>Page size in bytes for the file store page manager. Default: 8 KB.</summary>
    public int PageSizeBytes { get; set; } = 8192;

    /// <summary>Whether to enable the Write-Ahead Log for crash recovery.</summary>
    public bool EnableWal { get; set; } = true;

    /// <summary>Maximum WAL file size before rotation (bytes). Default: 64 MB.</summary>
    public long MaxWalSizeBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>If true, use in-memory storage only (no disk I/O). Useful for testing.</summary>
    public bool InMemory { get; set; }
}
