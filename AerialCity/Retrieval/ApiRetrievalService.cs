using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;
using AerialCity.Database;
using AerialCity.Delegates;
using AerialCity.Embedding;
using AerialCity.Retrieval.Strategy;
using AerialCity.VectorStore.Search;
using AerialCity.VectorStore.Similarity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AerialCity.Retrieval;

/// <summary>
/// Creates API-backed retrieval delegates that embed the query content per call,
/// open a file-backed database by location, and execute BM25, vector, or hybrid retrieval.
/// </summary>
public sealed class ApiRetrievalService
{
    private readonly ApiEmbeddingService _embeddingService;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates a retrieval service using shared HTTP and null logging.</summary>
    public ApiRetrievalService()
        : this(new ApiEmbeddingService(), NullLoggerFactory.Instance)
    {
    }

    internal ApiRetrievalService(ILoggerFactory? loggerFactory)
        : this(new ApiEmbeddingService(), loggerFactory ?? NullLoggerFactory.Instance)
    {
    }

    /// <summary>Creates a retrieval service with a caller-supplied HTTP client.</summary>
    public ApiRetrievalService(HttpClient httpClient, ILoggerFactory? loggerFactory = null)
        : this(new ApiEmbeddingService(httpClient), loggerFactory ?? NullLoggerFactory.Instance)
    {
    }

    private ApiRetrievalService(ApiEmbeddingService embeddingService, ILoggerFactory loggerFactory)
    {
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>Returns the retrieval delegate exposed by this service.</summary>
    public RetrieveContentDelegate CreateRetrievalDelegate() => RetrieveAsync;

    /// <summary>Creates a retrieval delegate using the shared HTTP client.</summary>
    public static RetrieveContentDelegate CreateDelegate() =>
        new ApiRetrievalService().CreateRetrievalDelegate();

    /// <summary>
    /// Embeds the request content when needed, opens the target database, and returns ranked retrieval results.
    /// </summary>
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        ApiRetrievalRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var location = ResolveDatabaseLocation(request);
        var textQuery = ResolveTextQuery(request);
        var queryVector = await ResolveQueryVectorAsync(request, textQuery, ct);
        var metric = CreateSimilarityMetric(request.Method);
        var retrievalOptions = ResolveRetrievalOptions(request);

        await using var db = await OpenDatabaseAsync(request, location, metric, ct);

        var searchEngine = new VectorSearchEngine(
            db.VectorIndex,
            db.Storage,
            metric,
            _loggerFactory.CreateLogger<VectorSearchEngine>());

        var strategy = new HybridRetrievalStrategy(
            retrievalOptions,
            _loggerFactory.CreateLogger<HybridRetrievalStrategy>());

        var engine = new RetrievalEngine(
            searchEngine,
            db.Bm25,
            strategy,
            db.Storage,
            _loggerFactory.CreateLogger<RetrievalEngine>(),
            metric);

        return await engine.RetrieveAsync(new RetrievalQuery
        {
            TextQuery = UsesTextQuery(request.Method) ? textQuery : null,
            QueryVector = queryVector,
            TopK = request.TopK,
            MinScore = request.MinScore,
            CollectionFilter = request.CollectionFilter,
            KindFilter = request.KindFilter
        }, ct);
    }

    private async Task<AerialDatabase> OpenDatabaseAsync(
        ApiRetrievalRequest request,
        DatabaseLocation location,
        ISimilarityMetric metric,
        CancellationToken ct)
    {
        var storageOptions = new StorageOptions
        {
            BasePath = location.BasePath,
            EnableWal = request.EnableWal
        };

        if (request.PageSizeBytes.HasValue)
            storageOptions.PageSizeBytes = request.PageSizeBytes.Value;

        if (request.MaxWalSizeBytes.HasValue)
            storageOptions.MaxWalSizeBytes = request.MaxWalSizeBytes.Value;

        var options = new DatabaseOptions
        {
            Name = location.Name,
            Storage = storageOptions,
            SimilarityMetric = metric,
            HnswOptions = request.HnswOptions ?? new()
        };

        var factory = new DatabaseFactory(_loggerFactory);
        return await factory.CreateAsync(options, ct);
    }

