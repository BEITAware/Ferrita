namespace AerialCity.Core.Storage;

/// <summary>
/// Defines the on-disk layout for a file-backed AerialCity database.
/// </summary>
internal sealed class AerialCityStorageLayout
{
    public const string VectorStoreDirectoryName = "ACVectorStore";
    public const string GraphDirectoryName = "ACGraph";
    public const string VectorFileExtension = ".acv";
    public const string GraphFileExtension = ".acg";

    /// <summary>The database root directory.</summary>
    public string DatabasePath { get; }

    /// <summary>The directory containing dimension-specific vector databases.</summary>
    public string VectorStorePath { get; }

    /// <summary>The directory containing dimension-specific graph folders.</summary>
    public string GraphStorePath { get; }

    public AerialCityStorageLayout(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        VectorStorePath = Path.Combine(databasePath, VectorStoreDirectoryName);
        GraphStorePath = Path.Combine(databasePath, GraphDirectoryName);
    }

    /// <summary>Creates the mandatory AerialCity database directories.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DatabasePath);
        Directory.CreateDirectory(VectorStorePath);
        Directory.CreateDirectory(GraphStorePath);
    }

    public string GetVectorFilePath(int dimensions) =>
        Path.Combine(VectorStorePath, GetVectorFileName(dimensions));

    public string GetGraphDimensionDirectoryPath(int dimensions) =>
        Path.Combine(GraphStorePath, GetGraphDimensionDirectoryName(dimensions));

    public string GetGraphFilePath(int dimensions, string graphName) =>
        Path.Combine(GetGraphDimensionDirectoryPath(dimensions), $"{SanitizeFileName(graphName)}{GraphFileExtension}");

    public string EnsureGraphDimensionDirectory(int dimensions)
    {
        var path = GetGraphDimensionDirectoryPath(dimensions);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string GetVectorFileName(int dimensions) =>
        $"Dimension_{ValidateDimensions(dimensions)}{VectorFileExtension}";

    public static string GetGraphDimensionDirectoryName(int dimensions) =>
        $"Dimension_{ValidateDimensions(dimensions)}_graph";

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Default";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalid.Contains(chars[i]))
                chars[i] = '_';
        }

        return new string(chars);
    }

    private static int ValidateDimensions(int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Vector dimensions must be positive.");

        return dimensions;
    }
}
