using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AerialCity;
using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using AerialCity.Core.Storage;
using AerialCity.Database;
using AerialCity.Embedding;
using AerialCity.Retrieval;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Services;
using Skyweaver.Models.AerialCityRag;
using Skyweaver.Services.Directories;

namespace Skyweaver.Services.AerialCityRag
{
    public sealed class AerialCityRagService
    {
        private const int DefaultTopK = 10;
        private const int MaximumTopK = 50;
        private const int DefaultMaximumFileBytes = 10 * 1024 * 1024;
        private const int MaximumRecordedFailures = 20;

        private static readonly HashSet<string> s_codeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".bat", ".c", ".cc", ".clj", ".cpp", ".cs", ".cshtml", ".csproj", ".css", ".fs", ".fsx",
            ".go", ".h", ".hpp", ".html", ".java", ".js", ".json", ".jsx", ".kt", ".kts", ".lua",
            ".php", ".ps1", ".py", ".razor", ".rb", ".rs", ".sh", ".sql", ".swift", ".ts", ".tsx",
            ".vb", ".xaml", ".xml", ".yaml", ".yml"
        };

        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
        };

        private readonly AerialCityRagConfigurationRepository _configurationRepository;
        private readonly AerialCityRagRegistry _registry;
        private readonly EmbeddingModelConfigurationRepository _embeddingModelRepository;
        private readonly EmbeddingModelService _embeddingModelService;

        public AerialCityRagService()
            : this(
                new AerialCityRagConfigurationRepository(),
                new AerialCityRagRegistry(),
                new EmbeddingModelConfigurationRepository(new EmbeddingModelConfigurationPathProvider()),
                new EmbeddingModelService())
        {
        }

        public AerialCityRagService(
            AerialCityRagConfigurationRepository configurationRepository,
            AerialCityRagRegistry registry,
            EmbeddingModelConfigurationRepository embeddingModelRepository,
            EmbeddingModelService embeddingModelService)
        {
            _configurationRepository = configurationRepository ?? throw new ArgumentNullException(nameof(configurationRepository));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _embeddingModelRepository = embeddingModelRepository ?? throw new ArgumentNullException(nameof(embeddingModelRepository));
            _embeddingModelService = embeddingModelService ?? throw new ArgumentNullException(nameof(embeddingModelService));
        }

        public async Task<AerialCityRagInitializationResult> InitializeAsync(
            string requestedTargetDirectory,
            string? workspacePath,
            int maximumFileBytes = DefaultMaximumFileBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var configuration = RequireEnabledConfiguration();
            var model = ResolveSelectedEmbeddingModel(configuration);
            var targetDirectory = ResolveExistingDirectory(requestedTargetDirectory, workspacePath);
            var aerialCityDirectory = SkyweaverDirectoryRuntime.Instance.AerialCityDirectoryPath;
            Directory.CreateDirectory(aerialCityDirectory);

            maximumFileBytes = maximumFileBytes <= 0 ? int.MaxValue : maximumFileBytes;

            var databaseFolderName = AerialCityRagRegistry.CreateDatabaseFolderName(targetDirectory);
            var databasePath = Path.Combine(aerialCityDirectory, databaseFolderName);
            var now = DateTimeOffset.UtcNow;
            var existingMapping = _registry.Load(aerialCityDirectory).FirstOrDefault(mapping =>
                string.Equals(
                    AerialCityRagRegistry.NormalizePath(mapping.TargetPath),
                    targetDirectory,
                    StringComparison.OrdinalIgnoreCase));

            var mapping = new AerialCityRagFolderMapping
            {
                TargetPath = targetDirectory,
                DatabaseFolderName = databaseFolderName,
                DatabasePath = databasePath,
                EmbeddingModelKey = model.Key,
                EmbeddingModelDisplayName = model.DisplayName,
                EmbeddingModelId = model.SummaryModelId,
                EmbeddingInterfaceType = model.InterfaceType,
                EmbeddingDimensions = model.Dimensions,
                SupportsMultimodalEmbedding = model.SupportsMultimodalEmbedding,
                InitializedAtUtc = existingMapping?.InitializedAtUtc == default || existingMapping == null
                    ? now
                    : existingMapping.InitializedAtUtc,
                UpdatedAtUtc = now
            };

            _registry.Upsert(aerialCityDirectory, mapping);

            using var engine = new AerialCityBuilder().Build();
            var createDatabase = engine.CreateDatabase();
            var embedCodeFile = engine.EmbedCodeFile();
            var embedTextFile = engine.EmbedTextFile();
            var embedContent = engine.EmbedContent();
            var insert = engine.Insert();

            await using var database = await createDatabase(new DatabaseOptions
            {
                Name = databaseFolderName,
                Storage = new StorageOptions
                {
                    BasePath = aerialCityDirectory
                }
            }, cancellationToken).ConfigureAwait(false);

            var template = CreateEmbeddingTemplate(model, EmbeddingInput.FromText("AerialCity RAG initialization"));
            var indexedSources = await LoadIndexedSourceUrisAsync(databasePath, cancellationToken).ConfigureAwait(false);
            var statistics = new AerialCityRagInitializationStatistics();

            foreach (var filePath in EnumerateFiles(targetDirectory, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                statistics.FilesVisited++;

                if (indexedSources.Contains(filePath))
                {
                    statistics.FilesSkippedAlreadyIndexed++;
                    continue;
                }

                if (IsFileTooLarge(filePath, maximumFileBytes))
                {
                    statistics.FilesSkippedTooLarge++;
                    continue;
                }

                var extension = Path.GetExtension(filePath);
                var probe = await ProbeFileAsync(filePath, cancellationToken).ConfigureAwait(false);

                try
                {
                    if (probe.IsBinary)
                    {
                        if (!model.SupportsMultimodalEmbedding)
                        {
                            statistics.BinaryFilesSkipped++;
                            continue;
                        }

                        if (!s_imageExtensions.Contains(extension))
                        {
                            statistics.BinaryFilesSkipped++;
                            continue;
                        }

                        var inserted = await EmbedAndInsertImageAsync(
                            embedContent,
                            insert,
                            database,
                            template,
                            filePath,
                            targetDirectory,
                            cancellationToken).ConfigureAwait(false);

                        statistics.ImageFilesEmbedded++;
                        statistics.SegmentsInserted += inserted;
                        indexedSources.Add(filePath);
                        continue;
                    }

                    var insertedSegments = s_codeExtensions.Contains(extension)
                        ? await EmbedAndInsertCodeFileAsync(
                            embedCodeFile,
                            embedTextFile,
                            insert,
                            database,
                            template,
                            filePath,
                            targetDirectory,
                            probe.Encoding,
                            model.MaxInputTokens,
                            statistics,
                            cancellationToken).ConfigureAwait(false)
                        : await EmbedAndInsertTextFileAsync(
                            embedTextFile,
                            insert,
                            database,
                            template,
                            filePath,
                            targetDirectory,
                            probe.Encoding,
                            model.MaxInputTokens,
                            cancellationToken).ConfigureAwait(false);

                    if (s_codeExtensions.Contains(extension))
                    {
                        statistics.CodeFilesEmbedded++;
                    }
                    else
                    {
                        statistics.TextFilesEmbedded++;
                    }

                    statistics.SegmentsInserted += insertedSegments;
                    indexedSources.Add(filePath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or AerialCityException)
                {
                    statistics.FilesFailed++;
                    if (statistics.Failures.Count < MaximumRecordedFailures)
                    {
                        statistics.Failures.Add($"{filePath}: {ex.Message}");
                    }
                }
            }

            return new AerialCityRagInitializationResult
            {
                TargetDirectory = targetDirectory,
                AerialCityDirectory = aerialCityDirectory,
                RegistryFilePath = _registry.GetRegistryFilePath(aerialCityDirectory),
                DatabasePath = databasePath,
                DatabaseFolderName = databaseFolderName,
                EmbeddingModelDisplayName = model.DisplayName,
                EmbeddingModelId = model.SummaryModelId,
                SupportsMultimodalEmbedding = model.SupportsMultimodalEmbedding,
                Statistics = statistics
            };
        }

        public Task<AerialCityRagSearchResult> SemanticSearchAsync(
            string requestedSearchPath,
            string query,
            string? workspacePath,
            int topK = DefaultTopK,
            float minScore = float.NegativeInfinity,
            CancellationToken cancellationToken = default)
        {
            return SearchAsync(
                requestedSearchPath,
                query,
                workspacePath,
                RetrievalMethod.Cosine,
                topK,
                minScore,
                cancellationToken);
        }

        public Task<AerialCityRagSearchResult> KeywordSearchAsync(
            string requestedSearchPath,
            string query,
            string? workspacePath,
            int topK = DefaultTopK,
            float minScore = float.NegativeInfinity,
            CancellationToken cancellationToken = default)
        {
            return SearchAsync(
                requestedSearchPath,
                query,
                workspacePath,
                RetrievalMethod.BM25,
                topK,
                minScore,
                cancellationToken);
        }

        private async Task<AerialCityRagSearchResult> SearchAsync(
            string requestedSearchPath,
            string query,
            string? workspacePath,
            RetrievalMethod method,
            int topK,
            float minScore,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireEnabledConfiguration();

            if (string.IsNullOrWhiteSpace(query))
            {
                throw new InvalidOperationException("Search query cannot be empty.");
            }

            var searchPath = ResolveExistingPath(requestedSearchPath, workspacePath);
            var aerialCityDirectory = SkyweaverDirectoryRuntime.Instance.AerialCityDirectoryPath;
            var mapping = _registry.FindBestMappingForPath(aerialCityDirectory, searchPath);
            if (mapping == null)
            {
                throw new InvalidOperationException(
                    $"SearchPath is not inside any initialized AerialCity folder: {searchPath}. Run InitializeAerialCityRAG for the corresponding folder first.");
            }

            var normalizedTopK = Math.Clamp(topK, 1, MaximumTopK);
            var candidateLimit = Math.Min(500, Math.Max(normalizedTopK * 10, normalizedTopK));
            var model = method == RetrievalMethod.BM25 ? null : ResolveEmbeddingModelForMapping(mapping);
            var request = CreateRetrievalRequest(mapping, model, method, query, candidateLimit, minScore);
            var retrieve = new ApiRetrievalService().CreateRetrievalDelegate();
            var rawResults = await retrieve(request, cancellationToken).ConfigureAwait(false);
            var filteredResults = rawResults
                .Where(result => IsResultInsideSearchPath(result, searchPath))
                .Take(normalizedTopK)
                .ToArray();

            return new AerialCityRagSearchResult
            {
                SearchPath = searchPath,
                TargetDirectory = mapping.TargetPath,
                DatabasePath = mapping.DatabasePath,
                Method = method,
                Query = query.Trim(),
                TopK = normalizedTopK,
                Results = filteredResults
            };
        }

        private AerialCityRagConfiguration RequireEnabledConfiguration()
        {
            var configuration = _configurationRepository.Load();
            if (!configuration.IsEnabled)
            {
                throw new InvalidOperationException("AerialCity RAG is disabled. Enable it in Preferences > Semantic Search first.");
            }

            var aerialCityDirectory = SkyweaverDirectoryRuntime.Instance.AerialCityDirectoryPath;
            if (string.IsNullOrWhiteSpace(aerialCityDirectory))
            {
                throw new InvalidOperationException("AerialCity directory is not configured. Set it in Preferences > Directory Locations first.");
            }

            return configuration;
        }

        private EmbeddingModelDefinition ResolveSelectedEmbeddingModel(AerialCityRagConfiguration configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration.SelectedEmbeddingModelKey))
            {
                throw new InvalidOperationException("No embedding model is selected for AerialCity RAG.");
            }

            var model = _embeddingModelRepository.Load().FirstOrDefault(candidate =>
                string.Equals(candidate.Key, configuration.SelectedEmbeddingModelKey, StringComparison.Ordinal));
            if (model == null)
            {
                throw new InvalidOperationException("The selected embedding model no longer exists.");
            }

            if (!model.IsFullyConfigured)
            {
                throw new InvalidOperationException($"Embedding model '{model.DisplayName}' is not fully configured.");
            }

            return model;
        }

        private EmbeddingModelDefinition ResolveEmbeddingModelForMapping(AerialCityRagFolderMapping mapping)
        {
            var model = _embeddingModelRepository.Load().FirstOrDefault(candidate =>
                string.Equals(candidate.Key, mapping.EmbeddingModelKey, StringComparison.Ordinal));
            if (model == null)
            {
                throw new InvalidOperationException(
                    $"Embedding model used for this AerialCity database is no longer configured: {mapping.EmbeddingModelDisplayName}");
            }

            if (!model.IsFullyConfigured)
            {
                throw new InvalidOperationException($"Embedding model '{model.DisplayName}' is not fully configured.");
            }

            return model;
        }

        private ApiEmbeddingRequest CreateEmbeddingTemplate(EmbeddingModelDefinition model, EmbeddingInput input)
        {
            return _embeddingModelService.CreateRequest(model, input);
        }

        private ApiRetrievalRequest CreateRetrievalRequest(
            AerialCityRagFolderMapping mapping,
            EmbeddingModelDefinition? model,
            RetrievalMethod method,
            string query,
            int candidateLimit,
            float minScore)
        {
            if (method == RetrievalMethod.BM25)
            {
                return new ApiRetrievalRequest
                {
                    DatabasePath = mapping.DatabasePath,
                    Method = method,
                    TextQuery = query,
                    TopK = candidateLimit,
                    MinScore = minScore
                };
            }

            var template = CreateEmbeddingTemplate(model!, EmbeddingInput.FromText(query));
            return new ApiRetrievalRequest
            {
                ApiKey = template.ApiKey,
                BaseUrl = template.BaseUrl,
                ApiType = template.ApiType,
                Model = template.Model,
                Content = template.Content,
                Parameters = new Dictionary<string, object?>(template.Parameters, StringComparer.Ordinal),
                Dimensions = template.Dimensions,
                Normalize = template.Normalize,
                IncludeBinaryDataInTextProjection = template.IncludeBinaryDataInTextProjection,
                DatabasePath = mapping.DatabasePath,
                Method = method,
                TextQuery = query,
                TopK = candidateLimit,
                MinScore = minScore
            };
        }

        private static async Task<int> EmbedAndInsertCodeFileAsync(
            AerialCity.Delegates.EmbedCodeFileDelegate embedCodeFile,
            AerialCity.Delegates.EmbedTextFileDelegate embedTextFile,
            AerialCity.Delegates.InsertSegmentDelegate insert,
            AerialDatabase database,
            ApiEmbeddingRequest template,
            string filePath,
            string targetDirectory,
            Encoding? encoding,
            int maxInputTokens,
            AerialCityRagInitializationStatistics statistics,
            CancellationToken cancellationToken)
        {
            try
            {
                var request = new ApiCodeFileEmbeddingRequest
                {
                    ApiKey = template.ApiKey,
                    BaseUrl = template.BaseUrl,
                    ApiType = template.ApiType,
                    Model = template.Model,
                    FilePath = filePath,
                    SourceUri = filePath,
                    Language = ResolveLanguageHint(filePath),
                    FileEncoding = encoding,
                    MaxInputTokens = maxInputTokens > 0 ? maxInputTokens : 8192,
                    Parameters = new Dictionary<string, object?>(template.Parameters, StringComparer.Ordinal),
                    Dimensions = template.Dimensions,
                    Normalize = template.Normalize,
                    IncludeBinaryDataInTextProjection = template.IncludeBinaryDataInTextProjection,
                    Metadata = CreateFileMetadata(filePath, targetDirectory, "code")
                };

                var results = await embedCodeFile(request, cancellationToken).ConfigureAwait(false);
                return await InsertEmbeddingResultsAsync(insert, database, results, filePath, targetDirectory, "code", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or SegmentationException)
            {
                statistics.CodeFilesFellBackToText++;
                return await EmbedAndInsertTextFileAsync(
                    embedTextFile,
                    insert,
                    database,
                    template,
                    filePath,
                    targetDirectory,
                    encoding,
                    maxInputTokens,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<int> EmbedAndInsertTextFileAsync(
            AerialCity.Delegates.EmbedTextFileDelegate embedTextFile,
            AerialCity.Delegates.InsertSegmentDelegate insert,
            AerialDatabase database,
            ApiEmbeddingRequest template,
            string filePath,
            string targetDirectory,
            Encoding? encoding,
            int maxInputTokens,
            CancellationToken cancellationToken)
        {
            var request = new ApiTextFileEmbeddingRequest
            {
                ApiKey = template.ApiKey,
                BaseUrl = template.BaseUrl,
                ApiType = template.ApiType,
                Model = template.Model,
                FilePath = filePath,
                SourceUri = filePath,
                FileEncoding = encoding,
                MaxInputTokens = maxInputTokens > 0 ? maxInputTokens : 8192,
                Parameters = new Dictionary<string, object?>(template.Parameters, StringComparer.Ordinal),
                Dimensions = template.Dimensions,
                Normalize = template.Normalize,
                IncludeBinaryDataInTextProjection = template.IncludeBinaryDataInTextProjection,
                Metadata = CreateFileMetadata(filePath, targetDirectory, "text")
            };

            var results = await embedTextFile(request, cancellationToken).ConfigureAwait(false);
            return await InsertEmbeddingResultsAsync(insert, database, results, filePath, targetDirectory, "text", cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> EmbedAndInsertImageAsync(
            AerialCity.Delegates.EmbedContentDelegate embedContent,
            AerialCity.Delegates.InsertSegmentDelegate insert,
            AerialDatabase database,
            ApiEmbeddingRequest template,
            string filePath,
            string targetDirectory,
            CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var segment = new Segment(SegmentKind.Image, $"Image file: {Path.GetFileName(filePath)}\nPath: {filePath}")
            {
                BinaryContent = bytes,
                SourceUri = filePath,
                Metadata =
                {
                    ["sourceKind"] = "Image",
                    ["path"] = filePath,
                    ["relativePath"] = GetRelativePath(targetDirectory, filePath),
                    ["fileName"] = Path.GetFileName(filePath),
                    ["mimeType"] = ResolveImageMimeType(filePath)
                }
            };

            var request = new ApiEmbeddingRequest
            {
                ApiKey = template.ApiKey,
                BaseUrl = template.BaseUrl,
                ApiType = template.ApiType,
                Model = template.Model,
                Segment = segment,
                Parameters = new Dictionary<string, object?>(template.Parameters, StringComparer.Ordinal),
                Dimensions = template.Dimensions,
                Normalize = template.Normalize,
                IncludeBinaryDataInTextProjection = template.IncludeBinaryDataInTextProjection
            };

            await embedContent(request, cancellationToken).ConfigureAwait(false);
            await insert(database, segment, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        private static async Task<int> InsertEmbeddingResultsAsync(
            AerialCity.Delegates.InsertSegmentDelegate insert,
            AerialDatabase database,
            IReadOnlyList<EmbeddingResult> results,
            string filePath,
            string targetDirectory,
            string sourceType,
            CancellationToken cancellationToken)
        {
            var inserted = 0;
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Segment == null)
                {
                    continue;
                }

                EnrichSegment(result.Segment, filePath, targetDirectory, sourceType);
                await insert(database, result.Segment, cancellationToken).ConfigureAwait(false);
                inserted++;
            }

            return inserted;
        }

        private static void EnrichSegment(Segment segment, string filePath, string targetDirectory, string sourceType)
        {
            segment.Metadata["sourceContent"] = segment.Content;
            segment.Metadata["path"] = filePath;
            segment.Metadata["relativePath"] = GetRelativePath(targetDirectory, filePath);
            segment.Metadata["fileName"] = Path.GetFileName(filePath);
            segment.Metadata["ragSourceType"] = sourceType;
        }

        private static Dictionary<string, object> CreateFileMetadata(
            string filePath,
            string targetDirectory,
            string sourceType)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = filePath,
                ["relativePath"] = GetRelativePath(targetDirectory, filePath),
                ["fileName"] = Path.GetFileName(filePath),
                ["ragSourceType"] = sourceType
            };
        }

        private static async Task<HashSet<string>> LoadIndexedSourceUrisAsync(string databasePath, CancellationToken cancellationToken)
        {
            var indexedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var segmentsPath = Path.Combine(databasePath, "segments");
            if (!Directory.Exists(segmentsPath))
            {
                return indexedSources;
            }

            foreach (var segmentFilePath in Directory.EnumerateFiles(segmentsPath, "*.seg"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = File.OpenRead(segmentFilePath);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (document.RootElement.TryGetProperty("sourceUri", out var sourceUriElement) &&
                        sourceUriElement.ValueKind == JsonValueKind.String)
                    {
                        var sourceUri = sourceUriElement.GetString();
                        if (!string.IsNullOrWhiteSpace(sourceUri) && TryNormalizePath(sourceUri, out var normalizedSourceUri))
                        {
                            indexedSources.Add(normalizedSourceUri);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
                {
                }
            }

            return indexedSources;
        }

        private static IEnumerable<string> EnumerateFiles(string rootDirectory, CancellationToken cancellationToken)
        {
            var pending = new Stack<string>();
            pending.Push(rootDirectory);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryNormalizePath(file, out var normalizedFile))
                    {
                        yield return normalizedFile;
                    }
                }

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(directory)
                        .OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    continue;
                }

                foreach (var childDirectory in childDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        continue;
                    }

                    pending.Push(childDirectory);
                }
            }
        }

        private static async Task<FileProbe> ProbeFileAsync(string filePath, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, useAsync: true);
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (TryDetectUtfEncoding(buffer, bytesRead, out var encoding))
            {
                return new FileProbe(false, encoding);
            }

            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] == 0)
                {
                    return new FileProbe(true, null);
                }
            }

            return new FileProbe(false, null);
        }

        private static bool TryDetectUtfEncoding(byte[] sample, int sampleLength, out Encoding? encoding)
        {
            encoding = null;
            if (sampleLength >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                encoding = Encoding.UTF8;
                return true;
            }

            if (sampleLength >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                return true;
            }

            if (sampleLength >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                return true;
            }

            var evenZeros = 0;
            var oddZeros = 0;
            var pairs = sampleLength / 2;
            if (pairs < 8)
            {
                return false;
            }

            for (var index = 0; index + 1 < sampleLength; index += 2)
            {
                if (sample[index] == 0 && sample[index + 1] != 0)
                {
                    evenZeros++;
                }
                else if (sample[index] != 0 && sample[index + 1] == 0)
                {
                    oddZeros++;
                }
            }

            if (oddZeros >= pairs * 0.6 && evenZeros <= pairs * 0.1)
            {
                encoding = Encoding.Unicode;
                return true;
            }

            if (evenZeros >= pairs * 0.6 && oddZeros <= pairs * 0.1)
            {
                encoding = Encoding.BigEndianUnicode;
                return true;
            }

            return false;
        }

        private static bool IsFileTooLarge(string filePath, int maximumFileBytes)
        {
            try
            {
                return new FileInfo(filePath).Length > maximumFileBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        }

        private static bool IsResultInsideSearchPath(RetrievalResult result, string searchPath)
        {
            var sourcePath = ResolveResultSourcePath(result);
            if (string.IsNullOrWhiteSpace(sourcePath) || !TryNormalizePath(sourcePath, out var normalizedSourcePath))
            {
                return false;
            }

            if (File.Exists(searchPath))
            {
                return string.Equals(normalizedSourcePath, searchPath, StringComparison.OrdinalIgnoreCase);
            }

            return AerialCityRagRegistry.IsSubPathOrEqual(searchPath, normalizedSourcePath);
        }

        private static string? ResolveResultSourcePath(RetrievalResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.Segment.SourceUri))
            {
                return result.Segment.SourceUri;
            }

            foreach (var key in new[] { "path", "Path", "filePath", "FilePath" })
            {
                if (result.Segment.Metadata.TryGetValue(key, out var value))
                {
                    var text = value?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }

        private static string ResolveExistingDirectory(string requestedDirectory, string? workspacePath)
        {
            var resolvedPath = ResolvePath(requestedDirectory, workspacePath);
            if (!Directory.Exists(resolvedPath))
            {
                throw new DirectoryNotFoundException($"Target directory not found: {resolvedPath}");
            }

            return AerialCityRagRegistry.NormalizePath(resolvedPath);
        }

        private static string ResolveExistingPath(string requestedPath, string? workspacePath)
        {
            var resolvedPath = ResolvePath(requestedPath, workspacePath);
            if (!Directory.Exists(resolvedPath) && !File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Search path not found: {resolvedPath}", resolvedPath);
            }

            return AerialCityRagRegistry.NormalizePath(resolvedPath);
        }

        private static string ResolvePath(string requestedPath, string? workspacePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
            var trimmedPath = requestedPath.Trim();
            if (Path.IsPathRooted(trimmedPath))
            {
                return Path.GetFullPath(trimmedPath);
            }

            var basePath = string.IsNullOrWhiteSpace(workspacePath)
                ? Environment.CurrentDirectory
                : workspacePath.Trim();
            return Path.GetFullPath(Path.Combine(basePath, trimmedPath));
        }

        private static bool TryNormalizePath(string path, out string normalizedPath)
        {
            try
            {
                normalizedPath = AerialCityRagRegistry.NormalizePath(path);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                normalizedPath = string.Empty;
                return false;
            }
        }

        private static string GetRelativePath(string rootPath, string filePath)
        {
            try
            {
                return Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return filePath;
            }
        }

        private static string? ResolveLanguageHint(string filePath)
        {
            var extension = Path.GetExtension(filePath).TrimStart('.');
            return string.IsNullOrWhiteSpace(extension) ? null : extension;
        }

        private static string ResolveImageMimeType(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".tif" or ".tiff" => "image/tiff",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }

        public static string FormatInitializationResult(AerialCityRagInitializationResult result)
        {
            var stats = result.Statistics;
            var builder = new StringBuilder();
            builder.AppendLine("AerialCity RAG initialized.");
            builder.AppendLine($"TargetDirectory: {result.TargetDirectory}");
            builder.AppendLine($"AerialCityDirectory: {result.AerialCityDirectory}");
            builder.AppendLine($"RegistryFile: {result.RegistryFilePath}");
            builder.AppendLine($"DatabasePath: {result.DatabasePath}");
            builder.AppendLine($"EmbeddingModel: {result.EmbeddingModelDisplayName} ({result.EmbeddingModelId})");
            builder.AppendLine($"SupportsMultimodalEmbedding: {result.SupportsMultimodalEmbedding}");
            builder.AppendLine();
            builder.AppendLine($"FilesVisited: {stats.FilesVisited}");
            builder.AppendLine($"CodeFilesEmbedded: {stats.CodeFilesEmbedded}");
            builder.AppendLine($"TextFilesEmbedded: {stats.TextFilesEmbedded}");
            builder.AppendLine($"ImageFilesEmbedded: {stats.ImageFilesEmbedded}");
            builder.AppendLine($"SegmentsInserted: {stats.SegmentsInserted}");
            builder.AppendLine($"AlreadyIndexedSkipped: {stats.FilesSkippedAlreadyIndexed}");
            builder.AppendLine($"BinaryFilesSkipped: {stats.BinaryFilesSkipped}");
            builder.AppendLine($"TooLargeSkipped: {stats.FilesSkippedTooLarge}");
            builder.AppendLine($"FailedFiles: {stats.FilesFailed}");
            builder.AppendLine($"CodeFallbackToText: {stats.CodeFilesFellBackToText}");

            if (stats.Failures.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Failures:");
                foreach (var failure in stats.Failures)
                {
                    builder.AppendLine($"- {failure}");
                }
            }

            return builder.ToString().TrimEnd();
        }

        public static string FormatSearchResult(AerialCityRagSearchResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{result.Method} search completed.");
            builder.AppendLine($"SearchPath: {result.SearchPath}");
            builder.AppendLine($"InitializedRoot: {result.TargetDirectory}");
            builder.AppendLine($"DatabasePath: {result.DatabasePath}");
            builder.AppendLine($"Query: {result.Query}");
            builder.AppendLine($"Results: {result.Results.Count}");
            builder.AppendLine();

            if (result.Results.Count == 0)
            {
                builder.AppendLine("No matching AerialCity segments found.");
                return builder.ToString().TrimEnd();
            }

            for (var index = 0; index < result.Results.Count; index++)
            {
                var item = result.Results[index];
                var path = ResolveResultSourcePath(item) ?? "(unknown source)";
                builder.AppendLine($"{index + 1}. Score: {item.Score.ToString("0.####", CultureInfo.InvariantCulture)}");
                builder.AppendLine($"Path: {path}");
                builder.AppendLine($"Kind: {item.Segment.Kind}");
                if (item.Segment.EndOffset > item.Segment.StartOffset)
                {
                    builder.AppendLine($"Offsets: {item.Segment.StartOffset}-{item.Segment.EndOffset}");
                }

                builder.AppendLine("Content:");
                builder.AppendLine(TrimContent(item.Segment.Content));
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static string TrimContent(string content)
        {
            const int maximumLength = 1600;
            var text = content.Trim();
            return text.Length <= maximumLength ? text : $"{text[..maximumLength]}...";
        }

        private sealed record FileProbe(bool IsBinary, Encoding? Encoding);
    }

    public sealed class AerialCityRagInitializationResult
    {
        public required string TargetDirectory { get; init; }

        public required string AerialCityDirectory { get; init; }

        public required string RegistryFilePath { get; init; }

        public required string DatabasePath { get; init; }

        public required string DatabaseFolderName { get; init; }

        public required string EmbeddingModelDisplayName { get; init; }

        public required string EmbeddingModelId { get; init; }

        public bool SupportsMultimodalEmbedding { get; init; }

        public required AerialCityRagInitializationStatistics Statistics { get; init; }
    }

    public sealed class AerialCityRagInitializationStatistics
    {
        public int FilesVisited { get; set; }

        public int CodeFilesEmbedded { get; set; }

        public int TextFilesEmbedded { get; set; }

        public int ImageFilesEmbedded { get; set; }

        public int SegmentsInserted { get; set; }

        public int FilesSkippedAlreadyIndexed { get; set; }

        public int BinaryFilesSkipped { get; set; }

        public int FilesSkippedTooLarge { get; set; }

        public int FilesFailed { get; set; }

        public int CodeFilesFellBackToText { get; set; }

        public List<string> Failures { get; } = [];
    }

    public sealed class AerialCityRagSearchResult
    {
        public required string SearchPath { get; init; }

        public required string TargetDirectory { get; init; }

        public required string DatabasePath { get; init; }

        public required RetrievalMethod Method { get; init; }

        public required string Query { get; init; }

        public int TopK { get; init; }

        public required IReadOnlyList<RetrievalResult> Results { get; init; }
    }
}
