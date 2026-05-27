using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Models;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Services.ChatSession;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Services.AgentLoop
{
    public sealed class AgentLoopService
    {
        private const int MaxIterations = 64;
        private const int StreamingTraceRawContentTailLength = 256;
        private const string ToolParseErrorName = "_tool_parse_error";
        private const string SyncToolTagName = "Tool";
        private const string AsyncToolTagName = "ToolAsync";
        private static readonly TimeSpan StreamingTokenUsageRefreshInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan StreamingProtocolRefreshInterval = TimeSpan.FromMilliseconds(16);
        private const double MinCompactionTriggerRatio = 0.8d;
        private const string MinCompactionLayerKey = "MinCompaction";
        private static readonly AsyncToolRegistry s_asyncToolRegistry = new();

        private sealed record ToolExecutionAuthorization(
            bool CanExecute,
            bool HasHostConfirmation,
            string? ErrorMessage);

        private sealed record PendingAsyncToolExecution(
            string ToolCallId,
            int PartIndex,
            int ToolCallIndex,
            SkyweaverToolInvocation Invocation,
            Task<SkyweaverToolResult> ExecutionTask,
            AsyncToolProgressTracker ProgressTracker);

        private sealed record AsyncToolProgressSnapshot(
            string ToolCallId,
            string ToolName,
            bool IsPending,
            bool IsCompleted,
            int Version,
            DateTimeOffset? ObservedAtUtc,
            SkyweaverToolProgressUpdate? Progress);

        private sealed class AsyncToolProgressTracker
        {
            private readonly object _syncRoot = new();
            private int _version;
            private DateTimeOffset? _observedAtUtc;
            private SkyweaverToolProgressUpdate? _latestProgress;

            public void Update(SkyweaverToolProgressUpdate progress)
            {
                ArgumentNullException.ThrowIfNull(progress);

                lock (_syncRoot)
                {
                    _latestProgress = progress.Normalize();
                    _observedAtUtc = DateTimeOffset.UtcNow;
                    _version++;
                }
            }

            public AsyncToolProgressSnapshot CreateSnapshot(
                string toolCallId,
                string toolName,
                bool isPending,
                bool isCompleted)
            {
                lock (_syncRoot)
                {
                    return new AsyncToolProgressSnapshot(
                        toolCallId,
                        toolName,
                        isPending,
                        isCompleted,
                        _version,
                        _observedAtUtc,
                        _latestProgress);
                }
            }
        }

        private sealed class AsyncToolPublicationGate : IDisposable
        {
            private int _isOpen = 1;

            public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

            public void Dispose()
            {
                Volatile.Write(ref _isOpen, 0);
            }
        }

        private sealed record AsyncToolCompletionRecord(
            PendingAsyncToolExecution Execution,
            SkyweaverToolReturnPayload Payload,
            AsyncToolProgressSnapshot ProgressSnapshot);

        private sealed record PersistedAsyncToolBackfill(
            string ToolCallId,
            AgentToolBackfill Backfill,
            SkyweaverToolInvocation? Invocation);

        private sealed class AsyncToolRegistryEntry
        {
            public AsyncToolRegistryEntry(PendingAsyncToolExecution execution)
            {
                Execution = execution ?? throw new ArgumentNullException(nameof(execution));
            }

            public PendingAsyncToolExecution Execution { get; private set; }

            public SkyweaverToolReturnPayload? CompletedPayload { get; private set; }

            public AsyncToolProgressSnapshot? CompletedProgressSnapshot { get; private set; }

            public bool IsDeliveredToConversation { get; private set; }

            public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

            public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

            public void UpdateExecution(PendingAsyncToolExecution execution)
            {
                Execution = execution ?? throw new ArgumentNullException(nameof(execution));
                UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            public void MarkCompleted(
                SkyweaverToolReturnPayload payload,
                AsyncToolProgressSnapshot progressSnapshot,
                bool deliveredToConversation)
            {
                CompletedPayload = payload ?? throw new ArgumentNullException(nameof(payload));
                CompletedProgressSnapshot = progressSnapshot ?? throw new ArgumentNullException(nameof(progressSnapshot));
                IsDeliveredToConversation |= deliveredToConversation;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            public void MarkDelivered()
            {
                IsDeliveredToConversation = true;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        private sealed class AsyncToolRegistry
        {
            private readonly object _syncRoot = new();
            private readonly Dictionary<string, Dictionary<string, AsyncToolRegistryEntry>> _entriesByScope =
                new(StringComparer.OrdinalIgnoreCase);

            public void Track(string? scopeKey, PendingAsyncToolExecution execution)
            {
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return;
                }

                lock (_syncRoot)
                {
                    var scopeEntries = GetOrCreateScope(normalizedScopeKey);
                    if (scopeEntries.TryGetValue(execution.ToolCallId, out var entry))
                    {
                        entry.UpdateExecution(execution);
                        return;
                    }

                    scopeEntries[execution.ToolCallId] = new AsyncToolRegistryEntry(execution);
                }
            }

            public void MarkCompleted(
                string? scopeKey,
                PendingAsyncToolExecution execution,
                SkyweaverToolReturnPayload payload,
                AsyncToolProgressSnapshot progressSnapshot,
                bool deliveredToConversation)
            {
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return;
                }

                lock (_syncRoot)
                {
                    var scopeEntries = GetOrCreateScope(normalizedScopeKey);
                    if (!scopeEntries.TryGetValue(execution.ToolCallId, out var entry))
                    {
                        entry = new AsyncToolRegistryEntry(execution);
                        scopeEntries[execution.ToolCallId] = entry;
                    }

                    entry.MarkCompleted(payload, progressSnapshot, deliveredToConversation);
                }
            }

            public void MarkDelivered(string? scopeKey, string? toolCallId)
            {
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return;
                }

                var normalizedToolCallId = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
                if (normalizedToolCallId.Length == 0)
                {
                    return;
                }

                lock (_syncRoot)
                {
                    if (_entriesByScope.TryGetValue(normalizedScopeKey, out var scopeEntries) &&
                        scopeEntries.TryGetValue(normalizedToolCallId, out var entry))
                    {
                        entry.MarkDelivered();
                    }
                }
            }

            public IReadOnlyList<AsyncToolCompletionRecord> ConsumeUndeliveredCompleted(
                string? scopeKey,
                IEnumerable<string>? excludedToolCallIds = null)
            {
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return Array.Empty<AsyncToolCompletionRecord>();
                }

                var excludedIds = excludedToolCallIds == null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : excludedToolCallIds
                        .Select(ChatSessionToolCallIdGenerator.Normalize)
                        .Where(id => id.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                lock (_syncRoot)
                {
                    if (!_entriesByScope.TryGetValue(normalizedScopeKey, out var scopeEntries))
                    {
                        return Array.Empty<AsyncToolCompletionRecord>();
                    }

                    var records = scopeEntries.Values
                        .Where(entry => entry.CompletedPayload != null &&
                                        entry.CompletedProgressSnapshot != null &&
                                        !entry.IsDeliveredToConversation &&
                                        !excludedIds.Contains(entry.Execution.ToolCallId))
                        .OrderBy(entry => entry.CreatedAtUtc)
                        .Select(entry =>
                        {
                            entry.MarkDelivered();
                            return new AsyncToolCompletionRecord(
                                entry.Execution,
                                entry.CompletedPayload!,
                                entry.CompletedProgressSnapshot!);
                        })
                        .ToArray();

                    return records;
                }
            }

            public bool TryGetExecution(
                string? scopeKey,
                string? toolCallId,
                out PendingAsyncToolExecution execution)
            {
                execution = null!;
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return false;
                }

                var normalizedToolCallId = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
                if (normalizedToolCallId.Length == 0)
                {
                    return false;
                }

                lock (_syncRoot)
                {
                    if (!_entriesByScope.TryGetValue(normalizedScopeKey, out var scopeEntries) ||
                        !scopeEntries.TryGetValue(normalizedToolCallId, out var entry))
                    {
                        return false;
                    }

                    execution = entry.Execution;
                    return true;
                }
            }

            public bool TryGetCompleted(
                string? scopeKey,
                string? toolCallId,
                out SkyweaverToolReturnPayload payload,
                out AsyncToolProgressSnapshot progressSnapshot)
            {
                payload = null!;
                progressSnapshot = null!;
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return false;
                }

                var normalizedToolCallId = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
                if (normalizedToolCallId.Length == 0)
                {
                    return false;
                }

                lock (_syncRoot)
                {
                    if (!_entriesByScope.TryGetValue(normalizedScopeKey, out var scopeEntries) ||
                        !scopeEntries.TryGetValue(normalizedToolCallId, out var entry) ||
                        entry.CompletedPayload == null ||
                        entry.CompletedProgressSnapshot == null)
                    {
                        return false;
                    }

                    payload = entry.CompletedPayload;
                    progressSnapshot = entry.CompletedProgressSnapshot;
                    return true;
                }
            }

            public IReadOnlyList<string> GetKnownToolCallIds(string? scopeKey)
            {
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return Array.Empty<string>();
                }

                lock (_syncRoot)
                {
                    return _entriesByScope.TryGetValue(normalizedScopeKey, out var scopeEntries)
                        ? scopeEntries.Keys
                            .Select(ChatSessionToolCallIdGenerator.Normalize)
                            .Where(id => id.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(id => GetToolCallIdOrdinal(id))
                            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : Array.Empty<string>();
                }
            }

            public IReadOnlyList<PendingAsyncToolExecution> GetExecutions(string? scopeKey)
            {
                if (!TryNormalizeScopeKey(scopeKey, out var normalizedScopeKey))
                {
                    return Array.Empty<PendingAsyncToolExecution>();
                }

                lock (_syncRoot)
                {
                    return _entriesByScope.TryGetValue(normalizedScopeKey, out var scopeEntries)
                        ? scopeEntries.Values
                            .OrderBy(entry => entry.CreatedAtUtc)
                            .Select(entry => entry.Execution)
                            .ToArray()
                        : Array.Empty<PendingAsyncToolExecution>();
                }
            }

            private Dictionary<string, AsyncToolRegistryEntry> GetOrCreateScope(string scopeKey)
            {
                if (!_entriesByScope.TryGetValue(scopeKey, out var scopeEntries))
                {
                    scopeEntries = new Dictionary<string, AsyncToolRegistryEntry>(StringComparer.OrdinalIgnoreCase);
                    _entriesByScope[scopeKey] = scopeEntries;
                }

                return scopeEntries;
            }

            private static bool TryNormalizeScopeKey(string? scopeKey, out string normalizedScopeKey)
            {
                normalizedScopeKey = string.IsNullOrWhiteSpace(scopeKey)
                    ? string.Empty
                    : scopeKey.Trim();
                return normalizedScopeKey.Length > 0;
            }
        }

        private sealed record AsyncToolFlushResult(
            IReadOnlyList<AgentToolBackfill> ToolBackfills,
            IReadOnlyList<string> NewlyLoadedToolKitKeys);

        private sealed class MinCompactionAttemptState
        {
            public string? LastAttemptHash { get; set; }
        }

        private sealed class TransientToolCallIdFactory
        {
            private readonly HashSet<string> _reservedIds = new(StringComparer.OrdinalIgnoreCase);
            private int _nextId;

            public void Reserve(string? toolCallId)
            {
                var normalized = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
                if (normalized.Length > 0)
                {
                    _reservedIds.Add(normalized);
                }
            }

            public string Create()
            {
                while (_nextId < int.MaxValue)
                {
                    var candidate = $"TC{++_nextId}";
                    if (_reservedIds.Add(candidate))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException("Unable to allocate a unique transient tool call id.");
            }
        }

        private Func<string> CreateDefaultToolCallIdFactory(AgentLoopRequest request)
        {
            var factory = new TransientToolCallIdFactory();
            foreach (var toolCallId in CollectKnownToolCallIds(request))
            {
                factory.Reserve(toolCallId);
            }

            return factory.Create;
        }

        private sealed record StreamedResponseResult(
            int AttemptNumber,
            string? ModelId,
            AgentAssistantResponse AssistantResponse,
            IReadOnlyList<AgentToolBackfill> ToolBackfills,
            AgentLoopFinalOutput? FinalOutput,
            AgentLoopFinalOutput? LatestPassdownOutput,
            IReadOnlyList<string> NewlyLoadedToolKitKeys);

        private sealed class AssistantVisibleTextStreamingTracker
        {
            private readonly AgentLoopOutputKind _outputKind;
            private int _emittedLength;

            public AssistantVisibleTextStreamingTracker(AgentLoopOutputKind outputKind)
            {
                _outputKind = outputKind;
            }

            public AgentLoopOutputKind OutputKind => _outputKind;

            public string ExtractDelta(string rawContent, bool isFinal)
            {
                var visibleContent = ExtractVisibleAssistantText(rawContent, isFinal);
                if (visibleContent.Length <= _emittedLength)
                {
                    return string.Empty;
                }

                var delta = visibleContent[_emittedLength..];
                _emittedLength = visibleContent.Length;
                return delta;
            }
        }

        private sealed record AssistantPresentationDelta(
            bool IsReasoning,
            string Content,
            AgentLoopOutputKind? TextOutputKind,
            int? PartIndex,
            bool IsReasoningCollapsible);

        private sealed record GemmaThoughtSegment(int SegmentIndex, string Content);

        private sealed record GemmaThoughtExtraction(
            string ProtocolContent,
            IReadOnlyList<GemmaThoughtSegment> ThoughtSegments)
        {
            public static GemmaThoughtExtraction Empty { get; } =
                new(string.Empty, Array.Empty<GemmaThoughtSegment>());
        }

        private sealed class AssistantPresentationStreamingTracker
        {
            private readonly AssistantVisibleTextStreamingTracker _fallbackVisibleTextTracker;
            private readonly AssistantVisibleTextStreamingTracker _gemmaVisibleTextTracker;
            private readonly bool _enableGemmaThoughtCompatibility;
            private readonly Dictionary<int, int> _emittedGemmaThoughtLengths = new();
            private bool? _isGemmaThoughtContent;

            public AssistantPresentationStreamingTracker(
                AgentLoopOutputKind outputKind,
                bool enableGemmaThoughtCompatibility)
            {
                _fallbackVisibleTextTracker = new AssistantVisibleTextStreamingTracker(outputKind);
                _gemmaVisibleTextTracker = new AssistantVisibleTextStreamingTracker(outputKind);
                _enableGemmaThoughtCompatibility = enableGemmaThoughtCompatibility;
            }

            public AgentLoopOutputKind OutputKind => _fallbackVisibleTextTracker.OutputKind;

            public IReadOnlyList<AssistantPresentationDelta> ExtractDeltas(string rawContent, bool isFinal)
            {
                if (!_enableGemmaThoughtCompatibility)
                {
                    return ExtractFallbackDeltas(rawContent, isFinal);
                }

                var isPendingGemmaDetection = false;
                if (_isGemmaThoughtContent != false &&
                    TryExtractGemmaThoughtContent(
                        rawContent,
                        isFinal,
                        out var gemmaThought,
                        out isPendingGemmaDetection))
                {
                    _isGemmaThoughtContent = true;
                    return ExtractGemmaDeltas(gemmaThought, isFinal);
                }

                if (_isGemmaThoughtContent == true)
                {
                    return ExtractGemmaDeltas(GemmaThoughtExtraction.Empty, isFinal);
                }

                if (isPendingGemmaDetection)
                {
                    return Array.Empty<AssistantPresentationDelta>();
                }

                _isGemmaThoughtContent = false;
                return ExtractFallbackDeltas(rawContent, isFinal);
            }

            private IReadOnlyList<AssistantPresentationDelta> ExtractFallbackDeltas(string rawContent, bool isFinal)
            {
                var delta = _fallbackVisibleTextTracker.ExtractDelta(rawContent, isFinal);
                return string.IsNullOrEmpty(delta)
                    ? Array.Empty<AssistantPresentationDelta>()
                    : new[]
                    {
                        new AssistantPresentationDelta(
                            false,
                            delta,
                            _fallbackVisibleTextTracker.OutputKind,
                            null,
                            true)
                    };
            }

            private IReadOnlyList<AssistantPresentationDelta> ExtractGemmaDeltas(
                GemmaThoughtExtraction extraction,
                bool isFinal)
            {
                var deltas = new List<AssistantPresentationDelta>();

                foreach (var segment in extraction.ThoughtSegments.OrderBy(segment => segment.SegmentIndex))
                {
                    if (string.IsNullOrEmpty(segment.Content))
                    {
                        continue;
                    }

                    _emittedGemmaThoughtLengths.TryGetValue(segment.SegmentIndex, out var emittedLength);
                    if (segment.Content.Length <= emittedLength)
                    {
                        continue;
                    }

                    var delta = segment.Content[emittedLength..];
                    _emittedGemmaThoughtLengths[segment.SegmentIndex] = segment.Content.Length;
                    deltas.Add(new AssistantPresentationDelta(
                        true,
                        delta,
                        null,
                        segment.SegmentIndex,
                        false));
                }

                var visibleDelta = _gemmaVisibleTextTracker.ExtractDelta(extraction.ProtocolContent, isFinal);
                if (!string.IsNullOrEmpty(visibleDelta))
                {
                    deltas.Add(new AssistantPresentationDelta(
                        false,
                        visibleDelta,
                        _gemmaVisibleTextTracker.OutputKind,
                        null,
                        true));
                }

                return deltas;
            }
        }

        private readonly AgentSystemPromptBuilder _systemPromptBuilder;
        private readonly IAgentLanguageModelResolver _languageModelResolver;
        private readonly ILanguageModelChatService _chatService;
        private readonly SkyweaverToolManager _toolManager;
        private readonly AgentLoopContextManager _contextManager;
        private readonly SkyweaverToolKitService _toolKitService;
        private readonly AgentLoopCompactionStore _compactionStore;
        private readonly AgentLoopTokenCounter _tokenCounter;
        private readonly ChatSessionToolCallResourceStore _toolCallResourceStore;

        public AgentLoopService()
            : this(
                new AgentSystemPromptBuilder(),
                new AgentLanguageModelResolver(),
                new LanguageModelChatService(),
                new SkyweaverToolManager(),
                new AgentLoopContextManager(),
                new SkyweaverToolKitService())
        {
        }

        public AgentLoopService(
            AgentSystemPromptBuilder systemPromptBuilder,
            IAgentLanguageModelResolver languageModelResolver,
            ILanguageModelChatService chatService,
            SkyweaverToolManager toolManager,
            AgentLoopContextManager contextManager,
            SkyweaverToolKitService? toolKitService = null,
            AgentLoopCompactionStore? compactionStore = null,
            AgentLoopTokenCounter? tokenCounter = null,
            ChatSessionToolCallResourceStore? toolCallResourceStore = null)
        {
            _systemPromptBuilder = systemPromptBuilder ?? throw new ArgumentNullException(nameof(systemPromptBuilder));
            _languageModelResolver = languageModelResolver ?? throw new ArgumentNullException(nameof(languageModelResolver));
            _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
            _toolManager = toolManager ?? throw new ArgumentNullException(nameof(toolManager));
            _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
            _toolKitService = toolKitService ?? new SkyweaverToolKitService();
            _compactionStore = compactionStore ?? new AgentLoopCompactionStore();
            _tokenCounter = tokenCounter ?? new AgentLoopTokenCounter(_chatService, _compactionStore);
            _toolCallResourceStore = toolCallResourceStore ?? new ChatSessionToolCallResourceStore();
        }

        public Task<AgentLoopResult> RunAsync(
            AgentLoopRequest request,
            CancellationToken cancellationToken = default)
        {
            return RunCoreAsync(request, onEventAsync: null, cancellationToken);
        }

        public Task<AgentLoopResult> RunStreamingAsync(
            AgentLoopRequest request,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask> onEventAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(onEventAsync);
            return RunCoreAsync(request, onEventAsync, cancellationToken);
        }

        private async Task<AgentLoopResult> RunCoreAsync(
            AgentLoopRequest request,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);

            using var asyncToolPublicationGate = new AsyncToolPublicationGate();
            var toolCallIdFactory = request.ToolCallIdFactory ?? CreateDefaultToolCallIdFactory(request);

            var availableToolKits = _toolKitService.Load();
            var toolKitMembershipMap = _toolKitService.BuildToolKitMembershipMap(availableToolKits);
            var activeToolKitKeys = new HashSet<string>(
                ExtractLoadedToolKitKeysFromHistory(request.History),
                StringComparer.OrdinalIgnoreCase);
            foreach (var toolKitKey in ExtractDefaultToolKitKeys(request.Agent))
            {
                activeToolKitKeys.Add(toolKitKey);
            }
            var runtimeToolContext = request.ToolContext
                .WithSubAgentMode(request.IsSubAgent)
                .WithRuntimeAgent(
                    request.Agent,
                    request.ToolConfirmationCallback != null)
                .WithAvailableToolKits(availableToolKits);
            var baseSystemPrompt = _systemPromptBuilder.BuildCompleteSystemPrompt(
                request.Agent,
                supportsHostToolConfirmation: request.ToolConfirmationCallback != null,
                availableToolKits: availableToolKits,
                activeToolKitKeys: activeToolKitKeys,
                isSubAgent: request.IsSubAgent);

            if (request.IsScheduledTaskSession)
            {
                baseSystemPrompt += "\n\n### 计划任务后台运行特别指令\n" +
                                    "- 代理现在正在后台的计划任务中操作。\n" +
                                    "- 用户提示词是系统自动注入的。\n" +
                                    "- 运行时自主性更强，请根据预设的计划直接执行任务，无需等待、也不得等待用户意见或进行交互确认。\n" +
                                    "- 如果评估该任务可能会伤害用户（例如删除重要系统文件、暴露敏感凭据）或损害用户财产，应直接拒绝执行该任务。";
            }

            var debugRunContext = AgentLoopDebugRecorder.TryCreateRunContext(request);
            AgentLoopDebugRecorder.RecordRunStart(debugRunContext, request, baseSystemPrompt);

            var persistentHistory = (request.History ?? Array.Empty<LanguageModelChatMessage>())
                .Select(message => message.Clone())
                .ToList();
            var turnHistory = new List<LanguageModelChatMessage>();
            var iterations = new List<AgentLoopIteration>();
            var pendingAsyncToolExecutions = new List<PendingAsyncToolExecution>();
            var completedAsyncToolResultsById = new Dictionary<string, SkyweaverToolReturnPayload>(StringComparer.OrdinalIgnoreCase);
            var completedAsyncToolProgressById = new Dictionary<string, AsyncToolProgressSnapshot>(StringComparer.OrdinalIgnoreCase);
            string? lastModelId = null;
            AgentLoopFinalOutput? latestPassdownOutput = null;
            var minCompactionAttemptState = new MinCompactionAttemptState();
            bool isMinCompacted = false;

            for (var iterationNumber = 1; iterationNumber <= MaxIterations; iterationNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var flushedAsyncTools = await FlushCompletedAsyncToolBackfillsAsync(
                    request,
                    pendingAsyncToolExecutions,
                    completedAsyncToolResultsById,
                    completedAsyncToolProgressById,
                    turnHistory,
                    iterationNumber,
                    onEventAsync,
                    cancellationToken).ConfigureAwait(false);

                foreach (var toolKitKey in flushedAsyncTools.NewlyLoadedToolKitKeys)
                {
                    activeToolKitKeys.Add(toolKitKey);
                }

                var systemPrompt = BuildSystemPromptWithRuntimeToolCallNotice(
                    baseSystemPrompt,
                    request);
                var preparedContext = await _contextManager.PrepareAsync(
                    request.Agent,
                    systemPrompt,
                    request.Input,
                    request.InputContentBlocks,
                    persistentHistory,
                    turnHistory,
                    request.CompactionFilePath,
                    debugRunContext,
                    iterationNumber,
                    ResolveSessionResourcesFolderPath(request),
                    cancellationToken).ConfigureAwait(false);

                persistentHistory = preparedContext.PersistentHistory
                    .Select(message => message.Clone())
                    .ToList();
                turnHistory = preparedContext.TurnHistory
                    .Select(message => message.Clone())
                    .ToList();

                var appliedMinCompaction = await TryApplyMinCompactionAsync(
                    request,
                    baseSystemPrompt,
                    preparedContext,
                    persistentHistory,
                    turnHistory,
                    minCompactionAttemptState,
                    cancellationToken).ConfigureAwait(false);

                if (appliedMinCompaction != null)
                {
                    isMinCompacted = true;
                    systemPrompt = BuildSystemPromptWithRuntimeToolCallNotice(
                        baseSystemPrompt,
                        request);
                    preparedContext = await _contextManager.PrepareAsync(
                        request.Agent,
                        systemPrompt,
                        request.Input,
                        request.InputContentBlocks,
                        persistentHistory,
                        turnHistory,
                        request.CompactionFilePath,
                        debugRunContext,
                        iterationNumber,
                        ResolveSessionResourcesFolderPath(request),
                        cancellationToken).ConfigureAwait(false);
                }

                if (request.MaxCompactionEnabled)
                {
                    var candidates = _languageModelResolver.GetCandidateModels(request.Agent)
                        .Where(model => model.InterfaceSettings.IsFullyConfigured)
                        .ToArray();
                    if (candidates.Length > 0)
                    {
                        var compactionModel = candidates
                            .OrderBy(model => model.EffectiveContextWindowTokens)
                            .First();
                        var contextWindowTokens = compactionModel.EffectiveContextWindowTokens;
                        
                        int currentTokenCount = 0;
                        try
                        {
                            var tokenCountResult = await _tokenCounter.CountAsync(
                                compactionModel,
                                preparedContext.PreparedMessages,
                                request.CompactionFilePath,
                                cancellationToken).ConfigureAwait(false);
                            currentTokenCount = tokenCountResult.TokenCount;
                        }
                        catch
                        {
                            currentTokenCount = 0;
                        }

                        if (isMinCompacted && currentTokenCount >= (int)(contextWindowTokens * 0.95d))
                        {
                            isMinCompacted = false;

                            var historyBuilder = new StringBuilder();
                            foreach (var msg in persistentHistory.Concat(turnHistory))
                            {
                                var roleName = msg.Role.ToString();
                                var authorStr = string.IsNullOrEmpty(msg.AuthorName) ? "" : $" (Author: {msg.AuthorName})";
                                historyBuilder.AppendLine($"=== Role: {roleName}{authorStr} ===");
                                foreach (var block in msg.ContentBlocks)
                                {
                                    if (block.Kind == LanguageModelChatContentBlockKind.Text || block.Kind == LanguageModelChatContentBlockKind.HostPreservedContent)
                                    {
                                        historyBuilder.AppendLine(block.Content);
                                    }
                                    else
                                    {
                                        historyBuilder.AppendLine($"[Binary/Media Data Kind: {block.Kind}, MediaType: {block.MediaType}, Size: {block.Data?.Length ?? 0} bytes]");
                                    }
                                }
                                historyBuilder.AppendLine();
                            }
                            var formattedHistory = historyBuilder.ToString();

                            var compressionPrompt = $@"你是一个上下文压缩助手。由于当前对话的上下文长度已经达到模型的极限，你需要对以下的历史对话进行深度压缩和总结（MaxCompaction），以便继续后续任务。

请严格遵守以下压缩要点：
1. 用户在整个过程中所有的指令（User messages / Instructions），必须尽可能原样保留。
2. 代理（Assistant）在整个过程中进行的所有操作、工具调用与返回结果，需要高度凝练地保留，仅保留核心步骤、主要发现和结果。
3. 必须详细说明：
   - 任务的详情与目的。
   - 任务当前的进行情况。
   - 中断点（即触发本次压缩之前最后进行的动作和当前状态）的详细信息。
4. 保留其他一切对继续任务所必要的关键上下文信息。
5. 你的输出内容需要尽可能详细、尽可能丰富（保留尽可能多的细节，不要过度省略关键信息，输出长度可以较长，保留更多内容）。

以下是待压缩的完整对话历史：
{formattedHistory}";

                            string compressedHistoryText;
                            try
                            {
                                compressedHistoryText = await _languageModelResolver.ExecuteCapabilityLayerWithFallbackAsync(
                                    CapabilityLayerBuiltIns.ContextCompressionLayerKey,
                                    async (compModel, ct) =>
                                    {
                                        var messages = new[]
                                        {
                                            new LanguageModelChatMessage(LanguageModelChatRole.User, compressionPrompt)
                                        };
                                        var response = await _chatService.GetResponseAsync(compModel, messages, ct).ConfigureAwait(false);
                                        return response.Text;
                                    },
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                compressedHistoryText = $"[MaxCompaction failed: {ex.Message}]\n\n" + formattedHistory;
                            }

                            persistentHistory = new List<LanguageModelChatMessage>
                            {
                                new LanguageModelChatMessage(
                                    LanguageModelChatRole.User,
                                    $"[系统已对历史上下文进行 MaxCompaction 压缩，以下是之前的任务摘要及所有指令原样保留的内容：]\n\n{compressedHistoryText}")
                                {
                                    AuthorName = "System"
                                }
                            };
                            turnHistory = new List<LanguageModelChatMessage>();

                            systemPrompt = BuildSystemPromptWithRuntimeToolCallNotice(
                                baseSystemPrompt,
                                request);
                            preparedContext = await _contextManager.PrepareAsync(
                                request.Agent,
                                systemPrompt,
                                request.Input,
                                request.InputContentBlocks,
                                persistentHistory,
                                turnHistory,
                                request.CompactionFilePath,
                                debugRunContext,
                                iterationNumber,
                                ResolveSessionResourcesFolderPath(request),
                                cancellationToken).ConfigureAwait(false);

                            persistentHistory = preparedContext.PersistentHistory
                                .Select(message => message.Clone())
                                .ToList();
                            turnHistory = preparedContext.TurnHistory
                                .Select(message => message.Clone())
                                .ToList();

                            int afterTokenCount = 0;
                            try
                            {
                                var tokenCountResult = await _tokenCounter.CountAsync(
                                    compactionModel,
                                    preparedContext.PreparedMessages,
                                    request.CompactionFilePath,
                                    cancellationToken).ConfigureAwait(false);
                                afterTokenCount = tokenCountResult.TokenCount;
                            }
                            catch
                            {
                                afterTokenCount = 0;
                            }

                            appliedMinCompaction = new AgentLoopContextCompressionInfo
                            {
                                ContextWindowTokens = contextWindowTokens,
                                EstimatedTokenCountBeforeCompression = currentTokenCount,
                                EstimatedTokenCountAfterCompression = afterTokenCount,
                                TargetTokenCountAfterCompression = (int)(contextWindowTokens * 0.95d),
                                CompressionLayerKey = "MaxCompaction",
                                CompressionModelId = compactionModel.SummaryModelId,
                                CompactedToolCallIds = Array.Empty<string>()
                            };
                        }
                    }
                }

                var preparedSnapshot = new AgentLoopPreparedRequestDebugSnapshot
                {
                    SystemPrompt = systemPrompt,
                    Input = request.Input,
                    PersistentHistory = preparedContext.PersistentHistory
                        .Select(message => message.Clone())
                        .ToArray(),
                    TurnHistory = preparedContext.TurnHistory
                        .Select(message => message.Clone())
                        .ToArray(),
                    PreparedMessages = preparedContext.PreparedMessages
                        .Select(message => message.Clone())
                        .ToArray(),
                    ContextCompression = appliedMinCompaction ?? preparedContext.ContextCompression
                };

                if (appliedMinCompaction != null || preparedContext.ContextCompression != null)
                {
                    await PublishAsync(
                        onEventAsync,
                        new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.ContextCompressionApplied,
                            IterationNumber = iterationNumber,
                            ContextCompression = appliedMinCompaction ?? preparedContext.ContextCompression
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                StreamedResponseResult response;
                try
                {
                    response = await InvokeModelStreamingAsync(
                        request,
                        toolCallIdFactory,
                        preparedSnapshot,
                        runtimeToolContext,
                        toolKitMembershipMap,
                        activeToolKitKeys,
                        pendingAsyncToolExecutions,
                        completedAsyncToolResultsById,
                        completedAsyncToolProgressById,
                        asyncToolPublicationGate,
                        debugRunContext,
                        iterationNumber,
                        latestPassdownOutput,
                        onEventAsync,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new AgentLoopResult
                    {
                        IsCompleted = false,
                        FailureReason = ex.Message,
                        LastModelId = lastModelId,
                        Iterations = iterations.ToArray()
                    };
                }

                if (!string.IsNullOrWhiteSpace(response.ModelId))
                {
                    lastModelId = response.ModelId;
                }

                if (response.LatestPassdownOutput != null)
                {
                    latestPassdownOutput = response.LatestPassdownOutput;
                }

                foreach (var toolKitKey in response.NewlyLoadedToolKitKeys)
                {
                    activeToolKitKeys.Add(toolKitKey);
                }

                var iterationToolBackfills = flushedAsyncTools.ToolBackfills
                    .Concat(response.ToolBackfills)
                    .ToArray();

                AppendCurrentTurnHistory(
                    response.AssistantResponse,
                    response.ToolBackfills,
                    turnHistory);

                AgentLoopDebugRecorder.RecordIterationOutcome(
                    debugRunContext,
                    request.Agent,
                    iterationNumber,
                    response.AttemptNumber,
                    response.ModelId,
                    response.AssistantResponse,
                    iterationToolBackfills,
                    response.FinalOutput);

                iterations.Add(new AgentLoopIteration
                {
                    IterationNumber = iterationNumber,
                    ModelId = response.ModelId,
                    AssistantResponse = response.AssistantResponse,
                    ToolBackfills = iterationToolBackfills,
                    FinalOutput = response.FinalOutput,
                    ContextCompression = appliedMinCompaction ?? preparedContext.ContextCompression
                });

                await PublishAsync(
                    onEventAsync,
                    new AgentLoopRuntimeEvent
                    {
                        Kind = AgentLoopRuntimeEventKind.IterationCompleted,
                        IterationNumber = iterationNumber,
                        ModelId = response.ModelId,
                        FinalOutput = response.FinalOutput
                    },
                    cancellationToken).ConfigureAwait(false);

                if (response.FinalOutput != null)
                {
                    return new AgentLoopResult
                    {
                        IsCompleted = true,
                        LastModelId = lastModelId,
                        FinalOutput = response.FinalOutput,
                        Iterations = iterations.ToArray()
                    };
                }
            }

            return new AgentLoopResult
            {
                IsCompleted = false,
                FailureReason = $"The agent loop reached the maximum of {MaxIterations} iterations without a tool-free assistant response.",
                LastModelId = lastModelId,
                Iterations = iterations.ToArray()
            };
        }

        private async Task<AgentLoopContextCompressionInfo?> TryApplyMinCompactionAsync(
            AgentLoopRequest request,
            string baseSystemPrompt,
            AgentLoopContextPreparationResult preparedContext,
            IReadOnlyList<LanguageModelChatMessage> persistentHistory,
            IReadOnlyList<LanguageModelChatMessage> turnHistory,
            MinCompactionAttemptState attemptState,
            CancellationToken cancellationToken)
        {
            if (!request.MinCompactionEnabled ||
                request.EnableCompactionTools ||
                string.IsNullOrWhiteSpace(request.CompactionFilePath))
            {
                return null;
            }

            var candidates = _languageModelResolver.GetCandidateModels(request.Agent)
                .Where(model => model.InterfaceSettings.IsFullyConfigured)
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            var compactionModel = candidates
                .OrderBy(model => model.EffectiveContextWindowTokens)
                .First();
            var contextWindowTokens = compactionModel.EffectiveContextWindowTokens;
            var triggerTokenCount = Math.Max(1, (int)Math.Floor(contextWindowTokens * MinCompactionTriggerRatio));
            AgentLoopTokenCountResult tokenCount;
            try
            {
                tokenCount = await _tokenCounter.CountAsync(
                    compactionModel,
                    preparedContext.PreparedMessages,
                    request.CompactionFilePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            if (tokenCount.TokenCount < triggerTokenCount ||
                string.Equals(attemptState.LastAttemptHash, tokenCount.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            attemptState.LastAttemptHash = tokenCount.Hash;

            var transcriptMessages = persistentHistory
                .Concat(turnHistory)
                .Select(message => message.Clone())
                .ToArray();
            var existingCompactedIds = _compactionStore
                .GetCompactedToolCallIds(request.CompactionFilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recordMap = _compactionStore.ExtractToolCallRecords(transcriptMessages)
                .Where(record => !existingCompactedIds.Contains(record.ToolCallId))
                .GroupBy(record => record.ToolCallId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            if (recordMap.Count == 0)
            {
                return null;
            }

            var selectedToolCallIds = await RequestMinCompactionToolCallIdsAsync(
                request,
                compactionModel,
                preparedContext.PreparedMessages,
                recordMap.Values.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var recordsToCompact = selectedToolCallIds
                .Where(id => recordMap.ContainsKey(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => recordMap[id])
                .ToArray();
            if (recordsToCompact.Length == 0)
            {
                return null;
            }

            _compactionStore.CompactToolCalls(request.CompactionFilePath, recordsToCompact);

            var estimatedAfter = 0;
            try
            {
                var compactedPrepared = await _contextManager.PrepareAsync(
                    request.Agent,
                    BuildSystemPromptWithRuntimeToolCallNotice(baseSystemPrompt, request),
                    request.Input,
                    request.InputContentBlocks,
                    persistentHistory,
                    turnHistory,
                    request.CompactionFilePath,
                    sessionResourcesFolderPath: ResolveSessionResourcesFolderPath(request),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var afterCount = await _tokenCounter.CountAsync(
                    compactionModel,
                    compactedPrepared.PreparedMessages,
                    request.CompactionFilePath,
                    cancellationToken).ConfigureAwait(false);
                estimatedAfter = afterCount.TokenCount;
            }
            catch
            {
                estimatedAfter = 0;
            }

            return new AgentLoopContextCompressionInfo
            {
                ContextWindowTokens = contextWindowTokens,
                EstimatedTokenCountBeforeCompression = tokenCount.TokenCount,
                EstimatedTokenCountAfterCompression = estimatedAfter,
                TargetTokenCountAfterCompression = triggerTokenCount,
                CompressionLayerKey = MinCompactionLayerKey,
                CompressionModelId = compactionModel.SummaryModelId,
                CompactedToolCallIds = recordsToCompact
                    .Select(record => record.ToolCallId)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }

        private async Task<IReadOnlyList<string>> RequestMinCompactionToolCallIdsAsync(
            AgentLoopRequest request,
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> preparedMessages,
            IReadOnlyList<AgentLoopCompactionToolCallRecord> candidates,
            CancellationToken cancellationToken)
        {
            if (candidates.Count == 0)
            {
                return Array.Empty<string>();
            }

            var candidateList = string.Join(
                Environment.NewLine,
                candidates.Select(record => $"- {record.ToolCallId}: {record.ToolName}"));
            var prompt = $"""
现在上下文接近已满。请停止当前工作，只做上下文压缩选择。

你可以调用内置工具 CompactToolCalls。一个 CompactToolCalls 可接受多个独立的 ToolCallID。
调用格式：
<Tool ToolName="CompactToolCalls"><ToolCallIDs>["TC1","TC2"]</ToolCallIDs></Tool>

请压缩尽可能多的、对当前任务无效或已经过时的工具调用。不要调用其他工具，不要继续原任务，不要解释。
可压缩工具调用：
{candidateList}
""";

            var compactionMessages = preparedMessages
                .Select(message => message.Clone())
                .Append(new LanguageModelChatMessage(LanguageModelChatRole.User, prompt)
                {
                    IsHostInjectedTail = true
                })
                .ToArray();

            LanguageModelChatResponse response;
            try
            {
                response = await _chatService.GetResponseAsync(
                    model,
                    compactionMessages,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return Array.Empty<string>();
            }

            var assistantResponse = ParseAssistantResponse(
                response.Text,
                request.EnableGemmaThoughtCompatibility);
            return assistantResponse.GetToolCalls()
                .Where(invocation => IsCompactToolCalls(invocation.ToolName))
                .SelectMany(ExtractToolCallIdsArgument)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<StreamedResponseResult> InvokeModelStreamingAsync(
            AgentLoopRequest request,
            Func<string> toolCallIdFactory,
            AgentLoopPreparedRequestDebugSnapshot preparedSnapshot,
            SkyweaverToolContext runtimeToolContext,
            IReadOnlyDictionary<string, IReadOnlyList<string>> toolKitMembershipMap,
            IReadOnlyCollection<string> activeToolKitKeys,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById,
            AsyncToolPublicationGate asyncToolPublicationGate,
            AgentLoopDebugRunContext? debugRunContext,
            int iterationNumber,
            AgentLoopFinalOutput? latestPassdownOutput,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var candidates = _languageModelResolver.GetCandidateModels(request.Agent);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Agent '{request.Agent.DisplayNameOrFallback}' has no resolved candidate language model.");
            }

            Exception? lastError = null;
            var failureMessages = new List<string>();
            var attemptNumber = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!candidate.InterfaceSettings.IsFullyConfigured)
                {
                    failureMessages.Add($"{GetLanguageModelDisplayName(candidate)}: interface settings are incomplete.");
                    continue;
                }

                attemptNumber++;
                AgentLoopDebugRecorder.RecordPreparedRequest(
                    debugRunContext,
                    request,
                    preparedSnapshot,
                    candidates,
                    candidate,
                    iterationNumber,
                    attemptNumber);

                var modelId = candidate.SummaryModelId;
                var hasStartedStreaming = false;
                var rawContentBuilder = new StringBuilder();
                var rawReasoningContentBuilder = new StringBuilder();
                var estimatedInputTokenCount = AgentLoopTokenCounter.EstimateMessages(preparedSnapshot.PreparedMessages);
                var toolInvocationStreamingParser = new SkyweaverToolInvocationStreamingParser(
                    _toolManager.GetRegisteredTools(resolveIcons: false)
                        .Select(registration => registration.Definition));
                IReadOnlyList<SkyweaverStreamingToolCallSnapshot> previousToolCallSnapshots =
                    Array.Empty<SkyweaverStreamingToolCallSnapshot>();
                var toolCallIdsByIndex = new Dictionary<int, string>();
                var presentationTracker = new AssistantPresentationStreamingTracker(
                    request.Agent.IsStructuredXmlIO
                        ? AgentLoopOutputKind.StructuredXml
                        : AgentLoopOutputKind.NaturalLanguage,
                    request.EnableGemmaThoughtCompatibility);
                List<AgentLoopStreamingUpdateDebugSnapshot>? streamingUpdates = debugRunContext == null
                    ? null
                    : new List<AgentLoopStreamingUpdateDebugSnapshot>();
                var lastStreamingTokenUsageRefreshUtc = DateTime.MinValue;
                var lastStreamingProtocolRefreshUtc = DateTime.MinValue;

                AgentLoopTokenUsageInfo BuildStreamingTokenUsage()
                {
                    return new AgentLoopTokenUsageInfo
                    {
                        ContextWindowTokens = candidate.EffectiveContextWindowTokens,
                        EstimatedInputTokenCount = estimatedInputTokenCount,
                        EstimatedOutputTokenCount =
                            EstimateTextLength(rawContentBuilder.Length) +
                            EstimateTextLength(rawReasoningContentBuilder.Length),
                        ModelId = modelId
                    };
                }

                static int EstimateTextLength(int length)
                {
                    return length <= 0
                        ? 0
                        : Math.Max(1, (int)Math.Ceiling(length / 4.0d));
                }

                AgentLoopTokenUsageInfo? TryBuildStreamingTokenUsage(bool force = false)
                {
                    if (onEventAsync == null)
                    {
                        return null;
                    }

                    var now = DateTime.UtcNow;
                    if (!force && now - lastStreamingTokenUsageRefreshUtc < StreamingTokenUsageRefreshInterval)
                    {
                        return null;
                    }

                    lastStreamingTokenUsageRefreshUtc = now;
                    return BuildStreamingTokenUsage();
                }

                IReadOnlyList<AgentLoopStreamingUpdateDebugSnapshot> GetStreamingUpdatesForDebug()
                {
                    return streamingUpdates != null
                        ? streamingUpdates
                        : Array.Empty<AgentLoopStreamingUpdateDebugSnapshot>();
                }

                bool ShouldRefreshStreamingProtocolProjection(bool force = false)
                {
                    if (force)
                    {
                        lastStreamingProtocolRefreshUtc = DateTime.UtcNow;
                        return true;
                    }

                    var now = DateTime.UtcNow;
                    if (now - lastStreamingProtocolRefreshUtc < StreamingProtocolRefreshInterval)
                    {
                        return false;
                    }

                    lastStreamingProtocolRefreshUtc = now;
                    return true;
                }

                try
                {
                    await PublishAsync(
                        onEventAsync,
                        new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.IterationStarted,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            TokenUsage = TryBuildStreamingTokenUsage(force: true)
                        },
                        cancellationToken).ConfigureAwait(false);

                    await foreach (var update in _chatService.GetStreamingResponseAsync(
                                       candidate,
                                       preparedSnapshot.PreparedMessages.Select(message => message.Clone()).ToArray(),
                                       cancellationToken,
                                       (progress, ct) => PublishAsync(
                                           onEventAsync,
                                           new AgentLoopRuntimeEvent
                                           {
                                               Kind = AgentLoopRuntimeEventKind.MediaProcessingProgressUpdated,
                                               IterationNumber = iterationNumber,
                                               ModelId = modelId,
                                               MediaProcessingProgress = new AgentLoopMediaProcessingProgress
                                               {
                                                   Progress = progress
                                               }
                                           },
                                           ct)).ConfigureAwait(false))
                    {
                        if (!string.IsNullOrWhiteSpace(update.ModelId))
                        {
                            modelId = update.ModelId;
                        }

                        if (!string.IsNullOrEmpty(update.ReasoningTextDelta))
                        {
                            rawReasoningContentBuilder.Append(update.ReasoningTextDelta);
                            await PublishAsync(
                                onEventAsync,
                                new AgentLoopRuntimeEvent
                                {
                                    Kind = AgentLoopRuntimeEventKind.ReasoningDelta,
                                    IterationNumber = iterationNumber,
                                    ModelId = modelId,
                                    ReasoningDelta = update.ReasoningTextDelta,
                                    TokenUsage = TryBuildStreamingTokenUsage()
                                },
                                cancellationToken).ConfigureAwait(false);
                        }

                        var rawContentLengthBeforeAppend = rawContentBuilder.Length;
                        var wasAppendedToRawContent = !string.IsNullOrEmpty(update.TextDelta);
                        if (wasAppendedToRawContent)
                        {
                            hasStartedStreaming = true;
                            rawContentBuilder.Append(update.TextDelta);
                        }

                        if (streamingUpdates != null)
                        {
                            streamingUpdates.Add(new AgentLoopStreamingUpdateDebugSnapshot
                            {
                                SequenceNumber = streamingUpdates.Count + 1,
                                ReceivedAtLocal = DateTimeOffset.Now,
                                Update = update,
                                WasAppendedToRawContent = wasAppendedToRawContent,
                                RawContentLengthBeforeAppend = rawContentLengthBeforeAppend,
                                RawContentLengthAfterAppend = rawContentBuilder.Length,
                                RawContentTailAfterAppend = GetStringBuilderTail(
                                    rawContentBuilder,
                                    StreamingTraceRawContentTailLength)
                            });
                        }

                        if (!wasAppendedToRawContent)
                        {
                            continue;
                        }

                        if (!ShouldRefreshStreamingProtocolProjection())
                        {
                            continue;
                        }

                        var currentRawContent = rawContentBuilder.ToString();
                        var currentProtocolContent = PrepareAssistantProtocolContent(
                            currentRawContent,
                            request.EnableGemmaThoughtCompatibility,
                            isFinal: false);
                        var currentToolCallSnapshots = toolInvocationStreamingParser.Parse(currentProtocolContent);
                        await PublishStreamingToolCallUpdatesAsync(
                            onEventAsync,
                            currentToolCallSnapshots,
                            previousToolCallSnapshots,
                            toolCallIdsByIndex,
                            toolCallIdFactory,
                            iterationNumber,
                            modelId,
                            cancellationToken).ConfigureAwait(false);
                        previousToolCallSnapshots = currentToolCallSnapshots;

                        var presentationDeltas = presentationTracker.ExtractDeltas(currentRawContent, isFinal: false);
                        await PublishPresentationDeltasAsync(
                            onEventAsync,
                            presentationDeltas,
                            iterationNumber,
                            modelId,
                            presentationDeltas.Count > 0 ? TryBuildStreamingTokenUsage() : null,
                            cancellationToken).ConfigureAwait(false);
                    }

                    var rawContent = rawContentBuilder.ToString();
                    AgentAssistantResponse assistantResponse;
                    if (TryPromoteAssistantResponseFromReasoning(
                            rawContent,
                            rawReasoningContentBuilder.ToString(),
                            request.EnableGemmaThoughtCompatibility,
                            out var promotedRawContent,
                            out var promotedAssistantResponse))
                    {
                        rawContent = promotedRawContent;
                        assistantResponse = promotedAssistantResponse;
                        hasStartedStreaming = true;
                    }
                    else
                    {
                        assistantResponse = ParseAssistantResponse(
                            rawContent,
                            request.EnableGemmaThoughtCompatibility);
                    }

                    var finalProtocolContent = PrepareAssistantProtocolContent(
                        rawContent,
                        request.EnableGemmaThoughtCompatibility,
                        isFinal: true);
                    var finalToolCallSnapshots = toolInvocationStreamingParser.Parse(finalProtocolContent);
                    await PublishStreamingToolCallUpdatesAsync(
                        onEventAsync,
                        finalToolCallSnapshots,
                        previousToolCallSnapshots,
                        toolCallIdsByIndex,
                        toolCallIdFactory,
                        iterationNumber,
                        modelId,
                        cancellationToken).ConfigureAwait(false);

                    var finalPresentationDeltas = presentationTracker.ExtractDeltas(rawContent, isFinal: true);
                    await PublishPresentationDeltasAsync(
                        onEventAsync,
                        finalPresentationDeltas,
                        iterationNumber,
                        modelId,
                        finalPresentationDeltas.Count > 0 ? TryBuildStreamingTokenUsage(force: true) : null,
                        cancellationToken).ConfigureAwait(false);
                    var hasToolActivity = assistantResponse.GetToolCallParts().Count > 0;

                    if (hasToolActivity)
                    {
                        await PublishAsync(
                            onEventAsync,
                            new AgentLoopRuntimeEvent
                            {
                                Kind = AgentLoopRuntimeEventKind.AssistantToolCallsReceived,
                                IterationNumber = iterationNumber,
                                ModelId = modelId,
                                ToolXml = rawContent
                            },
                            cancellationToken).ConfigureAwait(false);

                        var executionResult = await ExecuteAssistantToolCallsAsync(
                            request,
                            assistantResponse,
                            runtimeToolContext,
                            toolKitMembershipMap,
                            activeToolKitKeys,
                            pendingAsyncToolExecutions,
                            completedAsyncToolResultsById,
                            completedAsyncToolProgressById,
                            asyncToolPublicationGate,
                            toolCallIdsByIndex,
                            toolCallIdFactory,
                            iterationNumber,
                            modelId,
                            latestPassdownOutput,
                            onEventAsync,
                            cancellationToken).ConfigureAwait(false);

                        AgentLoopDebugRecorder.RecordStreamingTrace(
                            debugRunContext,
                            request.Agent,
                            candidate,
                            iterationNumber,
                            attemptNumber,
                            modelId,
                            GetStreamingUpdatesForDebug(),
                            rawContent,
                            hasStartedStreaming,
                            completedNormally: true,
                            terminalMessage: null);

                        return new StreamedResponseResult(
                            attemptNumber,
                            modelId,
                            assistantResponse,
                            executionResult.ToolBackfills,
                            FinalOutput: request.IsSubAgent && executionResult.LatestPassdownOutput?.Source == AgentLoopFinalOutputSource.PassToMainAgentPayload
                                ? executionResult.LatestPassdownOutput
                                : null,
                            executionResult.LatestPassdownOutput,
                            executionResult.NewlyLoadedToolKitKeys);
                    }

                    if (request.IsSubAgent && latestPassdownOutput == null)
                    {
                        var reminderBackfill = CreateBackfill(
                            0,
                            0,
                            [_toolManager.CreateToolReturnPayload(
                                SkyweaverBuiltInToolNames.PassToMainAgent,
                                SkyweaverToolResult.Success($"Sub-agent loops do not end on plain text. Continue with tools, or call {SkyweaverBuiltInToolNames.PassToMainAgent} to return content to the main agent."))]);

                        AgentLoopDebugRecorder.RecordStreamingTrace(
                            debugRunContext,
                            request.Agent,
                            candidate,
                            iterationNumber,
                            attemptNumber,
                            modelId,
                            GetStreamingUpdatesForDebug(),
                            rawContent,
                            hasStartedStreaming,
                            completedNormally: true,
                            terminalMessage: null);

                        return new StreamedResponseResult(
                            attemptNumber,
                            modelId,
                            assistantResponse,
                            [reminderBackfill],
                            FinalOutput: null,
                            LatestPassdownOutput: null,
                            Array.Empty<string>());
                    }

                    var finalOutput = latestPassdownOutput ??
                                       BuildAssistantTextFinalOutput(
                                           request.Agent,
                                          rawContent,
                                          request.EnableGemmaThoughtCompatibility);
                    await PublishAsync(
                        onEventAsync,
                        new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.FinalOutputProduced,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            FinalOutput = finalOutput
                        },
                        cancellationToken).ConfigureAwait(false);

                    AgentLoopDebugRecorder.RecordStreamingTrace(
                        debugRunContext,
                        request.Agent,
                        candidate,
                        iterationNumber,
                        attemptNumber,
                        modelId,
                        GetStreamingUpdatesForDebug(),
                        rawContent,
                        hasStartedStreaming,
                        completedNormally: true,
                        terminalMessage: null);

                    return new StreamedResponseResult(
                        attemptNumber,
                        modelId,
                        assistantResponse,
                        Array.Empty<AgentToolBackfill>(),
                        finalOutput,
                        latestPassdownOutput,
                        Array.Empty<string>());
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AgentLoopDebugRecorder.RecordStreamingTrace(
                        debugRunContext,
                        request.Agent,
                        candidate,
                        iterationNumber,
                        attemptNumber,
                        modelId,
                        GetStreamingUpdatesForDebug(),
                        rawContentBuilder.ToString(),
                        hasStartedStreaming,
                        completedNormally: false,
                        terminalMessage: ex.Message);

                    AgentLoopDebugRecorder.RecordMainRequestFailure(
                        debugRunContext,
                        request.Agent,
                        candidate,
                        iterationNumber,
                        attemptNumber,
                        modelId,
                        rawContentBuilder.ToString(),
                        hasStartedStreaming,
                        ex);

                    if (hasStartedStreaming)
                    {
                        throw;
                    }

                    lastError = ex;
                    failureMessages.Add($"{GetLanguageModelDisplayName(candidate)}: {ex.Message}");
                }
            }

            var failureText = failureMessages.Count == 0
                ? "No callable candidate language model is available."
                : $"Tried candidates in order: {string.Join("; ", failureMessages)}";

            throw new InvalidOperationException(
                $"Agent '{request.Agent.DisplayNameOrFallback}' could not start a streaming response. {failureText}",
                lastError);
        }

        private static string GetStringBuilderTail(StringBuilder builder, int maxLength)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (builder.Length == 0 || maxLength <= 0)
            {
                return string.Empty;
            }

            var length = Math.Min(maxLength, builder.Length);
            return builder.ToString(builder.Length - length, length);
        }

        private bool TryPromoteAssistantResponseFromReasoning(
            string rawContent,
            string rawReasoningContent,
            bool enableGemmaThoughtCompatibility,
            out string promotedRawContent,
            out AgentAssistantResponse promotedAssistantResponse)
        {
            promotedRawContent = rawContent ?? string.Empty;
            promotedAssistantResponse = null!;

            if (!string.IsNullOrWhiteSpace(rawContent) ||
                string.IsNullOrWhiteSpace(rawReasoningContent))
            {
                return false;
            }

            var reasoningResponse = ParseAssistantResponse(
                rawReasoningContent,
                enableGemmaThoughtCompatibility);
            var hasValidToolCalls = reasoningResponse.GetToolCalls().Count > 0;
            var hasAnyToolParts = reasoningResponse.GetToolCallParts().Count > 0;
            var visibleText = ExtractVisibleAssistantText(
                rawReasoningContent,
                isFinal: true,
                enableGemmaThoughtCompatibility).Trim();
            var hasRecoverableVisibleText = visibleText.Length > 0 && !hasAnyToolParts;
            if (!hasValidToolCalls && !hasRecoverableVisibleText)
            {
                return false;
            }

            // Some OpenAI-compatible backends occasionally place the assistant's
            // executable response in reasoning deltas instead of text deltas.
            // Only promote reasoning when it already parses as a valid tool call,
            // or as plain visible assistant text without tool markup.
            promotedRawContent = rawReasoningContent;
            promotedAssistantResponse = reasoningResponse;
            return true;
        }

        private async Task<(IReadOnlyList<AgentToolBackfill> ToolBackfills, AgentLoopFinalOutput? LatestPassdownOutput, IReadOnlyList<string> NewlyLoadedToolKitKeys)>
            ExecuteAssistantToolCallsAsync(
                AgentLoopRequest request,
            AgentAssistantResponse assistantResponse,
            SkyweaverToolContext runtimeToolContext,
            IReadOnlyDictionary<string, IReadOnlyList<string>> toolKitMembershipMap,
            IReadOnlyCollection<string> activeToolKitKeys,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById,
            AsyncToolPublicationGate asyncToolPublicationGate,
            IDictionary<int, string> toolCallIdsByIndex,
            Func<string> toolCallIdFactory,
            int iterationNumber,
            string? modelId,
            AgentLoopFinalOutput? latestPassdownOutput,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
                CancellationToken cancellationToken)
        {
            var toolBackfills = new List<AgentToolBackfill>();
            var newlyLoadedToolKitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentPassdownOutput = latestPassdownOutput;

            for (var partIndex = 0; partIndex < assistantResponse.Parts.Count; partIndex++)
            {
                var part = assistantResponse.Parts[partIndex];
                if (!part.IsToolCall)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (part.HasParseError || part.ToolCalls.Count == 0)
                {
                    var malformedToolCallId = ResolveToolCallId(
                        part.ToolCallIndex,
                        toolCallIdsByIndex,
                        toolCallIdFactory);
                    var parseErrorMessage = AppendToolParseErrorHint(
                        part.ParseError ?? "Tool call could not be parsed.");
                    var backfill = CreateBackfill(
                        partIndex,
                        part.ToolCallIndex,
                        [_toolManager.CreateErrorToolReturnPayload(
                            ToolParseErrorName,
                            parseErrorMessage,
                            malformedToolCallId)],
                        malformedToolCallId);
                    toolBackfills.Add(backfill);

                    await PublishAsync(
                        onEventAsync,
                        new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.MalformedToolCall,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            PartIndex = partIndex,
                            ToolCallIndex = part.ToolCallIndex,
                            ToolCallId = backfill.ToolCallId,
                            ToolXml = part.Content,
                            ToolOutputXml = backfill.ToolsReturnXml,
                            ToolReturns = backfill.ToolReturns,
                            ErrorMessage = parseErrorMessage
                        },
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                foreach (var invocation in part.ToolCalls)
                {
                    AgentToolBackfill backfill;
                    var toolCallId = ResolveToolCallId(
                        part.ToolCallIndex,
                        toolCallIdsByIndex,
                        toolCallIdFactory);

                    if (IsWaitForAsyncTools(invocation.ToolName))
                    {
                        if (invocation.IsAsyncInvocation)
                        {
                            var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                                SkyweaverBuiltInToolNames.WaitForAsyncTools,
                                "WaitForAsyncTools cannot be invoked asynchronously.",
                                toolCallId);
                            backfill = CreateBackfill(
                                partIndex,
                                part.ToolCallIndex,
                                [failurePayload],
                                toolCallId);
                        }
                        else
                        {
                            try
                            {
                                var result = await ExecuteWaitForAsyncToolsAsync(
                                    request,
                                    invocation,
                                    pendingAsyncToolExecutions,
                                    completedAsyncToolResultsById,
                                    completedAsyncToolProgressById,
                                    cancellationToken).ConfigureAwait(false);
                                var payload = _toolManager.CreateToolReturnPayload(
                                    SkyweaverBuiltInToolNames.WaitForAsyncTools,
                                    result,
                                    toolCallId);
                                backfill = CreateBackfill(partIndex, part.ToolCallIndex, [payload], toolCallId);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                                    SkyweaverBuiltInToolNames.WaitForAsyncTools,
                                    $"WaitForAsyncTools execution failed: {ex.Message}",
                                    toolCallId);
                                backfill = CreateBackfill(partIndex, part.ToolCallIndex, [failurePayload], toolCallId);
                            }
                        }
                    }
                    else if (IsGetAsyncToolProgress(invocation.ToolName))
                    {
                        if (invocation.IsAsyncInvocation)
                        {
                            var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                                SkyweaverBuiltInToolNames.GetAsyncToolProgress,
                                "GetAsyncToolProgress cannot be invoked asynchronously.",
                                toolCallId);
                            backfill = CreateBackfill(
                                partIndex,
                                part.ToolCallIndex,
                                [failurePayload],
                                toolCallId);
                        }
                        else
                        {
                            try
                            {
                                var result = ExecuteGetAsyncToolProgress(
                                    request,
                                    invocation,
                                    pendingAsyncToolExecutions,
                                    completedAsyncToolResultsById,
                                    completedAsyncToolProgressById);
                                var payload = _toolManager.CreateToolReturnPayload(
                                    SkyweaverBuiltInToolNames.GetAsyncToolProgress,
                                    result,
                                    toolCallId);
                                backfill = CreateBackfill(partIndex, part.ToolCallIndex, [payload], toolCallId);
                            }
                            catch (Exception ex)
                            {
                                var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                                    SkyweaverBuiltInToolNames.GetAsyncToolProgress,
                                    $"GetAsyncToolProgress execution failed: {ex.Message}",
                                    toolCallId);
                                backfill = CreateBackfill(partIndex, part.ToolCallIndex, [failurePayload], toolCallId);
                            }
                        }
                    }
                    else if (IsCompactToolCalls(invocation.ToolName))
                    {
                        var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                            SkyweaverBuiltInToolNames.CompactToolCalls,
                            "CompactToolCalls is reserved for hidden MinCompaction passes and is disabled in normal agent loops.",
                            toolCallId);
                        backfill = CreateBackfill(partIndex, part.ToolCallIndex, [failurePayload], toolCallId);
                    }
                    else if (IsRetrieveCompactedToolCalls(invocation.ToolName))
                    {
                        backfill = ExecuteRetrieveCompactedToolCallsInvocation(
                            request,
                            invocation,
                            partIndex,
                            part.ToolCallIndex,
                            toolCallId);
                    }
                    else if (IsPassdown(invocation.ToolName))
                    {
                        if (request.IsSubAgent)
                        {
                            backfill = CreateBackfill(
                                partIndex,
                                part.ToolCallIndex,
                                [_toolManager.CreateErrorToolReturnPayload(
                                    SkyweaverBuiltInToolNames.Passdown,
                                    "Passdown is disabled for sub-agents. Use PassToMainAgent to return content to the main agent.",
                                    toolCallId)],
                                toolCallId);
                        }
                        else if (invocation.IsAsyncInvocation)
                        {
                            var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                                SkyweaverBuiltInToolNames.Passdown,
                                "Passdown cannot be invoked asynchronously.",
                                toolCallId);
                            completedAsyncToolResultsById[toolCallId] = failurePayload;
                            backfill = CreateBackfill(
                                partIndex,
                                part.ToolCallIndex,
                                [failurePayload],
                                toolCallId);
                        }
                        else
                        {
                            backfill = ExecutePassdownInvocation(
                                request.Agent,
                                invocation,
                                partIndex,
                                part.ToolCallIndex,
                                toolCallId,
                                out var passdownOutput);

                            if (passdownOutput != null)
                            {
                                currentPassdownOutput = passdownOutput;
                            }
                        }
                    }
                    else if (IsPassToMainAgent(invocation.ToolName))
                    {
                        if (!request.IsSubAgent)
                        {
                            backfill = CreateBackfill(
                                partIndex,
                                part.ToolCallIndex,
                                [_toolManager.CreateErrorToolReturnPayload(
                                    SkyweaverBuiltInToolNames.PassToMainAgent,
                                    "PassToMainAgent can only be used by sub-agents.",
                                    toolCallId)],
                                toolCallId);
                        }
                        else if (invocation.IsAsyncInvocation)
                        {
                            var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                                SkyweaverBuiltInToolNames.PassToMainAgent,
                                "PassToMainAgent cannot be invoked asynchronously.",
                                toolCallId);
                            completedAsyncToolResultsById[toolCallId] = failurePayload;
                            backfill = CreateBackfill(
                                partIndex,
                                part.ToolCallIndex,
                                [failurePayload],
                                toolCallId);
                        }
                        else
                        {
                            backfill = ExecutePassToMainAgentInvocation(
                                request.Agent,
                                invocation,
                                partIndex,
                                part.ToolCallIndex,
                                toolCallId,
                                out var passToMainOutput);

                            if (passToMainOutput != null)
                            {
                                currentPassdownOutput = passToMainOutput;
                            }
                        }
                    }
                    else if (invocation.IsAsyncInvocation)
                    {
                        backfill = await ExecuteAsyncToolInvocationAsync(
                            request,
                            invocation,
                            runtimeToolContext,
                            toolKitMembershipMap,
                            activeToolKitKeys,
                            pendingAsyncToolExecutions,
                            completedAsyncToolResultsById,
                            completedAsyncToolProgressById,
                            asyncToolPublicationGate,
                            toolCallId,
                            iterationNumber,
                            partIndex,
                            part.ToolCallIndex,
                            modelId,
                            onEventAsync,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var toolReturns = await ExecuteAuthorizedInvocationsAsync(
                            request,
                            [invocation],
                            runtimeToolContext,
                            toolKitMembershipMap,
                            activeToolKitKeys,
                            iterationNumber,
                            partIndex,
                            part.ToolCallIndex,
                            toolCallId,
                            modelId,
                            onEventAsync,
                            cancellationToken).ConfigureAwait(false);

                        if (IsLoadToolKits(invocation.ToolName))
                        {
                            foreach (var toolKitKey in ExtractLoadedToolKitKeys(toolReturns))
                            {
                                newlyLoadedToolKitKeys.Add(toolKitKey);
                            }
                        }

                        backfill = CreateBackfill(partIndex, part.ToolCallIndex, toolReturns, toolCallId);
                    }

                    toolBackfills.Add(backfill);

                    await PublishAsync(
                        onEventAsync,
                        new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.ToolOutputReceived,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            PartIndex = partIndex,
                            ToolCallIndex = part.ToolCallIndex,
                            ToolCallId = backfill.ToolCallId,
                            ToolInvocation = invocation,
                            ToolOutputXml = backfill.ToolsReturnXml,
                            ToolReturns = backfill.ToolReturns
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return (toolBackfills, currentPassdownOutput, newlyLoadedToolKitKeys.ToArray());
        }

        private async Task<AsyncToolFlushResult> FlushCompletedAsyncToolBackfillsAsync(
            AgentLoopRequest request,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById,
            ICollection<LanguageModelChatMessage> turnHistory,
            int iterationNumber,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(pendingAsyncToolExecutions);
            ArgumentNullException.ThrowIfNull(completedAsyncToolResultsById);
            ArgumentNullException.ThrowIfNull(completedAsyncToolProgressById);
            ArgumentNullException.ThrowIfNull(turnHistory);

            var scopeKey = ResolveAsyncToolScopeKey(request);
            var flushedBackfills = new List<AgentToolBackfill>();
            var newlyLoadedToolKitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localToolCallIds = pendingAsyncToolExecutions
                .Select(execution => execution.ToolCallId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deliveredToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var index = pendingAsyncToolExecutions.Count - 1; index >= 0; index--)
            {
                var execution = pendingAsyncToolExecutions[index];
                if (!execution.ExecutionTask.IsCompleted)
                {
                    continue;
                }

                pendingAsyncToolExecutions.RemoveAt(index);
                cancellationToken.ThrowIfCancellationRequested();

                SkyweaverToolReturnPayload payload;
                try
                {
                    var result = await execution.ExecutionTask.ConfigureAwait(false);
                    payload = _toolManager.CreateToolReturnPayload(
                        execution.Invocation.ToolName,
                        result,
                        execution.ToolCallId);

                    if (IsLoadToolKits(execution.Invocation.ToolName))
                    {
                        foreach (var toolKitKey in ExtractLoadedToolKitKeys([payload]))
                        {
                            newlyLoadedToolKitKeys.Add(toolKitKey);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    payload = _toolManager.CreateErrorToolReturnPayload(
                        execution.Invocation.ToolName,
                        "Async tool execution was cancelled.",
                        execution.ToolCallId);
                }
                catch (Exception ex)
                {
                    payload = _toolManager.CreateErrorToolReturnPayload(
                        execution.Invocation.ToolName,
                        $"Tool execution failed: {ex.Message}",
                        execution.ToolCallId);
                }

                completedAsyncToolResultsById[execution.ToolCallId] = payload;
                var progressSnapshot = execution.ProgressTracker.CreateSnapshot(
                        execution.ToolCallId,
                        execution.Invocation.ToolName,
                        isPending: false,
                        isCompleted: true);
                completedAsyncToolProgressById[execution.ToolCallId] = progressSnapshot;
                s_asyncToolRegistry.MarkCompleted(
                    scopeKey,
                    execution,
                    payload,
                    progressSnapshot,
                    deliveredToConversation: true);
                var backfill = CreateBackfill(
                    execution.PartIndex,
                    execution.ToolCallIndex,
                    [payload],
                    execution.ToolCallId);
                flushedBackfills.Add(backfill);
                deliveredToolCallIds.Add(execution.ToolCallId);
                turnHistory.Add(CreateToolResultMessage(backfill));
                PersistAsyncToolOutput(
                    request.ToolCallResourceFolderPath,
                    execution.ToolCallId,
                    request.Agent.AgentId,
                    backfill.ToolsReturnXml,
                    transcriptDelivered: true);

                await PublishAsync(
                    onEventAsync,
                    new AgentLoopRuntimeEvent
                    {
                        Kind = AgentLoopRuntimeEventKind.ToolOutputReceived,
                        IterationNumber = iterationNumber,
                        PartIndex = execution.PartIndex,
                        ToolCallIndex = execution.ToolCallIndex,
                        ToolCallId = execution.ToolCallId,
                        ToolInvocation = execution.Invocation,
                        ToolOutputXml = backfill.ToolsReturnXml,
                        ToolReturns = backfill.ToolReturns
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var registryExecution in s_asyncToolRegistry.GetExecutions(scopeKey)
                         .Where(execution => execution.ExecutionTask.IsCompleted &&
                                             !localToolCallIds.Contains(execution.ToolCallId)))
            {
                await ObserveAsyncToolCompletionAsync(
                    request,
                    scopeKey,
                    registryExecution,
                    cancellationToken).ConfigureAwait(false);
            }

            var registryCompletions = s_asyncToolRegistry.ConsumeUndeliveredCompleted(
                scopeKey,
                localToolCallIds.Concat(deliveredToolCallIds));
            foreach (var completion in registryCompletions)
            {
                var execution = completion.Execution;
                var backfill = CreateBackfill(
                    execution.PartIndex,
                    execution.ToolCallIndex,
                    [completion.Payload],
                    execution.ToolCallId);
                completedAsyncToolResultsById[execution.ToolCallId] = completion.Payload;
                completedAsyncToolProgressById[execution.ToolCallId] = completion.ProgressSnapshot;
                flushedBackfills.Add(backfill);
                deliveredToolCallIds.Add(execution.ToolCallId);
                turnHistory.Add(CreateToolResultMessage(backfill));

                if (IsLoadToolKits(execution.Invocation.ToolName))
                {
                    foreach (var toolKitKey in ExtractLoadedToolKitKeys([completion.Payload]))
                    {
                        newlyLoadedToolKitKeys.Add(toolKitKey);
                    }
                }

                PersistAsyncToolOutput(
                    request.ToolCallResourceFolderPath,
                    execution.ToolCallId,
                    request.Agent.AgentId,
                    backfill.ToolsReturnXml,
                    transcriptDelivered: true);

                await PublishAsync(
                    onEventAsync,
                    new AgentLoopRuntimeEvent
                    {
                        Kind = AgentLoopRuntimeEventKind.ToolOutputReceived,
                        IterationNumber = iterationNumber,
                        PartIndex = execution.PartIndex,
                        ToolCallIndex = execution.ToolCallIndex,
                        ToolCallId = execution.ToolCallId,
                        ToolInvocation = execution.Invocation,
                        ToolOutputXml = backfill.ToolsReturnXml,
                        ToolReturns = backfill.ToolReturns
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var restored in RestoreUndeliveredPersistedAsyncToolOutputs(
                         request,
                         localToolCallIds.Concat(deliveredToolCallIds)))
            {
                flushedBackfills.Add(restored.Backfill);
                deliveredToolCallIds.Add(restored.ToolCallId);
                turnHistory.Add(CreateToolResultMessage(restored.Backfill));
                foreach (var toolKitKey in ExtractLoadedToolKitKeysFromToolsReturnXml(restored.Backfill.ToolsReturnXml))
                {
                    newlyLoadedToolKitKeys.Add(toolKitKey);
                }

                await PublishAsync(
                    onEventAsync,
                    new AgentLoopRuntimeEvent
                    {
                        Kind = AgentLoopRuntimeEventKind.ToolOutputReceived,
                        IterationNumber = iterationNumber,
                        PartIndex = restored.Backfill.PartIndex,
                        ToolCallIndex = restored.Backfill.ToolCallIndex,
                        ToolCallId = restored.ToolCallId,
                        ToolInvocation = restored.Invocation,
                        ToolOutputXml = restored.Backfill.ToolsReturnXml,
                        ToolReturns = restored.Backfill.ToolReturns
                    },
                    cancellationToken).ConfigureAwait(false);

                _toolCallResourceStore.MarkOutputTranscriptDelivered(
                    request.ToolCallResourceFolderPath,
                    restored.ToolCallId);
            }

            return new AsyncToolFlushResult(flushedBackfills, newlyLoadedToolKitKeys.ToArray());
        }

        private IReadOnlyList<PersistedAsyncToolBackfill> RestoreUndeliveredPersistedAsyncToolOutputs(
            AgentLoopRequest request,
            IEnumerable<string> excludedToolCallIds)
        {
            if (string.IsNullOrWhiteSpace(request.ToolCallResourceFolderPath))
            {
                return Array.Empty<PersistedAsyncToolBackfill>();
            }

            var excludedIds = excludedToolCallIds
                .Select(ChatSessionToolCallIdGenerator.Normalize)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var restored = new List<PersistedAsyncToolBackfill>();
            foreach (var record in _toolCallResourceStore.EnumerateRecords(request.ToolCallResourceFolderPath))
            {
                if (excludedIds.Contains(record.ToolCallId) ||
                    record.IsOutputTranscriptDelivered ||
                    string.IsNullOrWhiteSpace(record.OutputXml) ||
                    !TryParsePersistedAsyncInvocation(record.InvocationXml, out var invocation))
                {
                    continue;
                }

                var backfill = new AgentToolBackfill
                {
                    PartIndex = 0,
                    ToolCallIndex = 0,
                    ToolCallId = record.ToolCallId,
                    ToolReturns = Array.Empty<SkyweaverToolReturnPayload>(),
                    ToolsReturnXml = AgentLoopCompactionStore.EnsureToolCallIdInToolsReturnXml(
                        record.OutputXml,
                        record.ToolCallId)
                };
                restored.Add(new PersistedAsyncToolBackfill(record.ToolCallId, backfill, invocation));
                excludedIds.Add(record.ToolCallId);
            }

            return restored
                .OrderBy(item => GetToolCallIdOrdinal(item.ToolCallId))
                .ThenBy(item => item.ToolCallId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private bool TryParsePersistedAsyncInvocation(
            string? invocationXml,
            out SkyweaverToolInvocation? invocation)
        {
            invocation = null;
            if (string.IsNullOrWhiteSpace(invocationXml))
            {
                return false;
            }

            try
            {
                invocation = _toolManager.ParseToolInvocationXml(invocationXml)
                    .FirstOrDefault(item => item.IsAsyncInvocation);
                return invocation != null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or XmlException)
            {
                return false;
            }
        }

        private bool TryGetPersistedAsyncToolRecord(
            AgentLoopRequest request,
            string? toolCallId,
            out ChatSessionToolCallResourceRecord record,
            bool requireOutput)
        {
            record = null!;
            var normalizedToolCallId = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
            if (normalizedToolCallId.Length == 0 ||
                string.IsNullOrWhiteSpace(request.ToolCallResourceFolderPath))
            {
                return false;
            }

            foreach (var candidate in _toolCallResourceStore.EnumerateRecords(request.ToolCallResourceFolderPath))
            {
                if (!string.Equals(candidate.ToolCallId, normalizedToolCallId, StringComparison.OrdinalIgnoreCase) ||
                    (requireOutput && string.IsNullOrWhiteSpace(candidate.OutputXml)) ||
                    !TryParsePersistedAsyncInvocation(candidate.InvocationXml, out _))
                {
                    continue;
                }

                record = candidate;
                return true;
            }

            return false;
        }

        private bool TryGetKnownAsyncToolRecord(
            AgentLoopRequest request,
            string? toolCallId,
            out ChatSessionToolCallResourceRecord record,
            bool requireOutput)
        {
            if (TryGetPersistedAsyncToolRecord(request, toolCallId, out record, requireOutput))
            {
                return true;
            }

            record = null!;
            var normalizedToolCallId = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
            if (normalizedToolCallId.Length == 0)
            {
                return false;
            }

            foreach (var candidate in CollectHistoryToolCallRecords(request))
            {
                if (!string.Equals(candidate.ToolCallId, normalizedToolCallId, StringComparison.OrdinalIgnoreCase) ||
                    (requireOutput && string.IsNullOrWhiteSpace(candidate.OutputXml)) ||
                    !TryParsePersistedAsyncInvocation(candidate.InvocationXml, out _))
                {
                    continue;
                }

                record = new ChatSessionToolCallResourceRecord
                {
                    ToolCallId = candidate.ToolCallId,
                    InvocationXml = candidate.InvocationXml,
                    OutputXml = candidate.OutputXml,
                    IsOutputTranscriptDelivered = true
                };
                return true;
            }

            return false;
        }

        private static async Task PublishStreamingToolCallUpdatesAsync(
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            IReadOnlyList<SkyweaverStreamingToolCallSnapshot> currentSnapshots,
            IReadOnlyList<SkyweaverStreamingToolCallSnapshot> previousSnapshots,
            IDictionary<int, string> toolCallIdsByIndex,
            Func<string> toolCallIdFactory,
            int iterationNumber,
            string? modelId,
            CancellationToken cancellationToken)
        {
            if (onEventAsync == null || currentSnapshots.Count == 0)
            {
                return;
            }

            var previousByToolCallIndex = previousSnapshots.ToDictionary(
                snapshot => snapshot.ToolCallIndex,
                snapshot => snapshot);

            foreach (var snapshot in currentSnapshots.OrderBy(item => item.ToolCallIndex))
            {
                var eventKind = previousByToolCallIndex.TryGetValue(snapshot.ToolCallIndex, out var previousSnapshot)
                    ? AreEquivalent(previousSnapshot, snapshot)
                        ? (AgentLoopRuntimeEventKind?)null
                        : AgentLoopRuntimeEventKind.ToolCallUpdated
                    : AgentLoopRuntimeEventKind.ToolCallStarted;

                if (eventKind == null)
                {
                    continue;
                }

                await PublishAsync(
                    onEventAsync,
                    new AgentLoopRuntimeEvent
                    {
                        Kind = eventKind.Value,
                        IterationNumber = iterationNumber,
                        ModelId = modelId,
                        PartIndex = snapshot.PartIndex,
                        ToolCallIndex = snapshot.ToolCallIndex,
                        ToolCallId = ResolveToolCallId(
                            snapshot.ToolCallIndex,
                            toolCallIdsByIndex,
                            toolCallIdFactory),
                        ToolCallSnapshot = snapshot,
                        ToolXml = snapshot.ToolXmlFragment
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task PublishPresentationDeltasAsync(
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            IReadOnlyList<AssistantPresentationDelta> deltas,
            int iterationNumber,
            string? modelId,
            AgentLoopTokenUsageInfo? tokenUsage,
            CancellationToken cancellationToken)
        {
            if (onEventAsync == null || deltas.Count == 0)
            {
                return;
            }

            foreach (var delta in deltas)
            {
                if (string.IsNullOrEmpty(delta.Content))
                {
                    continue;
                }

                await PublishAsync(
                    onEventAsync,
                    delta.IsReasoning
                        ? new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.ReasoningDelta,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            ReasoningDelta = delta.Content,
                            PartIndex = delta.PartIndex,
                            IsReasoningCollapsible = delta.IsReasoningCollapsible,
                            TokenUsage = tokenUsage
                        }
                        : new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.TextDelta,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            TextDelta = delta.Content,
                            TextDeltaOutputKind = delta.TextOutputKind,
                            TokenUsage = tokenUsage
                        },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool AreEquivalent(
            SkyweaverStreamingToolCallSnapshot left,
            SkyweaverStreamingToolCallSnapshot right)
        {
            return left.PartIndex == right.PartIndex &&
                   left.ToolCallIndex == right.ToolCallIndex &&
                   string.Equals(left.ToolName, right.ToolName, StringComparison.OrdinalIgnoreCase) &&
                   left.IsAsyncInvocation == right.IsAsyncInvocation &&
                   left.IsInvocationClosed == right.IsInvocationClosed &&
                   string.Equals(left.ToolXmlFragment, right.ToolXmlFragment, StringComparison.Ordinal) &&
                   AreEquivalent(left.Parameters, right.Parameters);
        }

        private static bool AreEquivalent(
            IReadOnlyList<SkyweaverStreamingToolParameterSnapshot> left,
            IReadOnlyList<SkyweaverStreamingToolParameterSnapshot> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                var leftParameter = left[index];
                var rightParameter = right[index];
                if (!string.Equals(leftParameter.Name, rightParameter.Name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(leftParameter.Value, rightParameter.Value, StringComparison.Ordinal) ||
                    leftParameter.IsClosed != rightParameter.IsClosed)
                {
                    return false;
                }
            }

            return true;
        }

        private AgentToolBackfill ExecutePassdownInvocation(
            AgentDefinition agent,
            SkyweaverToolInvocation invocation,
            int partIndex,
            int toolCallIndex,
            string toolCallId,
            out AgentLoopFinalOutput? passdownOutput)
        {
            passdownOutput = null;
            if (!TryBuildPassdownOutput(agent, invocation, out var output, out var errorMessage))
            {
                return CreateBackfill(
                    partIndex,
                    toolCallIndex,
                    [_toolManager.CreateErrorToolReturnPayload(
                        SkyweaverBuiltInToolNames.Passdown,
                        errorMessage,
                        toolCallId)],
                    toolCallId);
            }

            passdownOutput = output;
            return CreateBackfill(
                partIndex,
                toolCallIndex,
                [_toolManager.CreateToolReturnPayload(
                    SkyweaverBuiltInToolNames.Passdown,
                    SkyweaverToolResult.Success("Passdown accepted."),
                    toolCallId)],
                toolCallId);
        }

        private AgentToolBackfill ExecutePassToMainAgentInvocation(
            AgentDefinition agent,
            SkyweaverToolInvocation invocation,
            int partIndex,
            int toolCallIndex,
            string toolCallId,
            out AgentLoopFinalOutput? passToMainOutput)
        {
            passToMainOutput = null;
            if (!TryBuildPassToMainAgentOutput(agent, invocation, out var output, out var errorMessage))
            {
                return CreateBackfill(
                    partIndex,
                    toolCallIndex,
                    [_toolManager.CreateErrorToolReturnPayload(
                        SkyweaverBuiltInToolNames.PassToMainAgent,
                        errorMessage,
                        toolCallId)],
                    toolCallId);
            }

            passToMainOutput = output;
            return CreateBackfill(
                partIndex,
                toolCallIndex,
                [_toolManager.CreateToolReturnPayload(
                    SkyweaverBuiltInToolNames.PassToMainAgent,
                    SkyweaverToolResult.Success("PassToMainAgent accepted."),
                    toolCallId)],
                toolCallId);
        }

        private AgentToolBackfill ExecuteRetrieveCompactedToolCallsInvocation(
            AgentLoopRequest request,
            SkyweaverToolInvocation invocation,
            int partIndex,
            int toolCallIndex,
            string toolCallId)
        {
            var requestedToolCallIds = ExtractToolCallIdsArgument(invocation);
            if (requestedToolCallIds.Count == 0)
            {
                var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                    SkyweaverBuiltInToolNames.RetrieveCompactedToolCalls,
                    "RetrieveCompactedToolCalls requires at least one ToolCallID.",
                    toolCallId);
                return CreateBackfill(partIndex, toolCallIndex, [failurePayload], toolCallId);
            }

            var retrievedIds = _compactionStore.RetrieveToolCalls(
                request.CompactionFilePath,
                requestedToolCallIds);
            var message = retrievedIds.Count == 0
                ? "No requested compacted tool calls were found."
                : $"Retrieved compacted tool call(s): {string.Join(", ", retrievedIds)}. The original tool invocation and return content will be visible in the next agent loop.";
            var payload = _toolManager.CreateToolReturnPayload(
                SkyweaverBuiltInToolNames.RetrieveCompactedToolCalls,
                SkyweaverToolResult.Success(
                    message,
                    new Dictionary<string, object?>
                    {
                        ["requestedToolCallIds"] = new JArray(requestedToolCallIds),
                        ["retrievedToolCallIds"] = new JArray(retrievedIds)
                    }),
                toolCallId);
            return CreateBackfill(partIndex, toolCallIndex, [payload], toolCallId);
        }

        private static async ValueTask PublishAsync(
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            AgentLoopRuntimeEvent update,
            CancellationToken cancellationToken)
        {
            if (onEventAsync == null)
            {
                return;
            }

            await onEventAsync(update, cancellationToken).ConfigureAwait(false);
        }

        private static void ValidateRequest(AgentLoopRequest request)
        {
            ArgumentNullException.ThrowIfNull(request.Agent);

            if (request.Agent.IsStructuredXmlIO &&
                !TryParseXmlWithRoot(
                    request.Input,
                    AgentDefinition.InputRootName,
                    out _,
                    out var inputError))
            {
                throw new InvalidOperationException(
                    $"Structured agent input must be a complete <{AgentDefinition.InputRootName}> XML document. {inputError}");
            }
        }

        private AgentAssistantResponse ParseAssistantResponse(
            string rawContent,
            bool enableGemmaThoughtCompatibility)
        {
            var content = PrepareAssistantProtocolContent(
                rawContent,
                enableGemmaThoughtCompatibility,
                isFinal: true);
            if (content.Length == 0)
            {
                return new AgentAssistantResponse(rawContent ?? string.Empty, Array.Empty<AgentAssistantResponsePart>());
            }

            var parts = new List<AgentAssistantResponsePart>();
            var index = 0;
            var toolCallIndex = 0;

            while (index < content.Length)
            {
                var toolStartIndex = FindNextToolTagIndex(content, index, out var toolElementName);
                if (toolStartIndex < 0)
                {
                    AddNaturalLanguagePart(parts, content[index..]);
                    break;
                }

                if (toolStartIndex > index)
                {
                    AddNaturalLanguagePart(parts, content[index..toolStartIndex]);
                }

                toolCallIndex++;
                var toolOpenTagEndIndex = FindTagEnd(content, toolStartIndex);
                if (toolOpenTagEndIndex < 0)
                {
                    parts.Add(AgentAssistantResponsePart.CreateToolCall(
                        content[toolStartIndex..],
                        Array.Empty<SkyweaverToolInvocation>(),
                        $"The <{toolElementName}> opening tag is incomplete.",
                        toolCallIndex));
                    break;
                }

                var toolEndIndex = IsSelfClosingTag(content[toolStartIndex..toolOpenTagEndIndex])
                    ? toolOpenTagEndIndex
                    : IndexOfClosingElementEnd(content, toolElementName, toolOpenTagEndIndex);
                if (toolEndIndex < 0)
                {
                    parts.Add(AgentAssistantResponsePart.CreateToolCall(
                        content[toolStartIndex..],
                        Array.Empty<SkyweaverToolInvocation>(),
                        $"The <{toolElementName}> element is missing a closing </{toolElementName}> tag.",
                        toolCallIndex));
                    break;
                }

                var toolXml = content[toolStartIndex..toolEndIndex];
                try
                {
                    var invocations = _toolManager.ParseToolInvocationXml(toolXml);
                    parts.Add(AgentAssistantResponsePart.CreateToolCall(
                        toolXml,
                        invocations,
                        toolCallIndex: toolCallIndex));
                }
                catch (Exception ex) when (ex is XmlException or InvalidOperationException)
                {
                    parts.Add(AgentAssistantResponsePart.CreateToolCall(
                        toolXml,
                        Array.Empty<SkyweaverToolInvocation>(),
                        ex.Message,
                        toolCallIndex));
                }

                index = toolEndIndex;
            }

            return new AgentAssistantResponse(rawContent ?? string.Empty, parts.ToArray());
        }

        private static void AddNaturalLanguagePart(
            ICollection<AgentAssistantResponsePart> parts,
            string content)
        {
            var normalizedContent = content.Trim();
            if (normalizedContent.Length == 0)
            {
                return;
            }

            parts.Add(AgentAssistantResponsePart.CreateNaturalLanguage(normalizedContent));
        }

        private enum GemmaThoughtDetectionState
        {
            NotGemma = 0,
            Pending = 1,
            Gemma = 2
        }

        private static string PrepareAssistantProtocolContent(
            string? rawContent,
            bool enableGemmaThoughtCompatibility,
            bool isFinal)
        {
            if (string.IsNullOrEmpty(rawContent))
            {
                return string.Empty;
            }

            var isPendingGemmaDetection = false;
            if (enableGemmaThoughtCompatibility &&
                TryExtractGemmaThoughtContent(
                    rawContent,
                    isFinal,
                    out var extraction,
                    out isPendingGemmaDetection))
            {
                return extraction.ProtocolContent;
            }

            return isPendingGemmaDetection ? string.Empty : rawContent;
        }

        private static bool TryExtractGemmaThoughtContent(
            string rawContent,
            bool isFinal,
            out GemmaThoughtExtraction extraction,
            out bool isPendingGemmaDetection)
        {
            extraction = GemmaThoughtExtraction.Empty;
            isPendingGemmaDetection = false;

            var detectionState = DetectGemmaThoughtOpening(
                rawContent,
                isFinal,
                out var openingTagStartIndex,
                out var openingTagEndIndex);
            if (detectionState == GemmaThoughtDetectionState.Pending)
            {
                isPendingGemmaDetection = true;
                return false;
            }

            if (detectionState != GemmaThoughtDetectionState.Gemma)
            {
                return false;
            }

            extraction = ExtractGemmaThoughtContent(
                rawContent,
                openingTagStartIndex,
                openingTagEndIndex,
                isFinal);
            return true;
        }

        private static GemmaThoughtDetectionState DetectGemmaThoughtOpening(
            string rawContent,
            bool isFinal,
            out int openingTagStartIndex,
            out int openingTagEndIndex)
        {
            openingTagStartIndex = 0;
            openingTagEndIndex = 0;

            while (openingTagStartIndex < rawContent.Length &&
                   char.IsWhiteSpace(rawContent[openingTagStartIndex]))
            {
                openingTagStartIndex++;
            }

            if (openingTagStartIndex >= rawContent.Length)
            {
                return isFinal ? GemmaThoughtDetectionState.NotGemma : GemmaThoughtDetectionState.Pending;
            }

            var candidate = rawContent[openingTagStartIndex..];
            if (!IsPotentialInitialThoughtTag(candidate))
            {
                return GemmaThoughtDetectionState.NotGemma;
            }

            if (!IsOpeningTagAt(rawContent, "thought", openingTagStartIndex))
            {
                return isFinal ? GemmaThoughtDetectionState.NotGemma : GemmaThoughtDetectionState.Pending;
            }

            openingTagEndIndex = FindTagEnd(rawContent, openingTagStartIndex);
            if (openingTagEndIndex < 0)
            {
                return isFinal ? GemmaThoughtDetectionState.NotGemma : GemmaThoughtDetectionState.Pending;
            }

            return GemmaThoughtDetectionState.Gemma;
        }

        private static bool IsPotentialInitialThoughtTag(string text)
        {
            const string openingTagPrefix = "<thought";

            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            if (text.Length <= openingTagPrefix.Length)
            {
                return openingTagPrefix.StartsWith(text, StringComparison.OrdinalIgnoreCase);
            }

            return text.StartsWith(openingTagPrefix, StringComparison.OrdinalIgnoreCase) &&
                   IsXmlNameBoundary(text[openingTagPrefix.Length]);
        }

        private static GemmaThoughtExtraction ExtractGemmaThoughtContent(
            string rawContent,
            int openingTagStartIndex,
            int openingTagEndIndex,
            bool isFinal)
        {
            var protocolBuilder = new StringBuilder(rawContent.Length);
            var thoughtBuilder = new StringBuilder();
            var thoughtSegments = new List<GemmaThoughtSegment>();
            var nextThoughtSegmentIndex = 1;
            var openingTag = rawContent[openingTagStartIndex..openingTagEndIndex];
            var insideThought = !IsSelfClosingTag(openingTag);
            var index = openingTagEndIndex;

            if (!insideThought)
            {
                AppendThoughtBoundaryNewline(protocolBuilder);
            }

            while (index < rawContent.Length)
            {
                var nextTagIndex = FindNextGemmaRelevantTagIndex(rawContent, index);
                if (nextTagIndex < 0)
                {
                    var tail = rawContent[index..];
                    if (!isFinal)
                    {
                        tail = TrimTrailingPotentialGemmaTagPrefix(tail);
                    }

                    AppendGemmaSegment(protocolBuilder, thoughtBuilder, insideThought, tail);
                    break;
                }

                if (nextTagIndex > index)
                {
                    AppendGemmaSegment(
                        protocolBuilder,
                        thoughtBuilder,
                        insideThought,
                        rawContent[index..nextTagIndex]);
                }

                if (FindNextToolTagIndex(rawContent, nextTagIndex, out var toolElementName) == nextTagIndex)
                {
                    var toolEndIndex = ResolveToolElementEnd(rawContent, nextTagIndex, toolElementName, isFinal);
                    if (toolEndIndex < 0)
                    {
                        protocolBuilder.Append(rawContent, nextTagIndex, rawContent.Length - nextTagIndex);
                        break;
                    }

                    protocolBuilder.Append(rawContent, nextTagIndex, toolEndIndex - nextTagIndex);
                    index = toolEndIndex;
                    continue;
                }

                if (IsClosingTagAt(rawContent, "thought", nextTagIndex))
                {
                    var tagEndIndex = FindTagEnd(rawContent, nextTagIndex);
                    if (tagEndIndex < 0)
                    {
                        break;
                    }

                    if (insideThought)
                    {
                        FlushGemmaThoughtSegment(
                            thoughtBuilder,
                            thoughtSegments,
                            ref nextThoughtSegmentIndex);
                        insideThought = false;
                        AppendThoughtBoundaryNewline(protocolBuilder);
                    }

                    index = tagEndIndex;
                    continue;
                }

                if (IsOpeningTagAt(rawContent, "thought", nextTagIndex))
                {
                    var tagEndIndex = FindTagEnd(rawContent, nextTagIndex);
                    if (tagEndIndex < 0)
                    {
                        break;
                    }

                    if (insideThought)
                    {
                        index = tagEndIndex;
                        continue;
                    }

                    insideThought = true;

                    if (IsSelfClosingTag(rawContent[nextTagIndex..tagEndIndex]))
                    {
                        insideThought = false;
                        AppendThoughtBoundaryNewline(protocolBuilder);
                    }

                    index = tagEndIndex;
                    continue;
                }

                AppendGemmaSegment(
                    protocolBuilder,
                    thoughtBuilder,
                    insideThought,
                    rawContent[nextTagIndex].ToString());
                index = nextTagIndex + 1;
            }

            FlushGemmaThoughtSegment(
                thoughtBuilder,
                thoughtSegments,
                ref nextThoughtSegmentIndex);

            return new GemmaThoughtExtraction(
                protocolBuilder.ToString(),
                thoughtSegments.ToArray());
        }

        private static void AppendGemmaSegment(
            StringBuilder protocolBuilder,
            StringBuilder thoughtBuilder,
            bool insideThought,
            string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                return;
            }

            if (insideThought)
            {
                thoughtBuilder.Append(segment);
            }
            else
            {
                protocolBuilder.Append(segment);
            }
        }

        private static void FlushGemmaThoughtSegment(
            StringBuilder thoughtBuilder,
            ICollection<GemmaThoughtSegment> thoughtSegments,
            ref int nextThoughtSegmentIndex)
        {
            var content = thoughtBuilder.ToString().Trim();
            thoughtBuilder.Clear();
            if (content.Length == 0)
            {
                return;
            }

            thoughtSegments.Add(new GemmaThoughtSegment(nextThoughtSegmentIndex, content));
            nextThoughtSegmentIndex++;
        }

        private static void AppendThoughtBoundaryNewline(StringBuilder builder)
        {
            if (builder.Length == 0)
            {
                builder.Append(Environment.NewLine);
                return;
            }

            var lastCharacter = builder[^1];
            if (lastCharacter is not ('\r' or '\n'))
            {
                builder.Append(Environment.NewLine);
            }
        }

        private static string TrimTrailingPotentialGemmaTagPrefix(string text)
        {
            var trailingPrefixLength = Math.Max(
                Math.Max(
                    GetTrailingPotentialTagPrefixLength(text, "<Tool"),
                    GetTrailingPotentialTagPrefixLength(text, "<ToolAsync")),
                Math.Max(
                    Math.Max(
                        GetTrailingPotentialTagPrefixLength(text, "</Tool"),
                        GetTrailingPotentialTagPrefixLength(text, "</ToolAsync")),
                    Math.Max(
                        GetTrailingPotentialTagPrefixLength(text, "<thought"),
                        GetTrailingPotentialTagPrefixLength(text, "</thought"))));
            return trailingPrefixLength <= 0
                ? text
                : text[..(text.Length - trailingPrefixLength)];
        }

        private static int FindNextGemmaRelevantTagIndex(string text, int startIndex)
        {
            return MinNonNegative(
                FindNextToolTagIndex(text, startIndex, out _),
                IndexOfOpeningTag(text, "thought", startIndex),
                IndexOfClosingTagStart(text, "thought", startIndex));
        }

        private static int MinNonNegative(params int[] values)
        {
            var minimum = -1;
            foreach (var value in values)
            {
                if (value < 0)
                {
                    continue;
                }

                minimum = minimum < 0 ? value : Math.Min(minimum, value);
            }

            return minimum;
        }

        private static int ResolveToolElementEnd(
            string text,
            int toolStartIndex,
            string toolElementName,
            bool isFinal)
        {
            var toolOpenTagEndIndex = FindTagEnd(text, toolStartIndex);
            if (toolOpenTagEndIndex < 0)
            {
                return isFinal ? text.Length : -1;
            }

            if (IsSelfClosingTag(text[toolStartIndex..toolOpenTagEndIndex]))
            {
                return toolOpenTagEndIndex;
            }

            var toolEndIndex = IndexOfClosingElementEnd(text, toolElementName, toolOpenTagEndIndex);
            return toolEndIndex < 0
                ? isFinal ? text.Length : -1
                : toolEndIndex;
        }

        private static bool IsOpeningTagAt(string text, string elementName, int index)
        {
            return IndexOfOpeningTag(text, elementName, index) == index;
        }

        private static bool IsClosingTagAt(string text, string elementName, int index)
        {
            return IndexOfClosingTagStart(text, elementName, index) == index;
        }

        private static string ExtractVisibleAssistantText(string? rawContent, bool isFinal)
        {
            return ExtractVisibleAssistantTextCore(rawContent, isFinal);
        }

        private static string ExtractVisibleAssistantText(
            string? rawContent,
            bool isFinal,
            bool enableGemmaThoughtCompatibility)
        {
            return ExtractVisibleAssistantTextCore(
                PrepareAssistantProtocolContent(
                    rawContent,
                    enableGemmaThoughtCompatibility,
                    isFinal),
                isFinal);
        }

        private static string ExtractVisibleAssistantTextCore(string? rawContent, bool isFinal)
        {
            if (string.IsNullOrEmpty(rawContent))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(rawContent.Length);
            var index = 0;

            while (index < rawContent.Length)
            {
                var toolStartIndex = FindNextToolTagIndex(rawContent, index, out var toolElementName);
                if (toolStartIndex < 0)
                {
                    var visibleTail = rawContent[index..];
                    if (!isFinal)
                    {
                        var trailingPrefixLength = Math.Max(
                            GetTrailingPotentialTagPrefixLength(visibleTail, "<Tool"),
                            GetTrailingPotentialTagPrefixLength(visibleTail, "<ToolAsync"));
                        visibleTail = visibleTail[..(visibleTail.Length - trailingPrefixLength)];
                    }

                    AppendVisibleAssistantTextSegment(builder, visibleTail, trimTrailing: false);
                    break;
                }

                if (toolStartIndex > index)
                {
                    AppendVisibleAssistantTextSegment(
                        builder,
                        rawContent[index..toolStartIndex],
                        trimTrailing: true);
                }

                var toolOpenTagEndIndex = FindTagEnd(rawContent, toolStartIndex);
                if (toolOpenTagEndIndex < 0)
                {
                    break;
                }

                var toolEndIndex = IsSelfClosingTag(rawContent[toolStartIndex..toolOpenTagEndIndex])
                    ? toolOpenTagEndIndex
                    : IndexOfClosingElementEnd(rawContent, toolElementName, toolOpenTagEndIndex);
                if (toolEndIndex < 0)
                {
                    break;
                }

                index = toolEndIndex;
            }

            return builder.ToString();
        }

        private static void AppendVisibleAssistantTextSegment(
            StringBuilder builder,
            string segment,
            bool trimTrailing)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return;
            }

            var normalizedSegment = builder.Length == 0
                ? segment.TrimStart()
                : segment;
            if (trimTrailing)
            {
                normalizedSegment = normalizedSegment.TrimEnd();
            }

            if (normalizedSegment.Length > 0)
            {
                builder.Append(normalizedSegment);
            }
        }

        private static int GetTrailingPotentialTagPrefixLength(string text, string prefix)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var maxLength = Math.Min(text.Length, prefix.Length - 1);
            for (var length = maxLength; length > 0; length--)
            {
                if (text.EndsWith(prefix[..length], StringComparison.OrdinalIgnoreCase))
                {
                    return length;
                }
            }

            return 0;
        }

        private static string AppendToolParseErrorHint(string errorMessage)
        {
            var normalizedErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? "Tool call could not be parsed."
                : errorMessage.Trim();

            const string hint = "请检查是否在回复文本中出现未转义的工具调用标签。";
            return normalizedErrorMessage.Contains(hint, StringComparison.Ordinal)
                ? normalizedErrorMessage
                : $"{normalizedErrorMessage} {hint}";
        }

        private static int FindNextToolTagIndex(
            string text,
            int startIndex,
            out string toolElementName)
        {
            var asyncTagIndex = IndexOfOpeningTag(text, AsyncToolTagName, startIndex);
            var syncTagIndex = IndexOfOpeningTag(text, SyncToolTagName, startIndex);

            if (asyncTagIndex < 0 && syncTagIndex < 0)
            {
                toolElementName = string.Empty;
                return -1;
            }

            if (syncTagIndex < 0 || (asyncTagIndex >= 0 && asyncTagIndex < syncTagIndex))
            {
                toolElementName = AsyncToolTagName;
                return asyncTagIndex;
            }

            toolElementName = SyncToolTagName;
            return syncTagIndex;
        }

        private static int IndexOfOpeningTag(string text, string elementName, int startIndex = 0)
        {
            var needle = $"<{elementName}";
            var searchIndex = Math.Max(0, startIndex);

            while (searchIndex < text.Length)
            {
                var matchIndex = text.IndexOf(needle, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return -1;
                }

                var nameEndIndex = matchIndex + needle.Length;
                if (nameEndIndex >= text.Length || IsXmlNameBoundary(text[nameEndIndex]))
                {
                    return matchIndex;
                }

                searchIndex = matchIndex + 1;
            }

            return -1;
        }

        private static int IndexOfClosingTagStart(string text, string elementName, int startIndex = 0)
        {
            var needle = $"</{elementName}";
            var searchIndex = Math.Max(0, startIndex);

            while (searchIndex < text.Length)
            {
                var matchIndex = text.IndexOf(needle, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return -1;
                }

                var nameEndIndex = matchIndex + needle.Length;
                if (nameEndIndex >= text.Length || IsXmlNameBoundary(text[nameEndIndex]))
                {
                    return matchIndex;
                }

                searchIndex = matchIndex + 1;
            }

            return -1;
        }

        private static int IndexOfClosingElementEnd(string text, string elementName, int startIndex = 0)
        {
            var closingTagStartIndex = IndexOfClosingTagStart(text, elementName, startIndex);
            return closingTagStartIndex < 0
                ? -1
                : FindTagEnd(text, closingTagStartIndex);
        }

        private static int FindTagEnd(string text, int tagStartIndex)
        {
            if (tagStartIndex < 0)
            {
                return -1;
            }

            var insideDoubleQuotes = false;
            var insideSingleQuotes = false;
            for (var index = tagStartIndex + 1; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '"' when !insideSingleQuotes:
                        insideDoubleQuotes = !insideDoubleQuotes;
                        break;
                    case '\'' when !insideDoubleQuotes:
                        insideSingleQuotes = !insideSingleQuotes;
                        break;
                    case '>' when !insideDoubleQuotes && !insideSingleQuotes:
                        return index + 1;
                }
            }

            return -1;
        }

        private static bool IsSelfClosingTag(string rawTag)
        {
            return rawTag.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
        }

        private static bool IsXmlNameBoundary(char character)
        {
            return char.IsWhiteSpace(character) || character is '>' or '/' or '?';
        }

        private static bool IsPassdown(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.Passdown, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPassToMainAgent(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.PassToMainAgent, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWaitForAsyncTools(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.WaitForAsyncTools, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGetAsyncToolProgress(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.GetAsyncToolProgress, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLoadToolKits(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.LoadToolKits, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompactToolCalls(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.CompactToolCalls, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRetrieveCompactedToolCalls(string toolName)
        {
            return string.Equals(toolName, SkyweaverBuiltInToolNames.RetrieveCompactedToolCalls, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> ExtractToolCallIdsArgument(SkyweaverToolInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);

            string? rawValue = null;
            foreach (var key in new[]
                     {
                         SkyweaverBuiltInToolNames.CompactionToolCallIdsParameter,
                         "ToolCallIds",
                         "ToolCallId",
                         "ToolCallID"
                     })
            {
                if (invocation.RawArguments.TryGetValue(key, out rawValue) &&
                    !string.IsNullOrWhiteSpace(rawValue))
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return Array.Empty<string>();
            }

            try
            {
                var token = JToken.Parse(rawValue);
                return ExtractRequestedToolCallIds(token);
            }
            catch (Exception ex) when (ex is FormatException or Newtonsoft.Json.JsonReaderException)
            {
                var normalized = ChatSessionToolCallIdGenerator.Normalize(rawValue);
                return normalized.Length == 0 ? Array.Empty<string>() : [normalized];
            }
        }

        private static SkyweaverToolContext CreateToolProgressContext(
            SkyweaverToolContext runtimeToolContext,
            SkyweaverToolInvocation invocation,
            int iterationNumber,
            int partIndex,
            int toolCallIndex,
            string toolCallId,
            string? modelId,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            Action<SkyweaverToolProgressUpdate>? progressSink = null,
            Func<bool>? canPublishProgress = null)
        {
            if (onEventAsync == null && progressSink == null)
            {
                return runtimeToolContext;
            }

            return runtimeToolContext.WithToolProgressReporter(async (progress, cancellationToken) =>
            {
                var normalizedProgress = progress.Normalize();
                try
                {
                    progressSink?.Invoke(normalizedProgress);
                }
                catch
                {
                    // Progress bookkeeping must not fail the tool itself.
                }

                if (onEventAsync == null ||
                    canPublishProgress?.Invoke() == false)
                {
                    return;
                }

                try
                {
                    await PublishAsync(
                        onEventAsync,
                        new AgentLoopRuntimeEvent
                        {
                            Kind = AgentLoopRuntimeEventKind.ToolProgressUpdated,
                            IterationNumber = iterationNumber,
                            ModelId = modelId,
                            PartIndex = partIndex,
                            ToolCallIndex = toolCallIndex,
                            ToolCallId = toolCallId,
                            ToolInvocation = invocation,
                            ToolProgress = normalizedProgress
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Progress rendering must not fail the tool itself.
                }
            });
        }

        private async Task<IReadOnlyList<SkyweaverToolReturnPayload>> ExecuteAuthorizedInvocationsAsync(
            AgentLoopRequest request,
            IReadOnlyList<SkyweaverToolInvocation> invocations,
            SkyweaverToolContext runtimeToolContext,
            IReadOnlyDictionary<string, IReadOnlyList<string>> toolKitMembershipMap,
            IReadOnlyCollection<string> activeToolKitKeys,
            int iterationNumber,
            int partIndex,
            int toolCallIndex,
            string toolCallId,
            string? modelId,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var toolReturns = new List<SkyweaverToolReturnPayload>(invocations.Count);

            foreach (var invocation in invocations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var authorization = await AuthorizeToolInvocationAsync(
                    request,
                    invocation,
                    runtimeToolContext,
                    toolKitMembershipMap,
                    activeToolKitKeys,
                    iterationNumber,
                    partIndex,
                    toolCallIndex,
                    cancellationToken).ConfigureAwait(false);

                if (!authorization.CanExecute)
                {
                    toolReturns.Add(_toolManager.CreateErrorToolReturnPayload(
                        invocation.ToolName,
                        authorization.ErrorMessage ?? $"Tool '{invocation.ToolName}' cannot be executed.",
                        toolCallId));
                    continue;
                }

                try
                {
                    var progressToolContext = CreateToolProgressContext(
                        runtimeToolContext,
                        invocation,
                        iterationNumber,
                        partIndex,
                        toolCallIndex,
                        toolCallId,
                        modelId,
                        onEventAsync);
                    var result = await _toolManager.ExecuteAsync(
                        invocation.ToolName,
                        invocation.RawArguments,
                        progressToolContext,
                        request.Agent,
                        authorization.HasHostConfirmation,
                        cancellationToken).ConfigureAwait(false);
                    toolReturns.Add(_toolManager.CreateToolReturnPayload(invocation.ToolName, result, toolCallId));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    toolReturns.Add(_toolManager.CreateErrorToolReturnPayload(
                        invocation.ToolName,
                        $"Tool execution failed: {ex.Message}",
                        toolCallId));
                }
            }

            return toolReturns;
        }

        private async Task<AgentToolBackfill> ExecuteAsyncToolInvocationAsync(
            AgentLoopRequest request,
            SkyweaverToolInvocation invocation,
            SkyweaverToolContext runtimeToolContext,
            IReadOnlyDictionary<string, IReadOnlyList<string>> toolKitMembershipMap,
            IReadOnlyCollection<string> activeToolKitKeys,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById,
            AsyncToolPublicationGate asyncToolPublicationGate,
            string toolCallId,
            int iterationNumber,
            int partIndex,
            int toolCallIndex,
            string? modelId,
            Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var authorization = await AuthorizeToolInvocationAsync(
                request,
                invocation,
                runtimeToolContext,
                toolKitMembershipMap,
                activeToolKitKeys,
                iterationNumber,
                partIndex,
                toolCallIndex,
                cancellationToken).ConfigureAwait(false);

            if (!authorization.CanExecute)
            {
                var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                    invocation.ToolName,
                    authorization.ErrorMessage ?? $"Tool '{invocation.ToolName}' cannot be executed.",
                    toolCallId);
                completedAsyncToolResultsById[toolCallId] = failurePayload;
                completedAsyncToolProgressById[toolCallId] = new AsyncToolProgressSnapshot(
                    toolCallId,
                    invocation.ToolName,
                    IsPending: false,
                    IsCompleted: true,
                    Version: 0,
                    ObservedAtUtc: null,
                    Progress: null);
                return CreateBackfill(partIndex, toolCallIndex, [failurePayload], toolCallId);
            }

            try
            {
                var progressTracker = new AsyncToolProgressTracker();
                var scopeKey = ResolveAsyncToolScopeKey(request);
                var progressToolContext = CreateToolProgressContext(
                    runtimeToolContext,
                    invocation,
                    iterationNumber,
                    partIndex,
                    toolCallIndex,
                    toolCallId,
                    modelId,
                    onEventAsync,
                    progressTracker.Update,
                    () => asyncToolPublicationGate.IsOpen);
                PersistAsyncToolInvocation(request, toolCallId, invocation.InvocationXml);
                var executionTask = _toolManager.ExecuteAsync(
                    invocation.ToolName,
                    invocation.RawArguments,
                    progressToolContext,
                    request.Agent,
                    authorization.HasHostConfirmation,
                    CancellationToken.None);

                var execution = new PendingAsyncToolExecution(
                    toolCallId,
                    partIndex,
                    toolCallIndex,
                    invocation,
                    executionTask,
                    progressTracker);
                pendingAsyncToolExecutions.Add(execution);
                s_asyncToolRegistry.Track(scopeKey, execution);
                _ = ObserveAsyncToolCompletionAsync(
                    request,
                    scopeKey,
                    execution,
                    cancellationToken);

                var acknowledgmentPayload = _toolManager.CreateToolReturnPayload(
                    invocation.ToolName,
                    SkyweaverToolResult.Success(
                        $"Async tool call accepted. ToolCallId={toolCallId}.",
                        new Dictionary<string, object?>
                        {
                            ["asyncAcknowledgement"] = true,
                            ["toolCallId"] = toolCallId,
                            ["toolName"] = invocation.ToolName
                        }),
                    toolCallId);
                return CreateBackfill(partIndex, toolCallIndex, [acknowledgmentPayload], toolCallId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failurePayload = _toolManager.CreateErrorToolReturnPayload(
                    invocation.ToolName,
                    $"Async tool scheduling failed: {ex.Message}",
                    toolCallId);
                completedAsyncToolResultsById[toolCallId] = failurePayload;
                completedAsyncToolProgressById[toolCallId] = new AsyncToolProgressSnapshot(
                    toolCallId,
                    invocation.ToolName,
                    IsPending: false,
                    IsCompleted: true,
                    Version: 0,
                    ObservedAtUtc: null,
                    Progress: null);
                return CreateBackfill(partIndex, toolCallIndex, [failurePayload], toolCallId);
            }
        }

        private async Task ObserveAsyncToolCompletionAsync(
            AgentLoopRequest request,
            string scopeKey,
            PendingAsyncToolExecution execution,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(execution);

            SkyweaverToolReturnPayload payload;
            try
            {
                var result = await execution.ExecutionTask.ConfigureAwait(false);
                payload = _toolManager.CreateToolReturnPayload(
                    execution.Invocation.ToolName,
                    result,
                    execution.ToolCallId);
            }
            catch (OperationCanceledException)
            {
                payload = _toolManager.CreateErrorToolReturnPayload(
                    execution.Invocation.ToolName,
                    "Async tool execution was cancelled.",
                    execution.ToolCallId);
            }
            catch (Exception ex)
            {
                payload = _toolManager.CreateErrorToolReturnPayload(
                    execution.Invocation.ToolName,
                    $"Tool execution failed: {ex.Message}",
                    execution.ToolCallId);
            }

            try
            {
                var progressSnapshot = execution.ProgressTracker.CreateSnapshot(
                    execution.ToolCallId,
                    execution.Invocation.ToolName,
                    isPending: false,
                    isCompleted: true);
                s_asyncToolRegistry.MarkCompleted(
                    scopeKey,
                    execution,
                    payload,
                    progressSnapshot,
                    deliveredToConversation: false);
                PersistAsyncToolOutput(
                    request.ToolCallResourceFolderPath,
                    execution.ToolCallId,
                    request.Agent.AgentId,
                    _toolManager.BuildToolsReturnXml([payload]),
                    transcriptDelivered: false);
            }
            catch
            {
                // Background completion bookkeeping must not fault the process.
            }
        }

        private void PersistAsyncToolInvocation(
            AgentLoopRequest request,
            string toolCallId,
            string invocationXml)
        {
            if (string.IsNullOrWhiteSpace(request.ToolCallResourceFolderPath))
            {
                return;
            }

            try
            {
                _toolCallResourceStore.SaveInvocation(
                    request.ToolCallResourceFolderPath,
                    toolCallId,
                    request.Agent.AgentId,
                    AgentLoopCompactionStore.EnsureToolCallIdInToolInvocationXml(invocationXml, toolCallId));
            }
            catch
            {
                // The transcript remains authoritative if the sidecar write fails.
            }
        }

        private void PersistAsyncToolOutput(
            string? toolCallResourceFolderPath,
            string toolCallId,
            string? callerAgentId,
            string outputXml,
            bool transcriptDelivered)
        {
            if (string.IsNullOrWhiteSpace(toolCallResourceFolderPath))
            {
                return;
            }

            try
            {
                _toolCallResourceStore.SaveOutput(
                    toolCallResourceFolderPath,
                    toolCallId,
                    callerAgentId,
                    AgentLoopCompactionStore.EnsureToolCallIdInToolsReturnXml(outputXml, toolCallId),
                    transcriptDelivered);
            }
            catch
            {
                // Async result sidecar persistence is best-effort.
            }
        }

        private async Task<SkyweaverToolResult> ExecuteWaitForAsyncToolsAsync(
            AgentLoopRequest request,
            SkyweaverToolInvocation invocation,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById,
            CancellationToken cancellationToken)
        {
            var registration = _toolManager.GetRegisteredTools(resolveIcons: false).FirstOrDefault(item =>
                string.Equals(item.Definition.Name, invocation.ToolName, StringComparison.OrdinalIgnoreCase));

            if (registration == null)
            {
                return SkyweaverToolResult.Failure($"Tool not found: {invocation.ToolName}");
            }

            var arguments = SkyweaverToolArguments.Bind(registration.Definition.Parameters, invocation.RawArguments);
            var requestedToolCallIds = ExtractRequestedToolCallIds(
                arguments.GetJson(SkyweaverBuiltInToolNames.WaitForAsyncToolsParameter));
            if (requestedToolCallIds.Count == 0)
            {
                return SkyweaverToolResult.Failure(
                    "WaitForAsyncTools requires at least one ToolCallId.");
            }

            var waitedToolCallIds = await WaitForAsyncToolCallsAsync(
                request,
                requestedToolCallIds,
                pendingAsyncToolExecutions,
                completedAsyncToolResultsById,
                cancellationToken).ConfigureAwait(false);
            var progressSnapshots = ResolveAsyncToolProgressSnapshots(
                request,
                waitedToolCallIds,
                pendingAsyncToolExecutions,
                completedAsyncToolResultsById,
                completedAsyncToolProgressById);

            var summary = waitedToolCallIds.Count == 1
                ? $"Waited for async tool {waitedToolCallIds[0]} to complete."
                : $"Waited for {waitedToolCallIds.Count} async tools to complete.";

            return SkyweaverToolResult.Success(
                summary,
                new Dictionary<string, object?>
                {
                    ["requestedToolCallIds"] = new JArray(requestedToolCallIds),
                    ["completedToolCallIds"] = new JArray(waitedToolCallIds),
                    ["progressSnapshots"] = BuildAsyncToolProgressSnapshotJson(progressSnapshots)
                });
        }

        private SkyweaverToolResult ExecuteGetAsyncToolProgress(
            AgentLoopRequest request,
            SkyweaverToolInvocation invocation,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById)
        {
            var registration = _toolManager.GetRegisteredTools(resolveIcons: false).FirstOrDefault(item =>
                string.Equals(item.Definition.Name, invocation.ToolName, StringComparison.OrdinalIgnoreCase));

            if (registration == null)
            {
                return SkyweaverToolResult.Failure($"Tool not found: {invocation.ToolName}");
            }

            var arguments = SkyweaverToolArguments.Bind(registration.Definition.Parameters, invocation.RawArguments);
            var requestedToolCallIds = ExtractRequestedToolCallIds(
                arguments.GetJson(SkyweaverBuiltInToolNames.GetAsyncToolProgressParameter));
            if (requestedToolCallIds.Count == 0)
            {
                return SkyweaverToolResult.Failure(
                    "GetAsyncToolProgress requires at least one ToolCallId.");
            }

            var scopeKey = ResolveAsyncToolScopeKey(request);
            var missingToolCallIds = requestedToolCallIds
                .Where(toolCallId =>
                    !completedAsyncToolResultsById.ContainsKey(toolCallId) &&
                    !completedAsyncToolProgressById.ContainsKey(toolCallId) &&
                    !pendingAsyncToolExecutions.Any(execution =>
                        string.Equals(execution.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase)) &&
                    !s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out _) &&
                    !TryGetKnownAsyncToolRecord(request, toolCallId, out _, requireOutput: false))
                .ToArray();
            if (missingToolCallIds.Length > 0)
            {
                return SkyweaverToolResult.Failure(
                    $"Unknown async tool call id(s): {string.Join(", ", missingToolCallIds)}",
                    new Dictionary<string, object?>
                    {
                        ["requestedToolCallIds"] = new JArray(requestedToolCallIds),
                        ["missingToolCallIds"] = new JArray(missingToolCallIds)
                    });
            }

            var progressSnapshots = ResolveAsyncToolProgressSnapshots(
                request,
                requestedToolCallIds,
                pendingAsyncToolExecutions,
                completedAsyncToolResultsById,
                completedAsyncToolProgressById);
            var content = BuildAsyncToolProgressContent(progressSnapshots);
            return SkyweaverToolResult.Success(
                content,
                new Dictionary<string, object?>
                {
                    ["requestedToolCallIds"] = new JArray(requestedToolCallIds),
                    ["progressSnapshots"] = BuildAsyncToolProgressSnapshotJson(progressSnapshots)
                });
        }

        private IReadOnlyList<AsyncToolProgressSnapshot> ResolveAsyncToolProgressSnapshots(
            AgentLoopRequest request,
            IReadOnlyList<string> requestedToolCallIds,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            IDictionary<string, AsyncToolProgressSnapshot> completedAsyncToolProgressById)
        {
            var scopeKey = ResolveAsyncToolScopeKey(request);
            var snapshots = new List<AsyncToolProgressSnapshot>(requestedToolCallIds.Count);
            foreach (var toolCallId in requestedToolCallIds)
            {
                var pendingExecution = pendingAsyncToolExecutions.FirstOrDefault(execution =>
                    string.Equals(execution.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase));
                if (pendingExecution != null)
                {
                    var isCompleted = pendingExecution.ExecutionTask.IsCompleted;
                    snapshots.Add(pendingExecution.ProgressTracker.CreateSnapshot(
                        pendingExecution.ToolCallId,
                        pendingExecution.Invocation.ToolName,
                        isPending: !isCompleted,
                        isCompleted: isCompleted));
                    continue;
                }

                if (s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out var registryExecution))
                {
                    var isCompleted = registryExecution.ExecutionTask.IsCompleted;
                    snapshots.Add(registryExecution.ProgressTracker.CreateSnapshot(
                        registryExecution.ToolCallId,
                        registryExecution.Invocation.ToolName,
                        isPending: !isCompleted,
                        isCompleted: isCompleted));
                    continue;
                }

                if (completedAsyncToolProgressById.TryGetValue(toolCallId, out var completedSnapshot))
                {
                    snapshots.Add(completedSnapshot);
                    continue;
                }

                if (completedAsyncToolResultsById.TryGetValue(toolCallId, out var completedResult))
                {
                    snapshots.Add(new AsyncToolProgressSnapshot(
                        toolCallId,
                        completedResult.ToolName,
                        IsPending: false,
                        IsCompleted: true,
                        Version: 0,
                        ObservedAtUtc: null,
                        Progress: null));
                    continue;
                }

                if (TryGetKnownAsyncToolRecord(request, toolCallId, out var persistedRecord, requireOutput: false))
                {
                    var toolName = TryParsePersistedAsyncInvocation(persistedRecord.InvocationXml, out var persistedInvocation)
                        ? persistedInvocation!.ToolName
                        : string.Empty;
                    snapshots.Add(new AsyncToolProgressSnapshot(
                        toolCallId,
                        toolName,
                        IsPending: string.IsNullOrWhiteSpace(persistedRecord.OutputXml),
                        IsCompleted: !string.IsNullOrWhiteSpace(persistedRecord.OutputXml),
                        Version: 0,
                        ObservedAtUtc: null,
                        Progress: null));
                }
            }

            return snapshots;
        }

        private static string BuildAsyncToolProgressContent(IReadOnlyList<AsyncToolProgressSnapshot> snapshots)
        {
            if (snapshots.Count == 0)
            {
                return "No async tool progress snapshots are available.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Async tool progress snapshots:");
            foreach (var snapshot in snapshots)
            {
                builder.Append("- ");
                builder.Append(snapshot.ToolCallId);
                if (!string.IsNullOrWhiteSpace(snapshot.ToolName))
                {
                    builder.Append(" (");
                    builder.Append(snapshot.ToolName);
                    builder.Append(')');
                }

                builder.Append(": ");
                builder.Append(snapshot.IsCompleted ? "completed" : snapshot.IsPending ? "running" : "accepted");
                builder.Append("; ");
                builder.Append(FormatAsyncToolProgress(snapshot));
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static string FormatAsyncToolProgress(AsyncToolProgressSnapshot snapshot)
        {
            var progress = snapshot.Progress;
            if (progress == null || snapshot.Version <= 0)
            {
                return "no progress reported yet";
            }

            var fragments = new List<string>();
            if (!string.IsNullOrWhiteSpace(progress.Phase))
            {
                fragments.Add($"phase={progress.Phase}");
            }

            if (!string.IsNullOrWhiteSpace(progress.StatusText))
            {
                fragments.Add($"status={progress.StatusText}");
            }

            if (progress.CompletedItems.HasValue || progress.TotalItems.HasValue)
            {
                fragments.Add($"items={progress.CompletedItems?.ToString() ?? "?"}/{progress.TotalItems?.ToString() ?? "?"}");
            }

            if (progress.ProgressFraction is double fraction)
            {
                fragments.Add($"fraction={Math.Round(fraction * 100d, 1)}%");
            }

            if (progress.ActiveItems.Count > 0)
            {
                fragments.Add($"active={string.Join(", ", progress.ActiveItems.Take(6))}");
            }

            fragments.Add($"version={snapshot.Version}");
            return string.Join("; ", fragments);
        }

        private static JArray BuildAsyncToolProgressSnapshotJson(
            IReadOnlyList<AsyncToolProgressSnapshot> snapshots)
        {
            return new JArray(snapshots.Select(CreateAsyncToolProgressSnapshotJson));
        }

        private static JObject CreateAsyncToolProgressSnapshotJson(AsyncToolProgressSnapshot snapshot)
        {
            var item = new JObject
            {
                ["toolCallId"] = snapshot.ToolCallId,
                ["toolName"] = snapshot.ToolName,
                ["isPending"] = snapshot.IsPending,
                ["isCompleted"] = snapshot.IsCompleted,
                ["progressVersion"] = snapshot.Version
            };

            if (snapshot.ObservedAtUtc is DateTimeOffset observedAtUtc)
            {
                item["observedAtUtc"] = observedAtUtc.ToString("O");
            }

            if (snapshot.Progress is not SkyweaverToolProgressUpdate progress)
            {
                return item;
            }

            item["phase"] = progress.Phase;
            item["statusText"] = progress.StatusText;
            item["completedItems"] = progress.CompletedItems.HasValue ? new JValue(progress.CompletedItems.Value) : JValue.CreateNull();
            item["totalItems"] = progress.TotalItems.HasValue ? new JValue(progress.TotalItems.Value) : JValue.CreateNull();
            item["progressFraction"] = progress.ProgressFraction.HasValue ? new JValue(progress.ProgressFraction.Value) : JValue.CreateNull();
            item["progressIsCompleted"] = progress.IsCompleted;
            item["activeItems"] = new JArray(progress.ActiveItems);
            return item;
        }

        private async Task<IReadOnlyList<string>> WaitForAsyncToolCallsAsync(
            AgentLoopRequest request,
            IReadOnlyCollection<string> requestedToolCallIds,
            IList<PendingAsyncToolExecution> pendingAsyncToolExecutions,
            IDictionary<string, SkyweaverToolReturnPayload> completedAsyncToolResultsById,
            CancellationToken cancellationToken)
        {
            var normalizedRequestedToolCallIds = requestedToolCallIds
                .Select(ChatSessionToolCallIdGenerator.Normalize)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var scopeKey = ResolveAsyncToolScopeKey(request);

            var missingToolCallIds = normalizedRequestedToolCallIds
                .Where(toolCallId =>
                    !completedAsyncToolResultsById.ContainsKey(toolCallId) &&
                    !pendingAsyncToolExecutions.Any(execution =>
                        string.Equals(execution.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase)) &&
                    !s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out _) &&
                    !TryGetKnownAsyncToolRecord(request, toolCallId, out _, requireOutput: false))
                .ToArray();
            if (missingToolCallIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Unknown async tool call id(s): {string.Join(", ", missingToolCallIds)}");
            }

            var orphanedToolCallIds = normalizedRequestedToolCallIds
                .Where(toolCallId =>
                    !completedAsyncToolResultsById.ContainsKey(toolCallId) &&
                    !pendingAsyncToolExecutions.Any(execution =>
                        string.Equals(execution.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase)) &&
                    !s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out _) &&
                    TryGetKnownAsyncToolRecord(request, toolCallId, out var record, requireOutput: false) &&
                    string.IsNullOrWhiteSpace(record.OutputXml))
                .ToArray();
            if (orphanedToolCallIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Async tool call id(s) are known but no live execution is attached: {string.Join(", ", orphanedToolCallIds)}");
            }

            var localPendingTasks = pendingAsyncToolExecutions
                .Where(execution => normalizedRequestedToolCallIds.Contains(execution.ToolCallId, StringComparer.OrdinalIgnoreCase))
                .Select(execution => execution.ExecutionTask);
            var registryPendingTasks = normalizedRequestedToolCallIds
                .Where(toolCallId =>
                    !pendingAsyncToolExecutions.Any(execution =>
                        string.Equals(execution.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase)) &&
                    s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out _))
                .Select(toolCallId =>
                    s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out var execution)
                        ? execution.ExecutionTask
                        : null)
                .Where(task => task != null)
                .Cast<Task<SkyweaverToolResult>>();
            var pendingTasks = localPendingTasks
                .Concat(registryPendingTasks)
                .Select(task => IgnoreToolTaskFaultsAsync(task, cancellationToken))
                .ToArray();

            if (pendingTasks.Length > 0)
            {
                await Task.WhenAll(pendingTasks).ConfigureAwait(false);
            }

            foreach (var toolCallId in normalizedRequestedToolCallIds)
            {
                if (s_asyncToolRegistry.TryGetExecution(scopeKey, toolCallId, out var execution) &&
                    execution.ExecutionTask.IsCompleted &&
                    !pendingAsyncToolExecutions.Any(localExecution =>
                        string.Equals(localExecution.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase)))
                {
                    await ObserveAsyncToolCompletionAsync(
                        request,
                        scopeKey,
                        execution,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            return normalizedRequestedToolCallIds;
        }

        private static async Task IgnoreToolTaskFaultsAsync(
            Task<SkyweaverToolResult> task,
            CancellationToken cancellationToken)
        {
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The async tool's real result will be backfilled separately.
            }
        }

        private static IReadOnlyList<string> ExtractRequestedToolCallIds(JToken? token)
        {
            if (token is JValue value && value.Type == JTokenType.String)
            {
                var normalized = ChatSessionToolCallIdGenerator.Normalize(value.Value<string>());
                return normalized.Length == 0 ? Array.Empty<string>() : [normalized];
            }

            if (token is not JArray array)
            {
                return Array.Empty<string>();
            }

            return array
                .Values<string>()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => ChatSessionToolCallIdGenerator.Normalize(item))
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ResolveToolCallId(
            int toolCallIndex,
            IDictionary<int, string> toolCallIdsByIndex,
            Func<string> toolCallIdFactory)
        {
            if (toolCallIdsByIndex.TryGetValue(toolCallIndex, out var existingId))
            {
                return existingId;
            }

            var resolvedToolCallId = ChatSessionToolCallIdGenerator.Normalize(toolCallIdFactory());
            if (resolvedToolCallId.Length == 0)
            {
                throw new InvalidOperationException("ToolCallIdFactory returned an empty tool call id.");
            }

            toolCallIdsByIndex[toolCallIndex] = resolvedToolCallId;
            return resolvedToolCallId;
        }

        private async Task<ToolExecutionAuthorization> AuthorizeToolInvocationAsync(
            AgentLoopRequest request,
            SkyweaverToolInvocation invocation,
            SkyweaverToolContext runtimeToolContext,
            IReadOnlyDictionary<string, IReadOnlyList<string>> toolKitMembershipMap,
            IReadOnlyCollection<string> activeToolKitKeys,
            int iterationNumber,
            int partIndex,
            int toolCallIndex,
            CancellationToken cancellationToken)
        {
            var registration = _toolManager.GetRegisteredTools(resolveIcons: false).FirstOrDefault(item =>
                string.Equals(item.Definition.Name, invocation.ToolName, StringComparison.OrdinalIgnoreCase));

            if (registration == null)
            {
                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Tool not found: {invocation.ToolName}");
            }

            if (!registration.IsEnabled)
            {
                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Tool '{invocation.ToolName}' is currently disabled.");
            }

            if (invocation.IsAsyncInvocation && !registration.Definition.SupportsAsyncInvocation)
            {
                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Tool '{invocation.ToolName}' does not support asynchronous invocation.");
            }

            if (registration.Definition.CanBelongToToolKit &&
                toolKitMembershipMap.TryGetValue(registration.Definition.Name, out var membershipKeys) &&
                membershipKeys.Count > 0 &&
                !membershipKeys.Any(activeToolKitKeys.Contains))
            {
                var toolKitNames = runtimeToolContext.AvailableToolKits
                    .Where(toolKit => membershipKeys.Contains(toolKit.Key, StringComparer.OrdinalIgnoreCase))
                    .Select(toolKit => string.IsNullOrWhiteSpace(toolKit.Name) ? toolKit.DisplayNameOrFallback : toolKit.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var toolKitHint = toolKitNames.Length == 0
                    ? $"Load the containing toolkit first with {SkyweaverBuiltInToolNames.LoadToolKits}."
                    : $"Load one of the containing toolkits first with {SkyweaverBuiltInToolNames.LoadToolKits}: {string.Join(", ", toolKitNames)}.";

                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Tool '{invocation.ToolName}' is gated behind a toolkit and is not currently loaded. {toolKitHint}");
            }

            var permissionDecision = AgentToolPermissionEvaluator.Resolve(request.Agent, registration);
            if (permissionDecision == AgentToolEffectiveDecision.Allowed)
            {
                return new ToolExecutionAuthorization(true, false, null);
            }

            if (permissionDecision == AgentToolEffectiveDecision.Denied)
            {
                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Tool '{invocation.ToolName}' is not available to agent '{request.Agent.AgentId}'.");
            }

            return await RequestToolConfirmationAsync(
                request,
                invocation,
                permissionDecision,
                iterationNumber,
                partIndex,
                toolCallIndex,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<ToolExecutionAuthorization> RequestToolConfirmationAsync(
            AgentLoopRequest request,
            SkyweaverToolInvocation invocation,
            AgentToolEffectiveDecision permissionDecision,
            int iterationNumber,
            int partIndex,
            int toolCallIndex,
            CancellationToken cancellationToken)
        {
            if (request.ToolConfirmationCallback == null)
            {
                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Tool '{invocation.ToolName}' requires host confirmation before execution, but the current runtime does not provide a confirmation callback.");
            }

            try
            {
                var confirmationResult = await request.ToolConfirmationCallback(
                    new AgentToolConfirmationRequest
                    {
                        Agent = request.Agent,
                        Invocation = invocation,
                        PermissionDecision = permissionDecision,
                        IterationNumber = iterationNumber,
                        PartIndex = partIndex,
                        ToolCallIndex = toolCallIndex
                    },
                    cancellationToken).ConfigureAwait(false);

                if (confirmationResult?.IsApproved == true)
                {
                    return new ToolExecutionAuthorization(true, true, null);
                }

                var rejectionMessage = confirmationResult == null || string.IsNullOrWhiteSpace(confirmationResult.RejectionMessage)
                    ? $"Tool '{invocation.ToolName}' was not approved by the host."
                    : confirmationResult.RejectionMessage.Trim();

                return new ToolExecutionAuthorization(false, false, rejectionMessage);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ToolExecutionAuthorization(
                    false,
                    false,
                    $"Host confirmation failed for tool '{invocation.ToolName}': {ex.Message}");
            }
        }

        private static AgentLoopFinalOutput BuildAssistantTextFinalOutput(
            AgentDefinition agent,
            string rawContent,
            bool enableGemmaThoughtCompatibility)
        {
            var payload = ExtractVisibleAssistantText(
                rawContent,
                isFinal: true,
                enableGemmaThoughtCompatibility).Trim();
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("Assistant response did not contain text or a Passdown payload.");
            }

            if (agent.IsStructuredXmlIO)
            {
                if (!TryParseXmlWithRoot(
                        payload,
                        AgentDefinition.OutputRootName,
                        out var normalizedXml,
                        out var xmlError))
                {
                    throw new InvalidOperationException(
                        $"Structured agent output must be a complete <{AgentDefinition.OutputRootName}> XML document. {xmlError}");
                }

                return new AgentLoopFinalOutput
                {
                    Content = normalizedXml,
                    Kind = AgentLoopOutputKind.StructuredXml,
                    Source = AgentLoopFinalOutputSource.AssistantText
                };
            }

            if (TryParseXmlWithRoot(
                    payload,
                    AgentDefinition.OutputRootName,
                    out _,
                    out _))
            {
                throw new InvalidOperationException(
                    $"Natural-language agent output must be plain text, not a standalone <{AgentDefinition.OutputRootName}> XML document.");
            }

            return new AgentLoopFinalOutput
            {
                Content = payload,
                Kind = AgentLoopOutputKind.NaturalLanguage,
                Source = AgentLoopFinalOutputSource.AssistantText
            };
        }

        private static bool TryBuildPassdownOutput(
            AgentDefinition agent,
            SkyweaverToolInvocation invocation,
            out AgentLoopFinalOutput output,
            out string errorMessage)
        {
            output = null!;
            errorMessage = string.Empty;

            XElement toolElement;
            try
            {
                toolElement = XElement.Parse(invocation.InvocationXml, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException ex)
            {
                errorMessage = $"Passdown invocation XML could not be parsed: {ex.Message}";
                return false;
            }

            var passdownElement = toolElement.Elements()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, SkyweaverBuiltInToolNames.PassdownParameter, StringComparison.OrdinalIgnoreCase));
            if (passdownElement == null)
            {
                errorMessage = $"Passdown requires a <{SkyweaverBuiltInToolNames.PassdownParameter}> parameter element.";
                return false;
            }

            if (agent.IsStructuredXmlIO)
            {
                var payload = string.Concat(passdownElement.Nodes()
                        .Select(node => node.ToString(SaveOptions.DisableFormatting)))
                    .Trim();
                if (!TryParseXmlWithRoot(
                        payload,
                        AgentDefinition.OutputRootName,
                        out var normalizedXml,
                        out var xmlError))
                {
                    errorMessage = $"Structured Passdown must contain one complete <{AgentDefinition.OutputRootName}> XML document as the child tree of <{SkyweaverBuiltInToolNames.PassdownParameter}>. {xmlError}";
                    return false;
                }

                output = new AgentLoopFinalOutput
                {
                    Content = normalizedXml,
                    Kind = AgentLoopOutputKind.StructuredXml,
                    Source = AgentLoopFinalOutputSource.PassdownPayload
                };
                return true;
            }

            if (passdownElement.Elements().Any())
            {
                errorMessage = $"Natural-language Passdown must contain text inside <{SkyweaverBuiltInToolNames.PassdownParameter}>, not an XML subtree.";
                return false;
            }

            var textPayload = passdownElement.Value?.Trim() ?? string.Empty;
            if (textPayload.Length == 0)
            {
                errorMessage = "Natural-language Passdown cannot be empty.";
                return false;
            }

            output = new AgentLoopFinalOutput
            {
                Content = textPayload,
                Kind = AgentLoopOutputKind.NaturalLanguage,
                Source = AgentLoopFinalOutputSource.PassdownPayload
            };
            return true;
        }

        private static bool TryBuildPassToMainAgentOutput(
            AgentDefinition agent,
            SkyweaverToolInvocation invocation,
            out AgentLoopFinalOutput output,
            out string errorMessage)
        {
            output = null!;
            errorMessage = string.Empty;

            if (!agent.CanRunAsSubAgent)
            {
                errorMessage = "PassToMainAgent can only be used by sub-agents.";
                return false;
            }

            XElement toolElement;
            try
            {
                toolElement = XElement.Parse(invocation.InvocationXml, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException ex)
            {
                errorMessage = $"PassToMainAgent invocation XML could not be parsed: {ex.Message}";
                return false;
            }

            var passElement = toolElement.Elements()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, SkyweaverBuiltInToolNames.PassToMainAgentParameter, StringComparison.OrdinalIgnoreCase));
            if (passElement == null)
            {
                errorMessage = $"PassToMainAgent requires a <{SkyweaverBuiltInToolNames.PassToMainAgentParameter}> parameter element.";
                return false;
            }

            var payload = string.Concat(passElement.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting))).Trim();
            if (payload.Length == 0)
            {
                errorMessage = "PassToMainAgent cannot be empty.";
                return false;
            }

            output = new AgentLoopFinalOutput
            {
                Content = payload,
                Kind = agent.IsStructuredXmlIO ? AgentLoopOutputKind.StructuredXml : AgentLoopOutputKind.NaturalLanguage,
                Source = AgentLoopFinalOutputSource.PassToMainAgentPayload
            };
            return true;
        }

        private AgentToolBackfill CreateBackfill(
            int partIndex,
            int toolCallIndex,
            IReadOnlyList<SkyweaverToolReturnPayload> toolReturns,
            string? toolCallId = null)
        {
            return new AgentToolBackfill
            {
                PartIndex = partIndex,
                ToolCallIndex = toolCallIndex,
                ToolCallId = toolCallId,
                ToolReturns = toolReturns,
                ToolsReturnXml = _toolManager.BuildToolsReturnXml(toolReturns)
            };
        }

        private static IReadOnlyList<string> ExtractLoadedToolKitKeys(
            IReadOnlyList<SkyweaverToolReturnPayload> toolReturns)
        {
            if (toolReturns.Count == 0)
            {
                return Array.Empty<string>();
            }

            return toolReturns
                .Where(toolReturn =>
                    toolReturn.IsSuccess &&
                    string.Equals(toolReturn.ToolName, SkyweaverBuiltInToolNames.LoadToolKits, StringComparison.OrdinalIgnoreCase))
                .SelectMany(toolReturn => ExtractStringValues(toolReturn.Result.Data, "loadedToolKitKeys"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractLoadedToolKitKeysFromHistory(
            IReadOnlyList<LanguageModelChatMessage>? history)
        {
            if (history == null || history.Count == 0)
            {
                return Array.Empty<string>();
            }

            return history
                .SelectMany(message => ExtractLoadedToolKitKeysFromToolsReturnXml(message.Content))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractDefaultToolKitKeys(AgentDefinition agent)
        {
            return agent.DefaultToolKits
                .Select(toolKit => toolKit.ToolKitKey?.Trim() ?? string.Empty)
                .Where(toolKitKey => toolKitKey.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractLoadedToolKitKeysFromToolsReturnXml(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return Array.Empty<string>();
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return Array.Empty<string>();
            }

            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "ToolsReturn", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            return root.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "ToolReturn", StringComparison.OrdinalIgnoreCase))
                .Where(IsSuccessfulLoadToolKitsReturn)
                .SelectMany(ExtractLoadedToolKitKeysFromToolReturnElement)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsSuccessfulLoadToolKitsReturn(XElement toolReturn)
        {
            var toolName = toolReturn.Attributes()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Name.LocalName, "ToolName", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();

            return string.Equals(toolName, SkyweaverBuiltInToolNames.LoadToolKits, StringComparison.OrdinalIgnoreCase) &&
                   !toolReturn.Elements().Any(element =>
                       string.Equals(element.Name.LocalName, "ErrorMessage", StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<string> ExtractLoadedToolKitKeysFromToolReturnElement(XElement toolReturn)
        {
            const string keyPrefix = "loadedToolKitKeys=";

            return toolReturn.Elements()
                .Where(element => element.Name.LocalName.StartsWith("StringReturn", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Value?.Trim() ?? string.Empty)
                .Where(value => value.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                .SelectMany(value => ParseStringArrayValue(value[keyPrefix.Length..]))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ParseStringArrayValue(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return Array.Empty<string>();
            }

            var normalized = rawValue.Trim();
            try
            {
                return JToken.Parse(normalized) is JArray array
                    ? array.Values<string>()
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!.Trim())
                        .ToArray()
                    : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is FormatException or Newtonsoft.Json.JsonReaderException)
            {
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> ExtractStringValues(
            IReadOnlyDictionary<string, object?> data,
            string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return Array.Empty<string>();
            }

            return value switch
            {
                JArray array => array.Values<string>()
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!.Trim())
                    .ToArray(),
                IEnumerable<string> strings => strings
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .ToArray(),
                _ => Array.Empty<string>()
            };
        }

        private string BuildSystemPromptWithRuntimeToolCallNotice(
            string baseSystemPrompt,
            AgentLoopRequest request)
        {
            var noticeLines = new List<string>();
            var knownToolCallIds = CollectKnownToolCallIds(request);
            if (knownToolCallIds.Count > 0)
            {
                noticeLines.Add("Persistent ToolCallID ledger:");
                noticeLines.Add($"- Earliest known ToolCallID: {knownToolCallIds[0]}");
                noticeLines.Add($"- Known ToolCallIDs, oldest first: {FormatToolCallIdList(knownToolCallIds)}");
                noticeLines.Add("- Reuse the same ToolCallID in later turns and after an agent loop restart. For async calls, use WaitForAsyncTools or GetAsyncToolProgress with these IDs.");
            }

            var knownAsyncToolCallIds = CollectKnownAsyncToolCallIds(request);
            if (knownAsyncToolCallIds.Count > 0)
            {
                noticeLines.Add($"- Known async ToolCallIDs: {FormatToolCallIdList(knownAsyncToolCallIds)}");
            }

            var compactedIds = _compactionStore.GetCompactedToolCallIds(request.CompactionFilePath);
            if (compactedIds.Count > 0)
            {
                noticeLines.Add("Some tool calls are compacted: their invocation and output content are hidden from the model context.");
                noticeLines.Add($"To retrieve compacted content, call <Tool ToolName=\"{SkyweaverBuiltInToolNames.RetrieveCompactedToolCalls}\"><{SkyweaverBuiltInToolNames.CompactionToolCallIdsParameter}>[\"TC1\"]</{SkyweaverBuiltInToolNames.CompactionToolCallIdsParameter}></Tool>.");
                noticeLines.Add($"Compacted ToolCallIDs: {string.Join(", ", compactedIds)}");
            }

            if (noticeLines.Count == 0)
            {
                return baseSystemPrompt;
            }

            return baseSystemPrompt.TrimEnd() +
                   Environment.NewLine +
                   Environment.NewLine +
                   string.Join(Environment.NewLine, noticeLines);
        }

        private IReadOnlyList<string> CollectKnownToolCallIds(AgentLoopRequest request)
        {
            var historyToolCallIds = CollectHistoryToolCallRecords(request)
                .Select(record => record.ToolCallId);
            return _toolCallResourceStore.EnumerateToolCallIds(request.ToolCallResourceFolderPath)
                .Concat(historyToolCallIds)
                .Concat(s_asyncToolRegistry.GetKnownToolCallIds(ResolveAsyncToolScopeKey(request)))
                .Select(ChatSessionToolCallIdGenerator.Normalize)
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetToolCallIdOrdinal)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private IReadOnlyList<string> CollectKnownAsyncToolCallIds(AgentLoopRequest request)
        {
            var persistedIds = string.IsNullOrWhiteSpace(request.ToolCallResourceFolderPath)
                ? Array.Empty<string>()
                : _toolCallResourceStore.EnumerateRecords(request.ToolCallResourceFolderPath)
                    .Where(record => TryParsePersistedAsyncInvocation(record.InvocationXml, out _))
                    .Select(record => record.ToolCallId)
                    .ToArray();
            var historyAsyncIds = CollectHistoryToolCallRecords(request)
                .Where(record => TryParsePersistedAsyncInvocation(record.InvocationXml, out _))
                .Select(record => record.ToolCallId)
                .ToArray();

            return persistedIds
                .Concat(historyAsyncIds)
                .Concat(s_asyncToolRegistry.GetKnownToolCallIds(ResolveAsyncToolScopeKey(request)))
                .Select(ChatSessionToolCallIdGenerator.Normalize)
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetToolCallIdOrdinal)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private IReadOnlyList<AgentLoopCompactionToolCallRecord> CollectHistoryToolCallRecords(AgentLoopRequest request)
        {
            var history = request.History ?? Array.Empty<LanguageModelChatMessage>();
            if (history.Count == 0)
            {
                return Array.Empty<AgentLoopCompactionToolCallRecord>();
            }

            return _compactionStore.ExtractToolCallRecords(history);
        }

        private static string FormatToolCallIdList(IReadOnlyList<string> toolCallIds)
        {
            if (toolCallIds.Count <= 40)
            {
                return string.Join(", ", toolCallIds);
            }

            var earliest = toolCallIds.Take(16);
            var latest = toolCallIds.Skip(Math.Max(16, toolCallIds.Count - 16));
            return $"{string.Join(", ", earliest)} ... {string.Join(", ", latest)} (total {toolCallIds.Count})";
        }

        private static long GetToolCallIdOrdinal(string? toolCallId)
        {
            var normalized = ChatSessionToolCallIdGenerator.Normalize(toolCallId);
            return normalized.StartsWith("TC", StringComparison.OrdinalIgnoreCase) &&
                   long.TryParse(normalized[2..], out var ordinal)
                ? ordinal
                : long.MaxValue;
        }

        private static string ResolveAsyncToolScopeKey(AgentLoopRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.AsyncToolStateScopeId))
            {
                return request.AsyncToolStateScopeId.Trim();
            }

            if (request.ToolContext.Properties.TryGetValue("sessionId", out var sessionId) &&
                !string.IsNullOrWhiteSpace(sessionId))
            {
                return sessionId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.ToolCallResourceFolderPath))
            {
                try
                {
                    return Path.GetFullPath(request.ToolCallResourceFolderPath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return request.ToolCallResourceFolderPath.Trim();
                }
            }

            return string.Empty;
        }

        private string BuildSystemPromptWithCompactionNotice(
            string baseSystemPrompt,
            string? compactionFilePath)
        {
            var compactedIds = _compactionStore.GetCompactedToolCallIds(compactionFilePath);
            if (compactedIds.Count == 0)
            {
                return baseSystemPrompt;
            }

            return baseSystemPrompt.TrimEnd() +
                   Environment.NewLine +
                   Environment.NewLine +
                   "部分工具调用已压缩，参数和返回内容已对模型隐藏。" +
                   Environment.NewLine +
                   $"如需取回原文，调用 <Tool ToolName=\"{SkyweaverBuiltInToolNames.RetrieveCompactedToolCalls}\"><{SkyweaverBuiltInToolNames.CompactionToolCallIdsParameter}>[\"TC1\"]</{SkyweaverBuiltInToolNames.CompactionToolCallIdsParameter}></Tool>。" +
                   Environment.NewLine +
                   $"当前已压缩 ToolCallID: {string.Join(", ", compactedIds)}";
        }

        private static void AppendCurrentTurnHistory(
            AgentAssistantResponse assistantResponse,
            IReadOnlyList<AgentToolBackfill> toolBackfills,
            ICollection<LanguageModelChatMessage> turnHistory)
        {
            ArgumentNullException.ThrowIfNull(assistantResponse);
            ArgumentNullException.ThrowIfNull(toolBackfills);
            ArgumentNullException.ThrowIfNull(turnHistory);

            var orderedBackfills = toolBackfills
                .OrderBy(item => item.PartIndex)
                .ThenBy(item => item.ToolCallIndex)
                .ToArray();

            var backfillCursor = 0;
            for (var partIndex = 0; partIndex < assistantResponse.Parts.Count; partIndex++)
            {
                var part = assistantResponse.Parts[partIndex];
                if (!string.IsNullOrWhiteSpace(part.Content))
                {
                    var toolCallId = orderedBackfills
                        .FirstOrDefault(backfill =>
                            backfill.PartIndex == partIndex &&
                            backfill.ToolCallIndex == part.ToolCallIndex)
                        ?.ToolCallId;
                    var content = part.IsToolCall
                        ? AgentLoopCompactionStore.EnsureToolCallIdInToolInvocationXml(part.Content, toolCallId)
                        : part.Content;
                    turnHistory.Add(new LanguageModelChatMessage(
                        LanguageModelChatRole.Assistant,
                        content));
                }

                while (backfillCursor < orderedBackfills.Length &&
                       orderedBackfills[backfillCursor].PartIndex == partIndex)
                {
                    turnHistory.Add(CreateToolResultMessage(orderedBackfills[backfillCursor]));
                    backfillCursor++;
                }
            }

            while (backfillCursor < orderedBackfills.Length)
            {
                turnHistory.Add(CreateToolResultMessage(orderedBackfills[backfillCursor]));
                backfillCursor++;
            }
        }

        private static LanguageModelChatMessage CreateToolResultMessage(AgentToolBackfill backfill)
        {
            var authorName = backfill.ToolReturns.Count == 1
                ? backfill.ToolReturns[0].ToolName
                : "SkyweaverTools";

            return new LanguageModelChatMessage(LanguageModelChatRole.User, backfill.ToolsReturnXml)
            {
                AuthorName = authorName
            };
        }

        private static bool TryParseXmlWithRoot(
            string? payload,
            string expectedRootName,
            out string normalizedXml,
            out string errorMessage)
        {
            normalizedXml = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(payload))
            {
                errorMessage = "The payload is empty.";
                return false;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(payload, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException)
            {
                errorMessage = $"The payload could not be parsed as XML: {ex.Message}";
                return false;
            }

            var root = document.Root;
            if (root == null)
            {
                errorMessage = "The payload is missing a root element.";
                return false;
            }

            if (!string.Equals(root.Name.LocalName, expectedRootName, StringComparison.Ordinal))
            {
                errorMessage = $"The root element must be <{expectedRootName}> instead of <{root.Name.LocalName}>.";
                return false;
            }

            normalizedXml = document.ToString();
            return true;
        }

        private static string? ResolveSessionResourcesFolderPath(AgentLoopRequest request)
        {
            if (request.ToolContext?.Properties != null &&
                request.ToolContext.Properties.TryGetValue("resourcesFolderPath", out var rPath) &&
                !string.IsNullOrWhiteSpace(rPath))
            {
                return rPath;
            }

            if (!string.IsNullOrWhiteSpace(request.ToolCallResourceFolderPath))
            {
                try
                {
                    var parent = Path.GetDirectoryName(request.ToolCallResourceFolderPath);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        return parent;
                    }
                }
                catch { }
            }

            return request.ToolContext?.WorkspacePath;
        }

        private static string GetLanguageModelDisplayName(LanguageModelDefinition model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var displayName = model.DisplayName?.Trim() ?? string.Empty;
            if (displayName.Length > 0)
            {
                return displayName;
            }

            var summaryModelId = model.SummaryModelId?.Trim() ?? string.Empty;
            if (summaryModelId.Length > 0)
            {
                return summaryModelId;
            }

            return string.IsNullOrWhiteSpace(model.Key) ? "unnamed-model" : model.Key.Trim();
        }
    }
}
