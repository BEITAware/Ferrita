using System.Collections.Concurrent;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Services;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.ContextManagement;
using Skyweaver.Services.Localization;

namespace Skyweaver.Services.ChatSession
{
    public sealed class ChatSessionRuntimeService
    {
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> s_activeExecutions =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ChatSessionFlowBindingService _flowBindingService;
        private readonly AgentConfigurationRepository _agentConfigurationRepository;
        private readonly IAgentLanguageModelResolver _languageModelResolver;
        private readonly SessionFlowExecutionService _executionService;
        private readonly ChatSessionTranscriptWriter _transcriptWriter;
        private readonly ChatSessionRepository _sessionRepository;
        private readonly ChatSessionPersistenceScheduler _persistenceScheduler;
        private readonly SemaphoreSlim _executionGate = new(1, 1);

        private CancellationTokenSource? _activeExecutionCancellationSource;
        private string? _activeSessionId;
        private bool _isExecutionActive;

        public ChatSessionRuntimeService()
            : this(
                new ChatSessionFlowBindingService(),
                new AgentConfigurationRepository(new AgentConfigurationPathProvider()),
                new AgentLanguageModelResolver(),
                new SessionFlowExecutionService(),
                new ChatSessionTranscriptWriter(),
                new ChatSessionRepository())
        {
        }

        public ChatSessionRuntimeService(
            ChatSessionFlowBindingService flowBindingService,
            AgentConfigurationRepository agentConfigurationRepository,
            IAgentLanguageModelResolver languageModelResolver,
            SessionFlowExecutionService executionService)
            : this(
                flowBindingService,
                agentConfigurationRepository,
                languageModelResolver,
                executionService,
                new ChatSessionTranscriptWriter(),
                new ChatSessionRepository(flowBindingService))
        {
        }

        public ChatSessionRuntimeService(
            ChatSessionFlowBindingService flowBindingService,
            AgentConfigurationRepository agentConfigurationRepository,
            IAgentLanguageModelResolver languageModelResolver,
            SessionFlowExecutionService executionService,
            ChatSessionTranscriptWriter transcriptWriter,
            ChatSessionRepository sessionRepository)
        {
            _flowBindingService = flowBindingService ?? throw new ArgumentNullException(nameof(flowBindingService));
            _agentConfigurationRepository = agentConfigurationRepository ?? throw new ArgumentNullException(nameof(agentConfigurationRepository));
            _languageModelResolver = languageModelResolver ?? throw new ArgumentNullException(nameof(languageModelResolver));
            _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
            _transcriptWriter = transcriptWriter ?? throw new ArgumentNullException(nameof(transcriptWriter));
            _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
            _persistenceScheduler = new ChatSessionPersistenceScheduler(_sessionRepository);
        }

        public bool IsExecutionActive => _isExecutionActive;

        public bool CancelActiveExecution(string? sessionId = null)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                s_activeExecutions.TryGetValue(sessionId, out var globalCancellationSource))
            {
                if (!ActiveChatSessionExecutionRegistry.Instance.Cancel(sessionId))
                {
                    globalCancellationSource.Cancel();
                }

                return true;
            }

            var cancellationSource = _activeExecutionCancellationSource;
            if (cancellationSource == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sessionId) &&
                !string.Equals(sessionId, _activeSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!ActiveChatSessionExecutionRegistry.Instance.Cancel(_activeSessionId))
            {
                cancellationSource.Cancel();
            }