    private async Task<EmbeddingVector?> ResolveQueryVectorAsync(
        ApiRetrievalRequest request,
        string? textQuery,
        CancellationToken ct)
    {
        if (!UsesQueryEmbedding(request.Method))
            return null;

        var result = await _embeddingService.EmbedAsync(new ApiEmbeddingRequest
        {
            ApiKey = request.ApiKey!,
            BaseUrl = request.BaseUrl,
            ApiType = request.ApiType,
            Model = request.Model!,
            Content = request.Content ?? EmbeddingInput.FromText(textQuery!),
            Parameters = request.Parameters,
            Dimensions = request.Dimensions,
            Normalize = request.Normalize,
            IncludeBinaryDataInTextProjection = request.IncludeBinaryDataInTextProjection
        }, ct);

        return result.Vector;
    }

    private static RetrievalOptions ResolveRetrievalOptions(ApiRetrievalRequest request)
    {
        if (request.RetrievalOptions is not null)
            return request.RetrievalOptions;

        if (request.Method == RetrievalMethod.Hybrid)
            return new RetrievalOptions();

        return request.Method == RetrievalMethod.BM25
            ? new RetrievalOptions { UseRrf = false, Bm25Weight = 1.0f, VectorWeight = 0.0f }
            : new RetrievalOptions { UseRrf = false, Bm25Weight = 0.0f, VectorWeight = 1.0f };
    }

    private static string? ResolveTextQuery(ApiRetrievalRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.TextQuery))
            return request.TextQuery;

        if (request.Content is null)
            return null;

        var textParts = request.Content.Parts
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return textParts.Length == 0
            ? null
            : string.Join("\n\n", textParts);
    }

    private static ISimilarityMetric CreateSimilarityMetric(RetrievalMethod method) =>
        method switch
        {
            RetrievalMethod.DotProduct => new DotProductSimilarity(),
            RetrievalMethod.Euclidean => new EuclideanDistance(),
            RetrievalMethod.Hybrid or RetrievalMethod.BM25 or RetrievalMethod.Cosine => new CosineSimilarity(),
            _ => throw new RetrievalException($"Unsupported retrieval method: {method}.")
        };

    private static bool UsesQueryEmbedding(RetrievalMethod method) =>
        method is RetrievalMethod.Hybrid or RetrievalMethod.Cosine
            or RetrievalMethod.DotProduct or RetrievalMethod.Euclidean;

    private static bool UsesTextQuery(RetrievalMethod method) =>
        method is RetrievalMethod.Hybrid or RetrievalMethod.BM25;

    private static void ValidateRequest(ApiRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DatabasePath))
            throw new RetrievalException("Database path is required.");

        if (request.TopK <= 0)
            throw new RetrievalException("TopK must be greater than zero.");

        if (request.PageSizeBytes is <= 0)
            throw new RetrievalException("PageSizeBytes must be greater than zero.");

        if (request.MaxWalSizeBytes is <= 0)
            throw new RetrievalException("MaxWalSizeBytes must be greater than zero.");

        if (!Enum.IsDefined(typeof(RetrievalMethod), request.Method))
            throw new RetrievalException($"Unsupported retrieval method: {request.Method}.");

        var textQuery = ResolveTextQuery(request);
        if (request.Method == RetrievalMethod.BM25 && string.IsNullOrWhiteSpace(textQuery))
            throw new RetrievalException("TextQuery or text content is required for BM25 retrieval.");

        if (!UsesQueryEmbedding(request.Method))
            return;

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new RetrievalException("Embedding API key is required for vector-based retrieval.");

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new RetrievalException("Embedding model is required for vector-based retrieval.");

        if (request.Content is null && string.IsNullOrWhiteSpace(textQuery))
            throw new RetrievalException("Content or TextQuery is required for vector-based retrieval.");
    }

    private static DatabaseLocation ResolveDatabaseLocation(ApiRetrievalRequest request)
    {
        var fullPath = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? Path.GetFullPath(request.DatabasePath.Trim())
            : Path.GetFullPath(Path.Combine(request.DatabasePath.Trim(), request.DatabaseName.Trim()));

        if (!Directory.Exists(fullPath))
            throw new RetrievalException($"Database path does not exist: {fullPath}");

        var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(name))
            throw new RetrievalException($"Database path must point to a database directory: {fullPath}");

        var parent = Directory.GetParent(trimmedPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            throw new RetrievalException($"Database path must have a parent directory: {fullPath}");

        return new DatabaseLocation(parent, name);
    }

    private readonly record struct DatabaseLocation(string BasePath, string Name);
}
