using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using AerialCity.Core.Primitives;

namespace AerialCity.VectorStore.Storage;

/// <summary>
/// Source modality captured by the ACVectorStore metadata index.
/// </summary>
internal enum AcVectorSourceKind
{
    Unknown = 0,
    Text = 1,
    Video = 2,
    Image = 3
}

/// <summary>
/// Describes the original source used to create an embedding vector.
/// </summary>
internal sealed class AcVectorSource
{
    public AcVectorSourceKind Kind { get; set; }

    public string? FileName { get; set; }

    public string? Path { get; set; }

    /// <summary>Text content used for text/code embeddings.</summary>
    public string? Content { get; set; }

    /// <summary>Video timecode start, if the embedding was produced from a clip.</summary>
    public string? TimeCodeStart { get; set; }

    /// <summary>Video timecode end, if the embedding was produced from a clip.</summary>
    public string? TimeCodeEnd { get; set; }

    public int? StartOffset { get; set; }

    public int? EndOffset { get; set; }

    public static AcVectorSource FromSegment(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var sourceKind = ResolveSourceKind(segment);
        var sourcePath = segment.SourceUri;
        var source = new AcVectorSource
        {
            Kind = sourceKind,
            Path = sourcePath,
            FileName = GetFileName(sourcePath),
            StartOffset = segment.StartOffset,
            EndOffset = segment.EndOffset
        };

        if (sourceKind is AcVectorSourceKind.Text or AcVectorSourceKind.Unknown)
        {
            source.Content = MetadataReader.GetString(
                segment.Metadata,
                "sourceContent",
                "SourceContent",
                "fileContent",
                "FileContent") ?? segment.Content;
        }

        if (sourceKind == AcVectorSourceKind.Video)
        {
            source.TimeCodeStart = MetadataReader.GetString(
                segment.Metadata,
                "timeCodeStart",
                "TimeCodeStart",
                "startTimeCode",
                "StartTimeCode",
                "startTime",
                "StartTime");

            source.TimeCodeEnd = MetadataReader.GetString(
                segment.Metadata,
                "timeCodeEnd",
                "TimeCodeEnd",
                "endTimeCode",
                "EndTimeCode",
                "endTime",
                "EndTime");
        }

        return source;
    }

    private static AcVectorSourceKind ResolveSourceKind(Segment segment)
    {
        var explicitKind = MetadataReader.GetString(segment.Metadata, "sourceKind", "SourceKind");
        if (!string.IsNullOrWhiteSpace(explicitKind)
            && Enum.TryParse<AcVectorSourceKind>(explicitKind, ignoreCase: true, out var parsed))
            return parsed;

        return segment.Kind switch
        {
            SegmentKind.VideoClip => AcVectorSourceKind.Video,
            SegmentKind.Image => AcVectorSourceKind.Image,
            SegmentKind.CodeBlock or SegmentKind.TextPassage or SegmentKind.StructuredData => AcVectorSourceKind.Text,
            _ => AcVectorSourceKind.Unknown
        };
    }

    private static string? GetFileName(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        try
        {
            if (Uri.TryCreate(sourcePath, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.LocalPath))
                return System.IO.Path.GetFileName(uri.LocalPath);

            return System.IO.Path.GetFileName(sourcePath);
        }
        catch (ArgumentException)
        {
            return sourcePath;
        }
    }
}

/// <summary>
/// Mutable, extensible index metadata attached to each vector record.
/// </summary>
internal sealed class AcVectorIndexSet
{
    [JsonPropertyName("ID")]
    public string? Id { get; set; }

    public long? Index { get; set; }

    public List<string> Callers { get; set; } = [];

    public List<string> Callees { get; set; } = [];

    public AcVectorSource? Source { get; set; }

    public string? Kind { get; set; }