            return true;
        }

        public async Task<ChatSessionRuntimeResult> ExecuteTurnAsync(
            ChatSessionRuntimeRequest request,
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Session);

            var trimmedUserText = request.UserText?.Trim() ?? string.Empty;
            var userContentBlocks = request.UserContentBlocks
                .Where(block => block != null)
                .Select(block => block.Clone())
                .ToArray();
            if (trimmedUserText.Length == 0 && userContentBlocks.Length == 0)
            {
                throw new InvalidOperationException("用户输入不能为空。");
            }

            var sessionId = request.Session.SessionId?.Trim() ?? string.Empty;
            if (sessionId.Length == 0)
            {
                throw new InvalidOperationException("当前 ChatSession 缺少有效的 SessionId，无法启动运行时执行。");
            }

            if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("当前 ChatSession 已有一个活动请求正在执行。");
            }

            var hasRegisteredExecution = false;
            using var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            async ValueTask PublishRuntimeEventAsync(
                ChatSessionRuntimeEvent runtimeEvent,
                CancellationToken token)
            {
                _transcriptWriter.ApplyRuntimeEvent(request.Session, runtimeEvent);
                ActiveChatSessionExecutionRegistry.Instance.ApplyRuntimeEvent(runtimeEvent);
                PersistSessionIfNeeded(request.Session, runtimeEvent);
                await PublishAsync(onEventAsync, runtimeEvent, token).ConfigureAwait(false);
            }

            try
            {
                if (!s_activeExecutions.TryAdd(sessionId, linkedCancellationSource))
                {
                    throw new InvalidOperationException("当前 ChatSession 已有一个活动请求正在执行。");
                }

                hasRegisteredExecution = true;
                _activeExecutionCancellationSource = linkedCancellationSource;
                _activeSessionId = sessionId;
                _isExecutionActive = true;
                ActiveChatSessionExecutionRegistry.Instance.Register(
                    request.Session,
                    linkedCancellationSource,
                    trimmedUserText);

                // 确保后续的所有重型 I/O 及计算操作都在后台线程上执行，释放 UI 线程
                await Task.Run(() => { }).ConfigureAwait(false);

                _transcriptWriter.BeginTurn(
                    request.Session,
                    new ChatSessionUserInput
                    {
                        Text = trimmedUserText,
                        ContentBlocks = userContentBlocks
                    });
                SchedulePersistSession(request.Session);

                await PublishRuntimeEventAsync(
                    new ChatSessionRuntimeEvent
                    {
                        Kind = ChatSessionRuntimeEventKind.ExecutionStarted,
                        SessionId = sessionId,
                        SessionTitle = request.Session.Name,
                        FlowName = request.Session.BoundFlowDisplayName
                    },
                    linkedCancellationSource.Token).ConfigureAwait(false);

                await PublishRuntimeEventAsync(
                    new ChatSessionRuntimeEvent
                    {
                        Kind = ChatSessionRuntimeEventKind.UserMessageCommitted,
                        SessionId = sessionId,
                        SessionTitle = request.Session.Name,
                        FlowName = request.Session.BoundFlowDisplayName
                    },
                    linkedCancellationSource.Token).ConfigureAwait(false);

                var hostInjectedHistoryMessages = request.HostInjectedHistoryMessages
                    .Where(message => message != null)
                    .Select(message => message.Clone())
                    .ToList();
                if (request.HostInjectedHistoryMessageFactory != null)
                {
                    var generatedMessages = await request.HostInjectedHistoryMessageFactory(linkedCancellationSource.Token)
                        .ConfigureAwait(false);
                    hostInjectedHistoryMessages.AddRange((generatedMessages ?? Array.Empty<LanguageModelChatMessage>())
                        .Where(message => message != null)
                        .Select(message => message.Clone()));
                }

                var conversationHistory = ChatSessionTurnHistoryBuilder.BuildForNextTurn(
                    request.Session,
                    trimmedUserText,
                    userContentBlocks)
                    .Select(message => message.Clone())
                    .ToList();
                conversationHistory.AddRange(hostInjectedHistoryMessages);
                var reservedToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var toolCallIdFactory = request.ToolCallIdFactory ??
                                        (() => ChatSessionToolCallIdGenerator.Create(request.Session, reservedToolCallIds));

                var compilationResult = _flowBindingService.CompileBinding(request.Session.FlowBinding);
                if (!compilationResult.IsSuccess || compilationResult.Graph == null)
                {
                    var message = BuildCompilationFailureMessage(compilationResult);
                    await PublishRuntimeEventAsync(
                        new ChatSessionRuntimeEvent
                        {
                            Kind = ChatSessionRuntimeEventKind.ExecutionFailed,
                            SessionId = sessionId,
                            SessionTitle = request.Session.Name,
                            FlowName = request.Session.BoundFlowDisplayName,
                            Message = message,
                            CompilationIssues = compilationResult.Issues
                        },
                        linkedCancellationSource.Token).ConfigureAwait(false);

                    return new ChatSessionRuntimeResult
                    {
                        FailureReason = message
                    };
                }

                var graph = compilationResult.Graph;
                var agentsById = LoadAgentMap();
                var preflightErrors = ValidatePreflight(graph, agentsById);
                if (preflightErrors.Count > 0)
                {
                    var message = string.Join(Environment.NewLine, preflightErrors);
                    await PublishRuntimeEventAsync(
                        new ChatSessionRuntimeEvent
                        {
                            Kind = ChatSessionRuntimeEventKind.ExecutionFailed,
                            SessionId = sessionId,
                            SessionTitle = request.Session.Name,
                            FlowName = graph.Document.Name,
                            Message = message,
                            CompilationIssues = compilationResult.Issues
                        },
                        linkedCancellationSource.Token).ConfigureAwait(false);

                    return new ChatSessionRuntimeResult
                    {
                        Graph = graph,
                        FailureReason = message
                    };
                }

                var executionResult = await _executionService.ExecuteAsync(
                    new SessionFlowExecutionRequest
                    {
                        Session = request.Session,
                        Graph = graph,
                        InitialPayload = SessionFlowPayload.FromNaturalLanguage(trimmedUserText, userContentBlocks),
                        InitialUserContentBlocks = userContentBlocks,
                        ConversationHistory = conversationHistory,
                        AgentsById = agentsById,
                        EnableGemmaThoughtCompatibility = request.EnableGemmaThoughtCompatibility,
                        MinCompactionEnabled = request.MinCompactionEnabled ||
                                                ContextManagementRuntime.Instance.MinCompactionEnabled,
                        MaxCompactionEnabled = request.MaxCompactionEnabled ||
                                                ContextManagementRuntime.Instance.MaxCompactionEnabled,
                        ToolCallIdFactory = toolCallIdFactory,
                        ToolConfirmationCallback = ResolveToolConfirmationCallback(request)
                    },
                    PublishRuntimeEventAsync,
                    linkedCancellationSource.Token).ConfigureAwait(false);

                await PublishRuntimeEventAsync(
                    new ChatSessionRuntimeEvent
                    {
                        Kind = ChatSessionRuntimeEventKind.ExecutionCompleted,
                        SessionId = sessionId,
                        SessionTitle = request.Session.Name,
                        FlowName = graph.Document.Name,
                        Message = "本轮会话执行完成。",
                        Payload = executionResult.ReturnPayload,
                        IsPayloadAlreadyPresented = executionResult.IsReturnPayloadAlreadyPresented,
                        CompilationIssues = compilationResult.Issues
                    },
                    linkedCancellationSource.Token).ConfigureAwait(false);

                return new ChatSessionRuntimeResult
                {
                    IsCompleted = true,
                    Graph = graph,
                    ReturnPayload = executionResult.ReturnPayload
                };
            }
            catch (OperationCanceledException)
            {
                await PublishRuntimeEventAsync(
                    new ChatSessionRuntimeEvent
                    {
                        Kind = ChatSessionRuntimeEventKind.ExecutionCancelled,
                        SessionId = sessionId,
                        SessionTitle = request.Session.Name,
                        FlowName = request.Session.BoundFlowDisplayName,
                        Message = "当前执行已取消。"
                    },
                    CancellationToken.None).ConfigureAwait(false);

                return new ChatSessionRuntimeResult
                {
                    IsCancelled = true
                };
            }
            catch (Exception ex)
            {
                await PublishRuntimeEventAsync(
                    new ChatSessionRuntimeEvent
                    {
                        Kind = ChatSessionRuntimeEventKind.ExecutionFailed,
                        SessionId = sessionId,
                        SessionTitle = request.Session.Name,
                        FlowName = request.Session.BoundFlowDisplayName,
                        Message = ex.Message
                    },
                    CancellationToken.None).ConfigureAwait(false);

                return new ChatSessionRuntimeResult
                {
                    FailureReason = ex.Message
                };
            }
            finally
            {
                if (hasRegisteredExecution)
                {
                    FlushPersistSession(request.Session);
                }

                if (hasRegisteredExecution)
                {
                    s_activeExecutions.TryRemove(sessionId, out _);
                }

                _isExecutionActive = false;
                _activeSessionId = null;
                _activeExecutionCancellationSource = null;
                ActiveChatSessionExecutionRegistry.Instance.Unregister(sessionId);
                _executionGate.Release();
            }
        }

        private void PersistSessionIfNeeded(
            ChatSessionModel session,
            ChatSessionRuntimeEvent runtimeEvent)
        {
            if (!ShouldPersistAfter(runtimeEvent))
            {
                return;
            }

            SchedulePersistSession(session);
        }

        private void SchedulePersistSession(ChatSessionModel session)
        {
            try
            {
                _persistenceScheduler.ScheduleSave(session);
            }
            catch
            {
                // Runtime persistence must not mask the original agent failure path.
            }
        }

        private void FlushPersistSession(ChatSessionModel session)
        {
            try
            {
                _persistenceScheduler.Flush(session);
            }
            catch
            {
                // Runtime persistence must not mask the original agent failure path.
            }
        }

        private static bool ShouldPersistAfter(ChatSessionRuntimeEvent runtimeEvent)
        {
            return runtimeEvent.Kind switch
            {
                ChatSessionRuntimeEventKind.TextDelta => false,
                ChatSessionRuntimeEventKind.ReasoningDelta => false,
                ChatSessionRuntimeEventKind.UserMessageCommitted => false,
                ChatSessionRuntimeEventKind.ToolCallUpdated when runtimeEvent.ToolCallSnapshot?.IsInvocationClosed != true &&
                                                       runtimeEvent.ToolInvocation == null => false,
                ChatSessionRuntimeEventKind.ToolProgressUpdated => false,
                ChatSessionRuntimeEventKind.MediaProcessingProgressUpdated => false,
                ChatSessionRuntimeEventKind.AgentIterationStarted or
                    ChatSessionRuntimeEventKind.AgentIterationCompleted or
                    ChatSessionRuntimeEventKind.NodeStarted => false,
                _ => true
            };
        }

        private Dictionary<string, AgentDefinition> LoadAgentMap()
        {
            return _agentConfigurationRepository.Load()
                .Where(agent => !string.IsNullOrWhiteSpace(agent.AgentId))
                .ToDictionary(
                    agent => agent.AgentId,
                    agent => agent.DeepClone(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Func<AgentToolConfirmationRequest, CancellationToken, Task<AgentToolConfirmationResult>>?
            ResolveToolConfirmationCallback(ChatSessionRuntimeRequest request)
        {
            if (request.ToolConfirmationService != null)
            {
                return request.ToolConfirmationService.ConfirmAsync;
            }

            return request.ToolConfirmationCallback;
        }

        private List<string> ValidatePreflight(
            SessionFlowCompiledGraph graph,
            IReadOnlyDictionary<string, AgentDefinition> agentsById)
        {
            var errors = new List<string>();

            foreach (var compiledNode in graph.NodesById.Values.Where(node => node.Node.Kind == SessionFlowNodeKind.Agent))
            {
                if (string.IsNullOrWhiteSpace(compiledNode.Node.AgentId))
                {
                    errors.Add(LF("ChatSessionRuntime.Preflight.AgentNodeMissingDefinitionFormat", "代理节点“{0}”尚未绑定 AgentDefinition。", compiledNode.Node.Title));
                    continue;
                }

                if (!agentsById.TryGetValue(compiledNode.Node.AgentId, out var agent))
                {
                    errors.Add(LF("ChatSessionRuntime.Preflight.AgentMissingFormat", "代理节点“{0}”引用的代理“{1}”不存在。", compiledNode.Node.Title, compiledNode.Node.AgentId));
                    continue;
                }

                var candidates = _languageModelResolver.GetCandidateModels(agent);
                if (candidates.Count == 0)
                {
                    errors.Add(LF("ChatSessionRuntime.Preflight.NoCandidateModelsFormat", "代理“{0}”没有解析出任何可用的语言模型候选项。", agent.DisplayNameOrFallback));
                    continue;
                }

                if (candidates.All(model => !model.InterfaceSettings.IsFullyConfigured))
                {
                    errors.Add(LF("ChatSessionRuntime.Preflight.CandidateModelsIncompleteFormat", "代理“{0}”的候选语言模型均未完成接口配置。", agent.DisplayNameOrFallback));
                }
            }

            return errors;
        }

        private static string BuildCompilationFailureMessage(SessionFlowCompilationResult compilationResult)
        {
            var issues = compilationResult.Errors.Any()
                ? compilationResult.Errors
                : compilationResult.Issues;

            if (issues.Count == 0)
            {
                return L("ChatSessionRuntime.CompilationFailed.NoReadableError", "会话流编译失败，但没有返回可读错误。");
            }

            return string.Join(
                Environment.NewLine,
                issues.Take(8).Select(issue => $"• {issue.Message}"));
        }

        private static async ValueTask PublishAsync(
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            ChatSessionRuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            if (onEventAsync == null)
            {
                return;
            }

            await onEventAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static string LF(string resourceKey, string fallbackFormat, params object?[] args)
        {
            return string.Format(L(resourceKey, fallbackFormat), args);
        }
    }
}
