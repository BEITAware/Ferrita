using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;
using AerialCity.Database;
using AerialCity.Database.Schema;
using AerialCity.Delegates;
using AerialCity.Embedding;
using AerialCity.GraphStore.Index;
using AerialCity.GraphStore.Model;
using AerialCity.GraphStore.Trace;
using AerialCity.GraphStore.Traversal;
using AerialCity.Retrieval;
using AerialCity.Retrieval.Scoring;
using AerialCity.Retrieval.Strategy;
using AerialCity.Segmentation;
using AerialCity.VectorStore.Index;
using AerialCity.VectorStore.Search;
using AerialCity.VectorStore.Similarity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AerialCity;

/// <summary>
/// The main entry point for constructing an AerialCity engine instance.
/// Uses the Builder pattern to configure all subsystems and produces
/// a set of <see cref="AerialCityEngine"/> delegates for the caller.
/// </summary>
/// <example>
/// <code>
/// var engine = new AerialCityBuilder()
///     .WithEmbeddingProvider(myProvider)
///     .WithStoragePath("./data")
///     .Build();
///
/// var createDatabase = engine.CreateDatabase();
/// var segmentContent = engine.SegmentContent();
/// var embedSegment = engine.EmbedSegment();
/// var insert = engine.Insert();
/// var retrieve = engine.Retrieve();
///
/// var db = await createDatabase(new DatabaseOptions { Name = "mydb" }, default);
/// var segments = await segmentContent(RawContent.FromCode(sourceCode, "csharp"));
/// foreach (var seg in segments)
/// {
///     await embedSegment(seg);
///     await insert(db, seg);
/// }
/// var results = await retrieve(db, new RetrievalQuery { TextQuery = "find me" });
/// </code>
/// </example>
public sealed class AerialCityBuilder
{
    private IEmbeddingProvider? _embeddingProvider;
    private ILoggerFactory? _loggerFactory;
    private ISimilarityMetric _metric = new CosineSimilarity();
    private HnswOptions _hnswOptions = new();
    private StorageOptions _storageOptions = new();
    private RetrievalOptions _retrievalOptions = new();
    private SegmentationOptions _segmentationOptions = new();
    private EmbeddingOptions _embeddingOptions = new();

    /// <summary>Sets the embedding provider (required for embed/retrieve operations).</summary>
    public AerialCityBuilder WithEmbeddingProvider(IEmbeddingProvider provider)
    { _embeddingProvider = provider; return this; }

    /// <summary>Sets the logger factory for all AerialCity components.</summary>
    public AerialCityBuilder WithLoggerFactory(ILoggerFactory factory)
    { _loggerFactory = factory; return this; }

    /// <summary>Sets the similarity metric for vector search.</summary>
    public AerialCityBuilder WithSimilarityMetric(ISimilarityMetric metric)
    { _metric = metric; return this; }

    /// <summary>Configures HNSW index parameters.</summary>
    public AerialCityBuilder WithHnswOptions(Action<HnswOptions> configure)
    { configure(_hnswOptions); return this; }

    /// <summary>Sets the base storage path for file-backed databases.</summary>
    public AerialCityBuilder WithStoragePath(string path)
    { _storageOptions.BasePath = path; return this; }

    /// <summary>Enables in-memory mode (no disk I/O).</summary>
    public AerialCityBuilder UseInMemoryStorage()
    { _storageOptions.InMemory = true; return this; }

    /// <summary>Configures retrieval strategy parameters.</summary>
    public AerialCityBuilder WithRetrievalOptions(Action<RetrievalOptions> configure)
    { configure(_retrievalOptions); return this; }

    /// <summary>Configures default segmentation parameters.</summary>
    public AerialCityBuilder WithSegmentationOptions(Action<SegmentationOptions> configure)
    { configure(_segmentationOptions); return this; }

    /// <summary>Configures embedding pipeline parameters.</summary>
    public AerialCityBuilder WithEmbeddingOptions(Action<EmbeddingOptions> configure)
    { configure(_embeddingOptions); return this; }