    public string? Collection { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Future index families can be stored here without changing the file format.
    /// </summary>
    public Dictionary<string, string[]> Custom { get; set; } = [];

    public static AcVectorIndexSet FromSegment(Segment segment, long index)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var custom = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, value) in segment.Metadata)
        {
            if (KnownMetadataKeys.Contains(key))
                continue;

            var values = MetadataReader.ToStringList(value);
            if (values.Count > 0)
                custom[key] = [.. values];
        }

        return new AcVectorIndexSet
        {
            Id = segment.Id.ToString(),
            Index = index,
            Callers = MetadataReader.GetStringList(segment.Metadata, "callers", "Callers", "caller", "Caller"),
            Callees = MetadataReader.GetStringList(segment.Metadata, "callees", "Callees", "callee", "Callee"),
            Source = AcVectorSource.FromSegment(segment),
            Kind = segment.Kind.ToString(),
            Collection = segment.CollectionName,
            CreatedAt = segment.CreatedAt,
            UpdatedAt = segment.UpdatedAt,
            Custom = custom
        };
    }

    private static readonly HashSet<string> KnownMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "caller",
        "callers",
        "callee",
        "callees",
        "sourceContent",
        "fileContent",
        "sourceKind",
        "timeCodeStart",
        "startTimeCode",
        "startTime",
        "timeCodeEnd",
        "endTimeCode",
        "endTime"
    };
}

/// <summary>
/// A single vector row in a Dimension_n.acv file.
/// </summary>
internal sealed class AcVectorRecord
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = string.Empty;

    public long Index { get; set; }

    public float[] Vector { get; set; } = [];

    public AcVectorIndexSet Indexes { get; set; } = new();

    public DateTimeOffset WrittenAt { get; set; } = DateTimeOffset.UtcNow;

    public static AcVectorRecord FromSegment(Segment segment, long index)
    {
        if (segment.Embedding is not { } embedding)
            throw new InvalidOperationException($"Segment {segment.Id} has no embedding vector.");

        return new AcVectorRecord
        {
            Id = segment.Id.ToString(),
            Index = index,
            Vector = embedding.Span.ToArray(),
            Indexes = AcVectorIndexSet.FromSegment(segment, index),
            WrittenAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Declares an index family stored in a vector database file.
/// </summary>
internal sealed class AcIndexDefinition
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool MultiValue { get; init; }

    public bool CanBeEmpty { get; init; } = true;

    public bool MutableAfterCreation { get; init; } = true;
}

/// <summary>
/// JSON payload stored in each Dimension_n.acv file.
/// </summary>
internal sealed class AcVectorDatabaseFile
{
    public string Format { get; set; } = AcVectorStoreFile.FormatName;

    public int Version { get; set; } = AcVectorStoreFile.CurrentVersion;

    public int Dimensions { get; set; }

    public List<AcIndexDefinition> IndexCatalog { get; set; } = AcVectorStoreFile.CreateDefaultIndexCatalog();

    public List<AcVectorRecord> Vectors { get; set; } = [];
}

internal static class MetadataReader
{
    public static string? GetString(IReadOnlyDictionary<string, object> metadata, params string[] keys) =>
        GetStringList(metadata, keys).FirstOrDefault();

    public static List<string> GetStringList(IReadOnlyDictionary<string, object> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGetValue(metadata, key, out var value))
                return ToStringList(value);
        }

        return [];
    }

    public static List<string> ToStringList(object? value)
    {
        if (value is null)
            return [];

        if (value is string text)
            return string.IsNullOrWhiteSpace(text) ? [] : [text];

        if (value is JsonElement element)
            return JsonElementToStrings(element);

        if (value is IEnumerable<string> strings)
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        if (value is IEnumerable enumerable)
        {
            var values = new List<string>();
            foreach (var item in enumerable)
            {
                var itemText = item?.ToString();
                if (!string.IsNullOrWhiteSpace(itemText))
                    values.Add(itemText);
            }

            return values;
        }

        var scalar = value.ToString();
        return string.IsNullOrWhiteSpace(scalar) ? [] : [scalar];
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, object> metadata, string key, out object? value)
    {
        if (metadata.TryGetValue(key, out var exact))
        {
            value = exact;
            return true;
        }

        foreach (var (candidateKey, candidateValue) in metadata)
        {
            if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static List<string> JsonElementToStrings(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? [] : [element.GetString()!],
            JsonValueKind.Array => element.EnumerateArray()
                .SelectMany(JsonElementToStrings)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => [element.ToString()],
            _ => []
        };
    }
}
