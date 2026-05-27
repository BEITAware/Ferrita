using System.Xml.Linq;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Services;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Services.ChatSession
{
    public sealed class SessionFlowExecutionService
    {
        private sealed record RoutedPayload(
            SessionFlowPortPayload Payload,
            bool IsAlreadyPresented);

        private sealed class ConnectionResolutionState
        {
            public bool IsResolved { get; set; }

            public RoutedPayload? Payload { get; set; }
        }

        private sealed class NodeRuntimeState
        {
            public bool IsProcessed { get; set; }

            public Dictionary<string, ConnectionResolutionState> IncomingConnections { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record DeliveredInputBinding(
            SessionFlowPortModel Port,
            SessionFlowPortPayload Payload,
            bool IsAlreadyPresented);

        private sealed class NodeExecutionOutcome
        {
            public bool IsSkipped { get; init; }

            public SessionFlowPayload? NodePayload { get; init; }

            public bool IsNodePayloadAlreadyPresented { get; init; }

            public IReadOnlyDictionary<string, RoutedPayload> ExplicitOutputPayloads { get; init; } =
                new Dictionary<string, RoutedPayload>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly SessionFlowPayloadRouter _payloadRouter;
        private readonly ISessionFlowAgentExecutor _agentExecutor;

        public SessionFlowExecutionService()
            : this(new SessionFlowPayloadRouter(), new AgentLoopSessionFlowAgentExecutor())
        {
        }

        public SessionFlowExecutionService(
            SessionFlowPayloadRouter payloadRouter,
            ISessionFlowAgentExecutor agentExecutor)
        {
            _payloadRouter = payloadRouter ?? throw new ArgumentNullException(nameof(payloadRouter));
            _agentExecutor = agentExecutor ?? throw new ArgumentNullException(nameof(agentExecutor));
        }

        public async Task<SessionFlowExecutionResult> ExecuteAsync(
            SessionFlowExecutionRequest request,
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Session);
            ArgumentNullException.ThrowIfNull(request.Graph);
            ArgumentNullException.ThrowIfNull(request.InitialPayload);

            var runtimeStates = request.Graph.NodesById.Values.ToDictionary(
                node => node.Node.Id,
                node =>
                {
                    var state = new NodeRuntimeState();
                    foreach (var connection in node.IncomingConnections)
                    {
                        state.IncomingConnections[connection.Id] = new ConnectionResolutionState();
                    }

                    return state;
                },
                StringComparer.OrdinalIgnoreCase);

            var readyQueue = new Queue<SessionFlowCompiledNode>();
            foreach (var node in request.Graph.NodesById.Values
                         .Where(node => node.IncomingConnections.Count == 0)
                         .OrderBy(node => node.Node.Kind == SessionFlowNodeKind.UserInput ? 0 : 1)
                         .ThenBy(node => node.Node.Title, StringComparer.OrdinalIgnoreCase))
            {
                readyQueue.Enqueue(node);
            }

            while (readyQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var compiledNode = readyQueue.Dequeue();
                if (runtimeStates[compiledNode.Node.Id].IsProcessed)
                {
                    continue;
                }

                var deliveredBindings = GetDeliveredBindings(compiledNode, runtimeStates[compiledNode.Node.Id]);

                var outcome = await ExecuteNodeAsync(
                    request,
                    compiledNode,
                    deliveredBindings,
                    onEventAsync,
                    cancellationToken).ConfigureAwait(false);

                runtimeStates[compiledNode.Node.Id].IsProcessed = true;

                await PublishAsync(
                    onEventAsync,
                    CreateRuntimeEvent(
                        request,
                        ChatSessionRuntimeEventKind.NodeCompleted,
                        compiledNode,
                        message: outcome.IsSkipped ? "节点未被激活，已跳过。" : "节点执行完成。",
                        payload: outcome.NodePayload,
                        isSkipped: outcome.IsSkipped),
                    cancellationToken).ConfigureAwait(false);

                if (compiledNode.Node.Kind == SessionFlowNodeKind.Return && outcome.NodePayload != null)
                {
                    return new SessionFlowExecutionResult
                    {
                        Graph = request.Graph,
                        ReturnPayload = outcome.NodePayload,
                        IsReturnPayloadAlreadyPresented = outcome.IsNodePayloadAlreadyPresented
                    };
                }

                var deliveredRouteCount = ResolveOutgoingConnections(
                    request,
                    compiledNode,
                    outcome,
                    runtimeStates,
                    readyQueue);

                if (!outcome.IsSkipped &&
                    compiledNode.Node.Kind != SessionFlowNodeKind.Return &&
                    deliveredRouteCount == 0)
                {
                    throw CreateExecutionError(
                        compiledNode,
                        "节点产生了输出，但没有任何下游分支被真正执行。");
                }
            }

            throw new InvalidOperationException("会话流执行结束时仍未到达“返回”节点。请检查分支是否可达，或图中是否存在未闭合的执行路径。");
        }

        private async Task<NodeExecutionOutcome> ExecuteNodeAsync(
            SessionFlowExecutionRequest request,
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var node = compiledNode.Node;

            await PublishAsync(
                onEventAsync,
                CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.NodeStarted,
                    compiledNode,
                    message: node.Kind == SessionFlowNodeKind.UserInput
                        ? "用户输入节点开始分发本轮消息。"
                        : deliveredBindings.Count == 0
                            ? "节点未收到激活载荷。"
                            : "节点开始执行。"),
                cancellationToken).ConfigureAwait(false);

            if (node.Kind != SessionFlowNodeKind.UserInput && deliveredBindings.Count == 0)
            {
                return new NodeExecutionOutcome
                {
                    IsSkipped = true
                };
            }

            return node.Kind switch
            {
                SessionFlowNodeKind.UserInput => ExecuteUserInputNode(request),
                SessionFlowNodeKind.Return => await ExecuteReturnNodeAsync(
                    request,
                    compiledNode,
                    deliveredBindings,
                    onEventAsync,
                    cancellationToken).ConfigureAwait(false),
                SessionFlowNodeKind.Agent => await ExecuteAgentNodeAsync(
                    request,
                    compiledNode,
                    deliveredBindings,
                    onEventAsync,
                    cancellationToken).ConfigureAwait(false),
                SessionFlowNodeKind.LogicAnd => ExecuteBooleanLogicNode(compiledNode, deliveredBindings, static (left, right) => left && right),
                SessionFlowNodeKind.LogicOr => ExecuteBooleanLogicNode(compiledNode, deliveredBindings, static (left, right) => left || right),
                SessionFlowNodeKind.LogicXor => ExecuteBooleanLogicNode(compiledNode, deliveredBindings, static (left, right) => left ^ right),
                SessionFlowNodeKind.LogicNot => ExecuteBooleanNotNode(compiledNode, deliveredBindings),
                SessionFlowNodeKind.LogicExecution => ExecuteLogicExecutionNode(compiledNode, deliveredBindings, onlyNextBranch: false),
                SessionFlowNodeKind.NextLogicExecution => ExecuteLogicExecutionNode(compiledNode, deliveredBindings, onlyNextBranch: true),
                _ => throw CreateExecutionError(compiledNode, $"不支持的节点类型：{node.Kind}")
            };
        }

        private static NodeExecutionOutcome ExecuteUserInputNode(SessionFlowExecutionRequest request)
        {
            return new NodeExecutionOutcome
            {
                NodePayload = request.InitialPayload
            };
        }

        private async Task<NodeExecutionOutcome> ExecuteReturnNodeAsync(
            SessionFlowExecutionRequest request,
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var naturalLanguageBinding = deliveredBindings.FirstOrDefault(binding =>
                string.Equals(binding.Port.Id, "input-return-text", StringComparison.OrdinalIgnoreCase));
            var xmlBinding = deliveredBindings.FirstOrDefault(binding =>
                string.Equals(binding.Port.Id, "input-return-xml", StringComparison.OrdinalIgnoreCase));

            if (naturalLanguageBinding != null && xmlBinding != null)
            {
                throw CreateExecutionError(compiledNode, "返回节点同时收到了自然语言和 XML 载荷，无法确定最终返回值。");
            }

            SessionFlowPayload? payload = null;
            var isPayloadAlreadyPresented = false;
            if (naturalLanguageBinding != null)
            {
                payload = SessionFlowPayload.FromNaturalLanguage(
                    naturalLanguageBinding.Payload.Content,
                    naturalLanguageBinding.Payload.ContentBlocks);
                isPayloadAlreadyPresented = naturalLanguageBinding.IsAlreadyPresented;
            }
            else if (xmlBinding != null)
            {
                if (!SessionFlowPayload.TryCreate(
                        SessionFlowPortType.XmlField,
                        xmlBinding.Payload.Content,
                        out payload,
                        out var errorMessage))
                {
                    throw CreateExecutionError(compiledNode, errorMessage);
                }

                isPayloadAlreadyPresented = xmlBinding.IsAlreadyPresented;

                await PublishAsync(
                    onEventAsync,
                    CreateRuntimeEvent(
                        request,
                        ChatSessionRuntimeEventKind.StructuredOutputProduced,
                        compiledNode,
                        message: "返回节点生成了结构化 XML 输出。",
                        payload: payload),
                    cancellationToken).ConfigureAwait(false);
            }

            return new NodeExecutionOutcome
            {
                NodePayload = payload,
                IsNodePayloadAlreadyPresented = isPayloadAlreadyPresented
            };
        }

        private async Task<NodeExecutionOutcome> ExecuteAgentNodeAsync(
            SessionFlowExecutionRequest request,
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var node = compiledNode.Node;
            if (string.IsNullOrWhiteSpace(node.AgentId))
            {
                throw CreateExecutionError(compiledNode, "代理节点尚未绑定 AgentDefinition。");
            }

            if (!request.AgentsById.TryGetValue(node.AgentId, out var agent))
            {
                throw CreateExecutionError(compiledNode, $"找不到代理配置：{node.AgentId}");
            }

            if (!agent.CanRunAsMainAgent)
            {
                throw CreateExecutionError(compiledNode, $"代理“{agent.DisplayNameOrFallback}”不允许作为会话流主代理执行。");
            }

            if (!_payloadRouter.TryBuildAgentInput(
                    agent,
                    deliveredBindings.Select(binding => (binding.Port, binding.Payload)),
                    out var agentInput,
                    out var agentInputContentBlocks,
                    out var errorMessage))
            {
                throw CreateExecutionError(compiledNode, errorMessage);
            }

            var agentHistory = request.ConversationHistory
                .Select(message => message.Clone())
                .ToList();
            AppendAgentInputToConversationHistory(
                agentHistory,
                compiledNode,
                agent,
                agentInput,
                agentInputContentBlocks);

            var reservedToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toolCallIdsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ChatSessionResourceLayout.EnsureResources(request.Session);
            var agentResult = await _agentExecutor.ExecuteAsync(
                new SessionFlowAgentExecutionRequest
                {
                    Agent = agent.DeepClone(),
                    Input = agentInput,
                    InputContentBlocks = agentInputContentBlocks,
                    History = agentHistory.ToArray(),
                    ToolContext = BuildToolContext(request),
                    ToolCallIdFactory = request.ToolCallIdFactory,
                    EnableGemmaThoughtCompatibility = request.EnableGemmaThoughtCompatibility,
                    MinCompactionEnabled = request.MinCompactionEnabled,
                    MaxCompactionEnabled = request.MaxCompactionEnabled,
                    CompactionFilePath = ChatSessionResourceLayout.GetCompactionFilePath(request.Session),
                    AsyncToolStateScopeId = request.Session.SessionId,
                    ToolCallResourceFolderPath = ChatSessionResourceLayout.GetToolCallsFolderPath(request.Session),
                    ToolConfirmationCallback = request.ToolConfirmationCallback,
                    IsScheduledTaskSession = request.Session.IsScheduledTaskSession,
                    EventSink = (update, ct) => PublishAgentLoopUpdateAsync(
                        request,
                        compiledNode,
                        update,
                        toolCallIdsByKey,
                        reservedToolCallIds,
                        onEventAsync,
                        ct)
                },
                cancellationToken).ConfigureAwait(false);

            if (!agentResult.IsCompleted || agentResult.FinalOutput == null)
            {
                var failureReason = string.IsNullOrWhiteSpace(agentResult.FailureReason)
                    ? $"代理节点“{node.Title}”没有产生有效输出。"
                    : agentResult.FailureReason!;
                throw CreateExecutionError(compiledNode, failureReason);
            }

            var payload = _payloadRouter.CreateNodePayloadFromAgentOutput(agentResult.FinalOutput);
            if (payload.IsStructuredXml)
            {
                await PublishAsync(
                    onEventAsync,
                    CreateRuntimeEvent(
                        request,
                        ChatSessionRuntimeEventKind.StructuredOutputProduced,
                        compiledNode,
                        message: "代理节点生成了结构化 XML 输出。",
                        payload: payload),
                    cancellationToken).ConfigureAwait(false);
            }

            return new NodeExecutionOutcome
            {
                NodePayload = payload,
                IsNodePayloadAlreadyPresented = !node.IsHiddenAgent &&
                                                agentResult.FinalOutput.Source == AgentLoopFinalOutputSource.AssistantText
            };
        }

        private NodeExecutionOutcome ExecuteBooleanLogicNode(
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            Func<bool, bool, bool> operation)
        {
            var left = GetRequiredBoolean(compiledNode, deliveredBindings, "in-a");
            var right = GetRequiredBoolean(compiledNode, deliveredBindings, "in-b");

            return new NodeExecutionOutcome
            {
                ExplicitOutputPayloads = new Dictionary<string, RoutedPayload>(StringComparer.OrdinalIgnoreCase)
                {
                    ["out-result"] = new RoutedPayload(
                        CreateBooleanPayload(operation(left, right)),
                        false)
                }
            };
        }

        private NodeExecutionOutcome ExecuteBooleanNotNode(
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings)
        {
            var value = GetRequiredBoolean(compiledNode, deliveredBindings, "in-a");
            return new NodeExecutionOutcome
            {
                ExplicitOutputPayloads = new Dictionary<string, RoutedPayload>(StringComparer.OrdinalIgnoreCase)
                {
                    ["out-result"] = new RoutedPayload(
                        CreateBooleanPayload(!value),
                        false)
                }
            };
        }

        private NodeExecutionOutcome ExecuteLogicExecutionNode(
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            bool onlyNextBranch)
        {
            var condition = GetRequiredBoolean(compiledNode, deliveredBindings, "condition");
            if (!condition)
            {
                throw CreateExecutionError(compiledNode, "逻辑执行节点条件为 false，当前没有可执行分支。");
            }

            var nonConditionBindings = deliveredBindings
                .Where(binding => !binding.Port.IsBooleanCondition)
                .OrderBy(binding => compiledNode.Node.InputPorts.IndexOf(binding.Port))
                .ToArray();
            if (nonConditionBindings.Length == 0)
            {
                throw CreateExecutionError(compiledNode, "逻辑执行节点没有收到任何可透传的输入载荷。");
            }

            var explicitOutputs = new Dictionary<string, RoutedPayload>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in nonConditionBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Port.PairKey))
                {
                    throw CreateExecutionError(compiledNode, $"输入端口“{binding.Port.Name}”缺少 PairKey，无法找到对应输出端口。");
                }

                var outputPort = compiledNode.Node.OutputPorts.FirstOrDefault(port =>
                    string.Equals(port.PairKey, binding.Port.PairKey, StringComparison.OrdinalIgnoreCase));
                if (outputPort == null)
                {
                    throw CreateExecutionError(compiledNode, $"输入端口“{binding.Port.Name}”没有对应的透明输出端口。");
                }

                explicitOutputs[outputPort.Id] = new RoutedPayload(
                    binding.Payload,
                    binding.IsAlreadyPresented);
                if (onlyNextBranch)
                {
                    break;
                }
            }

            if (explicitOutputs.Count == 0)
            {
                throw CreateExecutionError(compiledNode, "逻辑执行节点未解析出任何可执行分支。");
            }

            return new NodeExecutionOutcome
            {
                ExplicitOutputPayloads = explicitOutputs
            };
        }

        private int ResolveOutgoingConnections(
            SessionFlowExecutionRequest request,
            SessionFlowCompiledNode compiledNode,
            NodeExecutionOutcome outcome,
            IReadOnlyDictionary<string, NodeRuntimeState> runtimeStates,
            Queue<SessionFlowCompiledNode> readyQueue)
        {
            var deliveredRouteCount = 0;

            foreach (var connection in compiledNode.OutgoingConnections)
            {
                if (!compiledNode.TryGetOutputPort(connection.SourcePortId, out var sourcePort) || sourcePort == null)
                {
                    throw CreateExecutionError(compiledNode, $"找不到输出端口：{connection.SourcePortId}");
                }

                if (!TryResolveOutgoingPayload(
                        compiledNode,
                        sourcePort,
                        outcome,
                        out var payload,
                        out var errorMessage))
                {
                    throw CreateExecutionError(compiledNode, errorMessage);
                }

                var targetState = runtimeStates[connection.TargetNodeId];
                targetState.IncomingConnections[connection.Id].IsResolved = true;
                targetState.IncomingConnections[connection.Id].Payload = payload;

                if (payload != null)
                {
                    deliveredRouteCount++;
                }

                if (request.Graph.TryGetNode(connection.TargetNodeId, out var targetNode) &&
                    targetNode != null &&
                    AreAllIncomingConnectionsResolved(targetState) &&
                    !targetState.IsProcessed)
                {
                    readyQueue.Enqueue(targetNode);
                }
            }

            return deliveredRouteCount;
        }

        private bool TryResolveOutgoingPayload(
            SessionFlowCompiledNode compiledNode,
            SessionFlowPortModel sourcePort,
            NodeExecutionOutcome outcome,
            out RoutedPayload? payload,
            out string errorMessage)
        {
            payload = null;
            errorMessage = string.Empty;

            if (outcome.IsSkipped)
            {
                return true;
            }

            if (sourcePort.IsTransparentOutput)
            {
                outcome.ExplicitOutputPayloads.TryGetValue(sourcePort.Id, out payload);
                return true;
            }

            if (outcome.NodePayload == null)
            {
                return true;
            }

            if (!_payloadRouter.TryExtractPortPayload(
                    outcome.NodePayload,
                    sourcePort,
                    out var extractedPayload,
                    out errorMessage))
            {
                return false;
            }

            payload = extractedPayload == null
                ? null
                : new RoutedPayload(
                    extractedPayload,
                    outcome.IsNodePayloadAlreadyPresented);
            return true;
        }

        private static IReadOnlyList<DeliveredInputBinding> GetDeliveredBindings(
            SessionFlowCompiledNode compiledNode,
            NodeRuntimeState runtimeState)
        {
            return compiledNode.IncomingConnections
                .Where(connection =>
                    runtimeState.IncomingConnections.TryGetValue(connection.Id, out var resolution) &&
                    resolution.Payload != null &&
                    compiledNode.TryGetInputPort(connection.TargetPortId, out var port) &&
                    port != null)
                .Select(connection => new DeliveredInputBinding(
                    compiledNode.InputPortsById[connection.TargetPortId],
                    runtimeState.IncomingConnections[connection.Id].Payload!.Payload,
                    runtimeState.IncomingConnections[connection.Id].Payload!.IsAlreadyPresented))
                .ToArray();
        }

        private static bool AreAllIncomingConnectionsResolved(NodeRuntimeState runtimeState)
        {
            return runtimeState.IncomingConnections.Values.All(item => item.IsResolved);
        }

        private bool TryGetPortBinding(
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            string portId,
            out DeliveredInputBinding? binding,
            out string errorMessage)
        {
            binding = deliveredBindings.FirstOrDefault(item =>
                string.Equals(item.Port.Id, portId, StringComparison.OrdinalIgnoreCase));
            if (binding != null)
            {
                errorMessage = string.Empty;
                return true;
            }

            var inputPort = compiledNode.InputPortsById.TryGetValue(portId, out var port) ? port : null;
            if (inputPort == null)
            {
                errorMessage = $"找不到输入端口：{portId}";
                return false;
            }

            if (compiledNode.GetIncomingConnectionsForTargetPort(portId).Count == 0)
            {
                errorMessage = $"端口“{inputPort.Name}”没有任何输入连接。";
                return false;
            }

            errorMessage = $"端口“{inputPort.Name}”没有收到有效输入。";
            return false;
        }

        private bool TryGetRequiredBoolean(
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            string portId,
            out bool result,
            out string errorMessage)
        {
            if (!TryGetPortBinding(compiledNode, deliveredBindings, portId, out var binding, out errorMessage) ||
                binding == null)
            {
                result = false;
                return false;
            }

            if (!_payloadRouter.TryNormalizeBoolean(binding.Payload, out result))
            {
                errorMessage = $"端口“{binding.Port.Name}”的值无法归一化为布尔值。";
                return false;
            }

            return true;
        }

        private bool GetRequiredBoolean(
            SessionFlowCompiledNode compiledNode,
            IReadOnlyList<DeliveredInputBinding> deliveredBindings,
            string portId)
        {
            if (!TryGetRequiredBoolean(compiledNode, deliveredBindings, portId, out var value, out var errorMessage))
            {
                throw CreateExecutionError(compiledNode, errorMessage);
            }

            return value;
        }

        private static SessionFlowPortPayload CreateBooleanPayload(bool value)
        {
            return SessionFlowPortPayload.FromXmlElement(new XElement("Boolean", value ? "true" : "false"));
        }

        private static void AppendAgentInputToConversationHistory(
            IList<LanguageModelChatMessage> conversationHistory,
            SessionFlowCompiledNode compiledNode,
            AgentDefinition agent,
            string agentInput,
            IReadOnlyList<LanguageModelChatContentBlock>? agentInputContentBlocks)
        {
            ArgumentNullException.ThrowIfNull(conversationHistory);
            ArgumentNullException.ThrowIfNull(compiledNode);
            ArgumentNullException.ThrowIfNull(agent);

            var normalizedInput = NormalizeConversationHistoryContent(agentInput);
            var contentBlocks = new List<LanguageModelChatContentBlock>();
            if (normalizedInput.Length > 0)
            {
                contentBlocks.Add(LanguageModelChatContentBlock.CreateText(normalizedInput));
            }

            if (agentInputContentBlocks != null)
            {
                contentBlocks.AddRange(agentInputContentBlocks
                    .Where(block => block != null)
                    .Select(block => block.Clone()));
            }

            if (contentBlocks.Count == 0)
            {
                return;
            }

            if (conversationHistory.Count > 0 &&
                IsSameUserPayload(conversationHistory[^1], contentBlocks))
            {
                return;
            }

            conversationHistory.Add(new LanguageModelChatMessage(
                LanguageModelChatRole.User,
                contentBlocks)
            {
                AuthorName = NormalizeConversationHistoryAuthor(compiledNode.Node.Title)
                    ?? NormalizeConversationHistoryAuthor(agent.DisplayNameOrFallback)
            });
        }

        private static bool IsSameUserPayload(
            LanguageModelChatMessage candidate,
            IReadOnlyList<LanguageModelChatContentBlock> contentBlocks)
        {
            return candidate.Role == LanguageModelChatRole.User &&
                   AreContentBlocksEquivalent(candidate.ContentBlocks, contentBlocks);
        }

        private static bool AreContentBlocksEquivalent(
            IReadOnlyList<LanguageModelChatContentBlock> left,
            IReadOnlyList<LanguageModelChatContentBlock> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!AreContentBlocksEquivalent(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreContentBlocksEquivalent(
            LanguageModelChatContentBlock left,
            LanguageModelChatContentBlock right)
        {
            return left.Kind == right.Kind &&
                   string.Equals(NormalizeConversationHistoryContent(left.Content), NormalizeConversationHistoryContent(right.Content), StringComparison.Ordinal) &&
                   string.Equals(NormalizeConversationHistoryContent(left.ResourcePath), NormalizeConversationHistoryContent(right.ResourcePath), StringComparison.Ordinal) &&
                   string.Equals(NormalizeConversationHistoryContent(left.MediaType), NormalizeConversationHistoryContent(right.MediaType), StringComparison.OrdinalIgnoreCase) &&
                   AreByteArraysEquivalent(left.Data, right.Data);
        }

        private static bool AreByteArraysEquivalent(byte[]? left, byte[]? right)
        {
            if (left == null || left.Length == 0)
            {
                return right == null || right.Length == 0;
            }

            return right != null && left.SequenceEqual(right);
        }

        private static string NormalizeConversationHistoryContent(string? content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? string.Empty
                : content.Trim();
        }

        private static string? NormalizeConversationHistoryAuthor(string? authorName)
        {
            var normalized = NormalizeConversationHistoryContent(authorName);
            return normalized.Length == 0 ? null : normalized;
        }

        private static SkyweaverToolContext BuildToolContext(SessionFlowExecutionRequest request)
        {
            var workspacePath = string.IsNullOrWhiteSpace(request.Session.ResourcesFolderPath)
                ? request.Session.SessionFolderPath
                : request.Session.ResourcesFolderPath;

            return new SkyweaverToolContext
            {
                ApplicationName = "Skyweaver",
                SessionTitle = request.Session.Name,
                WorkspacePath = workspacePath,
                Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sessionId"] = request.Session.SessionId,
                    ["flowId"] = request.Session.FlowBinding.GraphId,
                    ["flowName"] = request.Graph.Document.Name,
                    ["resourcesFolderPath"] = ChatSessionResourceLayout.GetResourcesFolderPath(request.Session)
                }
            };
        }

        private async ValueTask PublishAgentLoopUpdateAsync(
            SessionFlowExecutionRequest request,
            SessionFlowCompiledNode compiledNode,
            AgentLoopRuntimeEvent update,
            IDictionary<string, string> toolCallIdsByKey,
            ISet<string> reservedToolCallIds,
            Func<ChatSessionRuntimeEvent, CancellationToken, ValueTask>? onEventAsync,
            CancellationToken cancellationToken)
        {
            var toolCallId = ResolveToolCallId(request, update, toolCallIdsByKey, reservedToolCallIds);

            ChatSessionRuntimeEvent? runtimeEvent = update.Kind switch
            {
                AgentLoopRuntimeEventKind.IterationStarted => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.AgentIterationStarted,
                    compiledNode,
                    message: "代理迭代开始。",
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    tokenUsage: update.TokenUsage),
                AgentLoopRuntimeEventKind.IterationCompleted => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.AgentIterationCompleted,
                    compiledNode,
                    message: "代理迭代结束。",
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId),
                AgentLoopRuntimeEventKind.TextDelta => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.TextDelta,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    textDelta: update.TextDelta,
                    textDeltaOutputKind: update.TextDeltaOutputKind,
                    tokenUsage: update.TokenUsage),
                AgentLoopRuntimeEventKind.ReasoningDelta => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.ReasoningDelta,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    reasoningDelta: update.ReasoningDelta,
                    reasoningCollapsible: update.IsReasoningCollapsible,
                    partIndex: update.PartIndex,
                    tokenUsage: update.TokenUsage),
                AgentLoopRuntimeEventKind.AssistantToolCallsReceived => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.AssistantToolCallsReceived,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    toolXml: update.ToolXml),
                AgentLoopRuntimeEventKind.ToolCallStarted => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.ToolCallStarted,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    partIndex: update.PartIndex,
                    toolCallIndex: update.ToolCallIndex,
                    toolCallId: toolCallId,
                    toolCallSnapshot: update.ToolCallSnapshot,
                    toolXml: update.ToolXml),
                AgentLoopRuntimeEventKind.ToolCallUpdated => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.ToolCallUpdated,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    partIndex: update.PartIndex,
                    toolCallIndex: update.ToolCallIndex,
                    toolCallId: toolCallId,
                    toolCallSnapshot: update.ToolCallSnapshot,
                    toolInvocation: update.ToolInvocation,
                    toolXml: update.ToolXml),
                AgentLoopRuntimeEventKind.MalformedToolCall => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.MalformedToolCall,
                    compiledNode,
                    message: update.ErrorMessage,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    partIndex: update.PartIndex,
                    toolCallIndex: update.ToolCallIndex,
                    toolCallId: toolCallId,
                    toolXml: update.ToolXml),
                AgentLoopRuntimeEventKind.ToolOutputReceived => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.ToolOutputReceived,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    partIndex: update.PartIndex,
                    toolCallIndex: update.ToolCallIndex,
                    toolCallId: toolCallId,
                    toolInvocation: update.ToolInvocation,
                    toolOutputXml: update.ToolOutputXml,
                    toolReturns: update.ToolReturns),
                AgentLoopRuntimeEventKind.ToolProgressUpdated => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.ToolProgressUpdated,
                    compiledNode,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    partIndex: update.PartIndex,
                    toolCallIndex: update.ToolCallIndex,
                    toolCallId: toolCallId,
                    toolInvocation: update.ToolInvocation,
                    toolProgress: update.ToolProgress),
                AgentLoopRuntimeEventKind.MediaProcessingProgressUpdated => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.MediaProcessingProgressUpdated,
                    compiledNode,
                    message: update.MediaProcessingProgress?.Progress.StatusText,
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    mediaProcessingProgress: update.MediaProcessingProgress),
                AgentLoopRuntimeEventKind.ContextCompressionApplied => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.ContextCompressionApplied,
                    compiledNode,
                    message: "本轮代理迭代触发了上下文压缩。",
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    contextCompression: update.ContextCompression),
                AgentLoopRuntimeEventKind.FinalOutputProduced when update.FinalOutput != null => CreateRuntimeEvent(
                    request,
                    ChatSessionRuntimeEventKind.AgentFinalOutputProduced,
                    compiledNode,
                    message: update.FinalOutput.IsStructuredXml
                        ? "代理节点生成了最终结构化 XML 输出。"
                        : "代理节点生成了最终自然语言输出。",
                    iterationNumber: update.IterationNumber,
                    modelId: update.ModelId,
                    payload: _payloadRouter.CreateNodePayloadFromAgentOutput(update.FinalOutput),
                    isPayloadFromPassdown: update.FinalOutput.IsFromPassdownPayload),
                _ => null
            };

            if (runtimeEvent != null)
            {
                await PublishAsync(onEventAsync, runtimeEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string? ResolveToolCallId(
            SessionFlowExecutionRequest request,
            AgentLoopRuntimeEvent update,
            IDictionary<string, string> toolCallIdsByKey,
            ISet<string> reservedToolCallIds)
        {
            var providedToolCallId = ChatSessionToolCallIdGenerator.Normalize(update.ToolCallId);
            if (providedToolCallId.Length > 0)
            {
                if (update.ToolCallIndex is int providedToolCallIndex)
                {
                    var providedToolCallKey = $"{update.IterationNumber}:{update.PartIndex ?? 0}:{providedToolCallIndex}";
                    toolCallIdsByKey[providedToolCallKey] = providedToolCallId;
                }

                reservedToolCallIds.Add(providedToolCallId);
                return providedToolCallId;
            }

            if (update.ToolCallIndex is not int generatedToolCallIndex)
            {
                return null;
            }

            var generatedToolCallKey = $"{update.IterationNumber}:{update.PartIndex ?? 0}:{generatedToolCallIndex}";
            if (toolCallIdsByKey.TryGetValue(generatedToolCallKey, out var existingId))
            {
                return existingId;
            }

            var toolCallId = ChatSessionToolCallIdGenerator.Create(request.Session, reservedToolCallIds);
            toolCallIdsByKey[generatedToolCallKey] = toolCallId;
            return toolCallId;
        }

        private static ChatSessionRuntimeEvent CreateRuntimeEvent(
            SessionFlowExecutionRequest request,
            ChatSessionRuntimeEventKind kind,
            SessionFlowCompiledNode? compiledNode,
            string? message = null,
            SessionFlowPayload? payload = null,
            bool isSkipped = false,
            int? iterationNumber = null,
            string? modelId = null,
            string? textDelta = null,
            string? reasoningDelta = null,
            bool reasoningCollapsible = true,
            AgentLoopOutputKind? textDeltaOutputKind = null,
            int? partIndex = null,
            int? toolCallIndex = null,
            string? toolCallId = null,
            SkyweaverToolInvocation? toolInvocation = null,
            SkyweaverStreamingToolCallSnapshot? toolCallSnapshot = null,
            string? toolXml = null,
            string? toolOutputXml = null,
            IReadOnlyList<SkyweaverToolReturnPayload>? toolReturns = null,
            SkyweaverToolProgressUpdate? toolProgress = null,
            AgentLoopMediaProcessingProgress? mediaProcessingProgress = null,
            AgentLoopContextCompressionInfo? contextCompression = null,
            AgentLoopTokenUsageInfo? tokenUsage = null,
            bool isPayloadFromPassdown = false)
        {
            return new ChatSessionRuntimeEvent
            {
                Kind = kind,
                SessionId = request.Session.SessionId,
                SessionTitle = request.Session.Name,
                FlowName = request.Graph.Document.Name,
                NodeId = compiledNode?.Node.Id,
                NodeTitle = compiledNode?.Node.Title,
                AgentId = compiledNode?.Node.AgentId,
                NodeKind = compiledNode?.Node.Kind,
                IsHiddenAgent = compiledNode?.Node.IsHiddenAgent == true,
                IsSkipped = isSkipped,
                IterationNumber = iterationNumber,
                ModelId = modelId,
                Message = message,
                Payload = payload,
                IsPayloadFromPassdown = isPayloadFromPassdown,
                TextDelta = textDelta,
                ReasoningDelta = reasoningDelta,
                IsReasoningCollapsible = reasoningCollapsible,
                TextDeltaOutputKind = textDeltaOutputKind,
                PartIndex = partIndex,
                ToolCallIndex = toolCallIndex,
                ToolCallId = toolCallId,
                ToolInvocation = toolInvocation,
                ToolCallSnapshot = toolCallSnapshot,
                ToolXml = toolXml,
                ToolOutputXml = toolOutputXml,
                ToolReturns = toolReturns ?? Array.Empty<SkyweaverToolReturnPayload>(),
                ToolProgress = toolProgress?.Normalize(),
                MediaProcessingProgress = mediaProcessingProgress?.Normalize(),
                ContextCompression = contextCompression,
                TokenUsage = tokenUsage
            };
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

        private static InvalidOperationException CreateExecutionError(
            SessionFlowCompiledNode compiledNode,
            string message)
        {
            return new InvalidOperationException($"节点“{compiledNode.Node.Title}”执行失败：{message}");
        }
    }
}
