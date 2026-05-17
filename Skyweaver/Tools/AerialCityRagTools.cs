using System.Windows;
using System.IO;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.AerialCityRag;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class InitializeAerialCityRagTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "InitializeAerialCityRAG";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Initializes AerialCity RAG for a target folder. It creates AerialCity.xml in the configured AerialCity directory when missing, records the target-folder to database-folder mapping, embeds text/code files with the selected embedding model, embeds images when the model supports multimodal embedding, and inserts segments into the AerialCity vector and graph database.",
            "Script",
            [
                new SkyweaverToolParameterDefinition(
                    "TargetDirectory",
                    "Target folder to initialize for AerialCity RAG. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "MaxFileBytes",
                    "Maximum single file size to embed. Default is 10485760 bytes. Use 0 to disable this guard.",
                    SkyweaverToolParameterType.Integer,
                    isRequired: false,
                    defaultValue: "10485760")
            ],
            defaultToolKitKeys: ["Investigate"]);

        private readonly AerialCityRagService _ragService = new();

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "Initializes AerialCity RAG for TargetDirectory. This tool is available only when Preferences > Semantic Search has AerialCity RAG enabled and an AerialCity directory is configured. It creates/updates AerialCity.xml, records the current embedding model, segments code files and text files with AerialCity's dedicated methods, embeds images when the selected model supports multimodal embedding, and stores the resulting vectors plus source metadata in AerialCity. MaxFileBytes defaults to 10485760; pass 0 to disable the size guard.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Target directory", "TargetDirectory", "Waiting for target directory..."),
                    new ToolInvocationCardFieldDefinition("Max file bytes", "MaxFileBytes", "Default 10485760")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            var targetDirectory = arguments.GetString("TargetDirectory") ?? string.Empty;
            var maxFileBytes = arguments.GetInteger("MaxFileBytes", 10 * 1024 * 1024);

            try
            {
                var result = await _ragService.InitializeAsync(
                    targetDirectory,
                    context.WorkspacePath,
                    maxFileBytes,
                    cancellationToken).ConfigureAwait(false);

                return SkyweaverToolResult.Success(
                    AerialCityRagService.FormatInitializationResult(result),
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["targetDirectory"] = result.TargetDirectory,
                        ["aerialCityDirectory"] = result.AerialCityDirectory,
                        ["registryFilePath"] = result.RegistryFilePath,
                        ["databasePath"] = result.DatabasePath,
                        ["databaseFolderName"] = result.DatabaseFolderName,
                        ["embeddingModel"] = result.EmbeddingModelDisplayName,
                        ["embeddingModelId"] = result.EmbeddingModelId,
                        ["supportsMultimodalEmbedding"] = result.SupportsMultimodalEmbedding,
                        ["filesVisited"] = result.Statistics.FilesVisited,
                        ["segmentsInserted"] = result.Statistics.SegmentsInserted,
                        ["filesFailed"] = result.Statistics.FilesFailed
                    });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return SkyweaverToolResult.Failure($"Failed to initialize AerialCity RAG: {ex.Message}");
            }
        }
    }

    public sealed class SemanticSearchTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "SemanticSearch";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Searches initialized AerialCity RAG databases with cosine similarity. SearchPath must be inside a folder initialized with InitializeAerialCityRAG.",
            "Script",
            [
                new SkyweaverToolParameterDefinition(
                    "SearchPath",
                    "Folder or file path to search. It must be equal to or inside an initialized AerialCity target folder. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Query",
                    "Semantic query text to embed and search with cosine similarity.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "TopK",
                    "Maximum number of results. Default is 10.",
                    SkyweaverToolParameterType.Integer,
                    isRequired: false,
                    defaultValue: "10"),
                new SkyweaverToolParameterDefinition(
                    "MinScore",
                    "Optional minimum cosine score.",
                    SkyweaverToolParameterType.Number,
                    isRequired: false)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["Investigate"]);

        private readonly AerialCityRagService _ragService = new();

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "Searches initialized AerialCity RAG databases with cosine similarity. SearchPath is required and must be equal to or inside a folder previously initialized with InitializeAerialCityRAG; if it is outside all initialized folders, the tool reports that initialization is required. Query is embedded using the embedding model recorded for that AerialCity database. TopK defaults to 10.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Search path", "SearchPath", "Waiting for search path..."),
                    new ToolInvocationCardFieldDefinition("Query", "Query", "Waiting for semantic query..."),
                    new ToolInvocationCardFieldDefinition("Top K", "TopK", "Default 10"),
                    new ToolInvocationCardFieldDefinition("Min score", "MinScore", "Optional")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _ragService.SemanticSearchAsync(
                    arguments.GetString("SearchPath") ?? string.Empty,
                    arguments.GetString("Query") ?? string.Empty,
                    context.WorkspacePath,
                    arguments.GetInteger("TopK", 10),
                    (float)arguments.GetNumber("MinScore", decimal.MinValue),
                    cancellationToken).ConfigureAwait(false);

                return SkyweaverToolResult.Success(
                    AerialCityRagService.FormatSearchResult(result),
                    BuildSearchData(result));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return SkyweaverToolResult.Failure($"Semantic search failed: {ex.Message}");
            }
        }

        private static IReadOnlyDictionary<string, object?> BuildSearchData(AerialCityRagSearchResult result)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["searchPath"] = result.SearchPath,
                ["targetDirectory"] = result.TargetDirectory,
                ["databasePath"] = result.DatabasePath,
                ["method"] = result.Method.ToString(),
                ["query"] = result.Query,
                ["topK"] = result.TopK,
                ["resultCount"] = result.Results.Count
            };
        }
    }

    public sealed class KeywordSearchTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "KeywordSearch";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Searches initialized AerialCity RAG databases with BM25 keyword ranking. This is often better than grep for source/text retrieval because it ranks relevant segments instead of only returning literal line matches.",
            "Script",
            [
                new SkyweaverToolParameterDefinition(
                    "SearchPath",
                    "Folder or file path to search. It must be equal to or inside an initialized AerialCity target folder. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Query",
                    "Keyword query text for BM25 ranking.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "TopK",
                    "Maximum number of results. Default is 10.",
                    SkyweaverToolParameterType.Integer,
                    isRequired: false,
                    defaultValue: "10"),
                new SkyweaverToolParameterDefinition(
                    "MinScore",
                    "Optional minimum BM25 score.",
                    SkyweaverToolParameterType.Number,
                    isRequired: false)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["Investigate"]);

        private readonly AerialCityRagService _ragService = new();

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "Searches initialized AerialCity RAG databases with BM25 keyword ranking. Prefer this over GrepSearch for many codebase investigations: it ranks semantically useful text/code segments and is usually better than grep when you need relevant passages rather than exact line matches. SearchPath must be inside a folder initialized with InitializeAerialCityRAG; otherwise initialize the corresponding folder first. TopK defaults to 10.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Search path", "SearchPath", "Waiting for search path..."),
                    new ToolInvocationCardFieldDefinition("Query", "Query", "Waiting for keyword query..."),
                    new ToolInvocationCardFieldDefinition("Top K", "TopK", "Default 10"),
                    new ToolInvocationCardFieldDefinition("Min score", "MinScore", "Optional")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _ragService.KeywordSearchAsync(
                    arguments.GetString("SearchPath") ?? string.Empty,
                    arguments.GetString("Query") ?? string.Empty,
                    context.WorkspacePath,
                    arguments.GetInteger("TopK", 10),
                    (float)arguments.GetNumber("MinScore", decimal.MinValue),
                    cancellationToken).ConfigureAwait(false);

                return SkyweaverToolResult.Success(
                    AerialCityRagService.FormatSearchResult(result),
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["searchPath"] = result.SearchPath,
                        ["targetDirectory"] = result.TargetDirectory,
                        ["databasePath"] = result.DatabasePath,
                        ["method"] = result.Method.ToString(),
                        ["query"] = result.Query,
                        ["topK"] = result.TopK,
                        ["resultCount"] = result.Results.Count
                    });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return SkyweaverToolResult.Failure($"Keyword search failed: {ex.Message}");
            }
        }
    }
}