    /// <summary>
    /// Builds the AerialCity engine with all configured components.
    /// </summary>
    /// <returns>A fully configured <see cref="AerialCityEngine"/>.</returns>
    public AerialCityEngine Build()
    {
        var loggerFactory = _loggerFactory ?? NullLoggerFactory.Instance;
        var dbFactory = new DatabaseFactory(loggerFactory);

        // Build segmenters
        var segmenters = new Dictionary<SegmentKind, ISegmenter>
        {
            [SegmentKind.CodeBlock] = new AstSegmenter(loggerFactory.CreateLogger<AstSegmenter>()),
            [SegmentKind.TextPassage] = new PassageSegmenter(loggerFactory.CreateLogger<PassageSegmenter>()),
            [SegmentKind.VideoClip] = new VideoSegmenter(loggerFactory.CreateLogger<VideoSegmenter>())
        };

        // Build embedding pipeline (optional — will throw if used without provider)
        EmbeddingPipeline? embeddingPipeline = _embeddingProvider is not null
            ? new EmbeddingPipeline(_embeddingProvider, _embeddingOptions,
                loggerFactory.CreateLogger<EmbeddingPipeline>())
            : null;

        return new AerialCityEngine(
            dbFactory, segmenters, embeddingPipeline,
            _metric, _hnswOptions, _storageOptions,
            _retrievalOptions, _segmentationOptions,
            loggerFactory);
    }
}

/// <summary>
/// The AerialCity engine — provides all database and retrieval delegates.
/// This is the primary object callers interact with.
/// </summary>
public sealed class AerialCityEngine : IDisposable
{
    private readonly DatabaseFactory _dbFactory;
    private readonly Dictionary<SegmentKind, ISegmenter> _segmenters;
    private readonly EmbeddingPipeline? _embeddingPipeline;
    private readonly ISimilarityMetric _metric;
    private readonly HnswOptions _hnswOptions;
    private readonly StorageOptions _storageOptions;
    private readonly RetrievalOptions _retrievalOptions;
    private readonly SegmentationOptions _defaultSegOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CreateDatabaseDelegate _createDatabase;
    private readonly CreateCollectionDelegate _createCollection;
    private readonly GetCollectionDelegate _getCollection;
    private readonly ListCollectionsDelegate _listCollections;
    private readonly InsertSegmentDelegate _insert;
    private readonly UpdateSegmentDelegate _update;
    private readonly DeleteSegmentDelegate _delete;
    private readonly AddEdgeDelegate _addEdge;
    private readonly SegmentContentDelegate _segmentContent;
    private readonly EmbedSegmentDelegate _embedSegment;
    private readonly EmbedContentDelegate _embedContent;
    private readonly EmbedCodeFileDelegate _embedCodeFile;
    private readonly EmbedTextFileDelegate _embedTextFile;
    private readonly RetrieveContentDelegate _retrieveContent;
    private readonly RetrieveDelegate _retrieve;
    private readonly TraceDelegate _trace;

    internal AerialCityEngine(
        DatabaseFactory dbFactory,
        Dictionary<SegmentKind, ISegmenter> segmenters,
        EmbeddingPipeline? embeddingPipeline,
        ISimilarityMetric metric,
        HnswOptions hnswOptions,
        StorageOptions storageOptions,
        RetrievalOptions retrievalOptions,
        SegmentationOptions defaultSegOptions,
        ILoggerFactory loggerFactory)
    {
        _dbFactory = dbFactory;
        _segmenters = segmenters;
        _embeddingPipeline = embeddingPipeline;
        _metric = metric;
        _hnswOptions = hnswOptions;
        _storageOptions = storageOptions;
        _retrievalOptions = retrievalOptions;
        _defaultSegOptions = defaultSegOptions;
        _loggerFactory = loggerFactory;

        _createDatabase = CreateDatabaseAsync;
        _createCollection = CreateCollectionCore;
        _getCollection = GetCollectionCore;
        _listCollections = ListCollectionsCore;
        _insert = InsertAsync;
        _update = UpdateAsync;
        _delete = DeleteAsync;
        _addEdge = AddEdgeCore;
        _segmentContent = SegmentContentAsync;
        _embedSegment = EmbedSegmentAsync;
        _embedContent = ApiEmbeddingService.CreateDelegate();
        _embedCodeFile = ApiEmbeddingService.CreateCodeFileDelegate();
        _embedTextFile = ApiEmbeddingService.CreateTextFileDelegate();
        _retrieveContent = new ApiRetrievalService(_loggerFactory).CreateRetrievalDelegate();
        _retrieve = RetrieveAsync;
        _trace = TraceCore;
    }

    // ═══ Database Delegate Providers ═══

    /// <summary>Creates a new AerialCity database.</summary>
    public CreateDatabaseDelegate CreateDatabase() => _createDatabase;

    /// <summary>Creates a collection in a database.</summary>
    public CreateCollectionDelegate CreateCollection() => _createCollection;

    /// <summary>Gets a collection schema by name.</summary>
    public GetCollectionDelegate GetCollection() => _getCollection;

    /// <summary>Lists all collection names.</summary>
    public ListCollectionsDelegate ListCollections() => _listCollections;

    /// <summary>Inserts a segment into a database.</summary>
    public InsertSegmentDelegate Insert() => _insert;

    /// <summary>Updates an existing segment.</summary>
    public UpdateSegmentDelegate Update() => _update;

