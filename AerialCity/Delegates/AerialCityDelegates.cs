using AerialCity.Core.Primitives;
using AerialCity.Database;
using AerialCity.Database.Schema;
using AerialCity.Embedding;
using AerialCity.GraphStore.Model;
using AerialCity.GraphStore.Trace;
using AerialCity.Retrieval;

namespace AerialCity.Delegates;

// ═══════════════════════════════════════════════════════════════════
//  AerialCity Database Delegates
//  These delegates define the public API for database lifecycle and
//  CUD (Create/Update/Delete) operations.
// ═══════════════════════════════════════════════════════════════════

/// <summary>Creates a new AerialCity database.</summary>
/// <param name="options">Database configuration.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The created database instance.</returns>
public delegate Task<AerialDatabase> CreateDatabaseDelegate(
    DatabaseOptions options, CancellationToken ct = default);

/// <summary>Creates a collection in the database.</summary>
/// <param name="db">The target database.</param>
/// <param name="schema">The collection schema.</param>
public delegate void CreateCollectionDelegate(
    AerialDatabase db, CollectionSchema schema);

/// <summary>Gets a collection schema from the database.</summary>
/// <param name="db">The target database.</param>
/// <param name="name">The collection name.</param>
/// <returns>The matching collection schema, if it exists.</returns>
public delegate CollectionSchema? GetCollectionDelegate(
    AerialDatabase db, string name);

/// <summary>Lists all collection names in the database.</summary>
/// <param name="db">The target database.</param>
/// <returns>Collection names.</returns>
public delegate IReadOnlyList<string> ListCollectionsDelegate(
    AerialDatabase db);

/// <summary>Inserts a segment into the database.</summary>
/// <param name="db">The target database.</param>
/// <param name="segment">The segment to insert.</param>
/// <param name="ct">Cancellation token.</param>
public delegate Task InsertSegmentDelegate(
    AerialDatabase db, Segment segment, CancellationToken ct = default);

/// <summary>Updates an existing segment in the database.</summary>
/// <param name="db">The target database.</param>
/// <param name="id">The ID of the segment to update.</param>
/// <param name="updated">The updated segment data.</param>
/// <param name="ct">Cancellation token.</param>
public delegate Task UpdateSegmentDelegate(
    AerialDatabase db, AerialId id, Segment updated, CancellationToken ct = default);

/// <summary>Deletes a segment from the database.</summary>
/// <param name="db">The target database.</param>
/// <param name="id">The ID of the segment to delete.</param>
/// <param name="ct">Cancellation token.</param>
public delegate Task DeleteSegmentDelegate(
    AerialDatabase db, AerialId id, CancellationToken ct = default);

/// <summary>Adds a relationship edge between two segments.</summary>
/// <param name="db">The target database.</param>
/// <param name="sourceSegmentId">The source segment ID.</param>
/// <param name="targetSegmentId">The target segment ID.</param>
/// <param name="kind">The edge kind.</param>
/// <param name="weight">The edge weight.</param>
public delegate void AddEdgeDelegate(
    AerialDatabase db,
    AerialId sourceSegmentId,
    AerialId targetSegmentId,
    EdgeKind kind,
    float weight = 1.0f);

// ═══════════════════════════════════════════════════════════════════
//  AerialCity Retrieve Delegates
//  These delegates define the public API for the RAG retrieval pipeline:
//  segmentation, embedding, retrieval, and trace.
// ═══════════════════════════════════════════════════════════════════

/// <summary>Segments raw content into a list of segments.</summary>
/// <param name="content">The raw content to segment.</param>
/// <param name="options">Segmentation configuration.</param>
/// <returns>The extracted segments.</returns>
public delegate Task<IReadOnlyList<Segment>> SegmentContentDelegate(
    RawContent content, Segmentation.SegmentationOptions? options = null);

/// <summary>Embeds a segment, populating its embedding vector.</summary>
/// <param name="segment">The segment to embed.</param>
/// <param name="ct">Cancellation token.</param>
public delegate Task EmbedSegmentDelegate(
    Segment segment, CancellationToken ct = default);

/// <summary>
/// Embeds caller-supplied content by passing API configuration with each call.
/// </summary>
/// <param name="request">API credentials, model configuration, and content to embed.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The generated vector and the embedded segment when a segment was supplied.</returns>
public delegate Task<EmbeddingResult> EmbedContentDelegate(
    ApiEmbeddingRequest request, CancellationToken ct = default);

/// <summary>
/// Embeds a complete source code file by splitting it into AST-aware chunks first.
/// </summary>
/// <param name="request">API credentials, model configuration, code file input, and chunking limits.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The embedding result for each generated code chunk.</returns>
public delegate Task<IReadOnlyList<EmbeddingResult>> EmbedCodeFileDelegate(
    ApiCodeFileEmbeddingRequest request, CancellationToken ct = default);

/// <summary>
/// Embeds a complete text file by splitting it into paragraph-based chunks first.
/// </summary>
/// <param name="request">API credentials, model configuration, text file input, and chunking limits.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The embedding result for each generated text chunk.</returns>
public delegate Task<IReadOnlyList<EmbeddingResult>> EmbedTextFileDelegate(
    ApiTextFileEmbeddingRequest request, CancellationToken ct = default);

/// <summary>
/// Retrieves caller-supplied content by passing API configuration and database location with each call.
/// </summary>
/// <param name="request">API credentials, embedding model configuration, retrieval method, and database location.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>Ranked retrieval results.</returns>
public delegate Task<IReadOnlyList<RetrievalResult>> RetrieveContentDelegate(
    ApiRetrievalRequest request, CancellationToken ct = default);

/// <summary>Retrieves segments matching a query using hybrid BM25+vector search.</summary>
/// <param name="db">The database to search.</param>
/// <param name="query">The retrieval query.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>Ranked retrieval results.</returns>
public delegate Task<IReadOnlyList<RetrievalResult>> RetrieveDelegate(
    AerialDatabase db, RetrievalQuery query, CancellationToken ct = default);

/// <summary>Traces relationships for a segment through the graph store.</summary>
/// <param name="db">The database to trace in.</param>
/// <param name="query">The trace query.</param>
/// <returns>Trace results with related nodes and edges.</returns>
public delegate TraceResult TraceDelegate(
    AerialDatabase db, TraceQuery query);