    /// <summary>Deletes a segment from a database.</summary>
    public DeleteSegmentDelegate Delete() => _delete;

    /// <summary>Adds a relationship edge between two segments.</summary>
    public AddEdgeDelegate AddEdge() => _addEdge;

    // ═══ Retrieve Delegate Providers ═══

    /// <summary>Segments raw content into semantic chunks.</summary>
    public SegmentContentDelegate SegmentContent() => _segmentContent;

    /// <summary>Embeds a segment using the configured embedding provider.</summary>
    public EmbedSegmentDelegate EmbedSegment() => _embedSegment;

    /// <summary>Embeds caller-supplied content using per-call API configuration.</summary>
    public EmbedContentDelegate EmbedContent() => _embedContent;

    /// <summary>Embeds a complete code file using Tree-sitter chunking and per-call API configuration.</summary>
    public EmbedCodeFileDelegate EmbedCodeFile() => _embedCodeFile;

    /// <summary>Embeds a complete text file using paragraph chunking and per-call API configuration.</summary>
    public EmbedTextFileDelegate EmbedTextFile() => _embedTextFile;

    /// <summary>Retrieves caller-supplied content using per-call API configuration and database location.</summary>
    public RetrieveContentDelegate RetrieveContent() => _retrieveContent;

    /// <summary>Retrieves segments matching a hybrid query.</summary>
    public RetrieveDelegate Retrieve() => _retrieve;

    /// <summary>Traces relationships for a segment through the graph.</summary>
    public TraceDelegate Trace() => _trace;

    private Task<AerialDatabase> CreateDatabaseAsync(
        DatabaseOptions options, CancellationToken ct = default) =>
        _dbFactory.CreateAsync(options, ct);

    private static void CreateCollectionCore(AerialDatabase db, CollectionSchema schema) =>
        db.CreateCollection(schema);

    private static CollectionSchema? GetCollectionCore(AerialDatabase db, string name) =>
        db.GetCollection(name);

    private static IReadOnlyList<string> ListCollectionsCore(AerialDatabase db) =>
        db.ListCollections();

    private static Task InsertAsync(AerialDatabase db, Segment segment, CancellationToken ct = default) =>
        db.InsertAsync(segment, ct);

    private static Task UpdateAsync(
        AerialDatabase db, AerialId id, Segment updated, CancellationToken ct = default) =>
        db.UpdateAsync(id, updated, ct);

    private static Task DeleteAsync(AerialDatabase db, AerialId id, CancellationToken ct = default) =>
        db.DeleteAsync(id, ct);

    private static void AddEdgeCore(
        AerialDatabase db,
        AerialId sourceSegmentId,
        AerialId targetSegmentId,
        EdgeKind kind,
        float weight = 1.0f) =>
        db.AddEdge(sourceSegmentId, targetSegmentId, kind, weight);

    private Task<IReadOnlyList<Segment>> SegmentContentAsync(
        RawContent content, SegmentationOptions? options = null)
    {
        var opts = options ?? _defaultSegOptions;
        var kind = content.SuggestedKind;
        var segmenter = _segmenters.GetValueOrDefault(kind)
            ?? _segmenters[SegmentKind.TextPassage]; // fallback
        return Task.FromResult(segmenter.Segment(content, opts));
    }

    private async Task EmbedSegmentAsync(Segment segment, CancellationToken ct = default)
    {
        if (_embeddingPipeline is null)
            throw new InvalidOperationException("No embedding provider configured.");
        await _embeddingPipeline.EmbedAsync(segment, ct);
    }

    private async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        AerialDatabase db, RetrievalQuery query, CancellationToken ct = default)
    {
        var searchEngine = new VectorSearchEngine(
            db.VectorIndex, db.Storage, _metric,
            _loggerFactory.CreateLogger<VectorSearchEngine>());

        var strategy = new HybridRetrievalStrategy(
            _retrievalOptions,
            _loggerFactory.CreateLogger<HybridRetrievalStrategy>());

        var engine = new RetrievalEngine(
            searchEngine, db.Bm25, strategy, db.Storage,
            _loggerFactory.CreateLogger<RetrievalEngine>(),
            _metric);

        return await engine.RetrieveAsync(query, ct);
    }

    private TraceResult TraceCore(AerialDatabase db, TraceQuery query)
    {
        var traverser = new BfsTraverser();
        var engine = new TraceEngine(
            db.GraphIndex, traverser,
            _loggerFactory.CreateLogger<TraceEngine>());
        return engine.Execute(query);
    }

    /// <summary>Disposes the engine facade.</summary>
    public void Dispose()
    {
        // Engine is stateless; databases manage their own lifecycle
    }
}
