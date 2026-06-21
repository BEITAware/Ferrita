using Ferrita.Controls.AgentConfigurationControl.Models;
using Ferrita.Controls.AgentConfigurationControl.Services;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Controls.LanguageModelConfigurationControl.Services;
using Ferrita.Controls.WorkflowEditorControl.Models;
using Ferrita.Controls.WorkflowEditorControl.Services;
using Ferrita.Models.ChatSession;

namespace Ferrita.Services.ChatSession
{
    public sealed class ChatSessionContextWindowSnapshot
    {
        public int EstimatedTokenCount { get; init; }

        public int ContextWindowTokens { get; init; }

        public string? AgentName { get; init; }

        public string? ModelName { get; init; }

        public string? Message { get; init; }

        public bool IsAvailable => ContextWindowTokens > 0;

        public double UsageRatio => ContextWindowTokens <= 0
            ? 0d
            : Math.Clamp(EstimatedTokenCount / (double)ContextWindowTokens, 0d, 1d);

        public static ChatSessionContextWindowSnapshot Unavailable(string message)
        {
            return new ChatSessionContextWindowSnapshot
            {
                Message = message
            };
        }
    }

    public sealed class ChatSessionContextWindowService
    {
        private readonly ChatSessionFlowBindingService _flowBindingService;
        private readonly AgentConfigurationRepository _agentConfigurationRepository;
        private readonly IAgentLanguageModelResolver _languageModelResolver;

        public ChatSessionContextWindowService()
            : this(
                new ChatSessionFlowBindingService(),
                new AgentConfigurationRepository(new AgentConfigurationPathProvider()),
                new AgentLanguageModelResolver())
        {
        }

        public ChatSessionContextWindowService(
            ChatSessionFlowBindingService flowBindingService,
            AgentConfigurationRepository agentConfigurationRepository,
            IAgentLanguageModelResolver languageModelResolver)
        {
            _flowBindingService = flowBindingService ?? throw new ArgumentNullException(nameof(flowBindingService));
            _agentConfigurationRepository = agentConfigurationRepository ?? throw new ArgumentNullException(nameof(agentConfigurationRepository));
            _languageModelResolver = languageModelResolver ?? throw new ArgumentNullException(nameof(languageModelResolver));
        }

        public ChatSessionContextWindowSnapshot CreateSnapshot(ChatSessionModel session)
        {
            ArgumentNullException.ThrowIfNull(session);

            if (!session.HasBoundFlow)
            {
                return ChatSessionContextWindowSnapshot.Unavailable("当前会话未绑定会话流。");
            }

            var compilationResult = _flowBindingService.CompileBinding(session.FlowBinding);
            if (!compilationResult.IsSuccess || compilationResult.Graph == null)
            {
                var message = compilationResult.Errors.FirstOrDefault()?.Message
                    ?? compilationResult.Issues.FirstOrDefault()?.Message
                    ?? "绑定的会话流未通过运行时验证。";
                return ChatSessionContextWindowSnapshot.Unavailable(message);
            }

            var window = ResolveSmallestContextWindow(compilationResult.Graph);
            if (window == null)
            {
                return ChatSessionContextWindowSnapshot.Unavailable("会话流中没有可解析上下文窗口的代理节点。");
            }

            var projectedMessages = ChatSessionTurnHistoryBuilder.BuildForNextTurn(session, currentUserText: null);
            return new ChatSessionContextWindowSnapshot
            {
                EstimatedTokenCount = 0,
                ContextWindowTokens = window.ContextWindowTokens,
                AgentName = window.AgentName,
                ModelName = window.ModelName
            };
        }

        public ChatSessionContextWindowSnapshot CreateSnapshot(
            ChatSessionModel session,
            SessionFlowCompilationResult compilationResult)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(compilationResult);

            if (!session.HasBoundFlow)
            {
                return ChatSessionContextWindowSnapshot.Unavailable("Current session is not bound to a session flow.");
            }

            if (!compilationResult.IsSuccess || compilationResult.Graph == null)
            {
                var message = compilationResult.Errors.FirstOrDefault()?.Message
                    ?? compilationResult.Issues.FirstOrDefault()?.Message
                    ?? "The bound session flow did not pass runtime validation.";
                return ChatSessionContextWindowSnapshot.Unavailable(message);
            }

            var window = ResolveSmallestContextWindow(compilationResult.Graph);
            if (window == null)
            {
                return ChatSessionContextWindowSnapshot.Unavailable("No agent node with a resolvable context window was found in the session flow.");
            }

            var projectedMessages = ChatSessionTurnHistoryBuilder.BuildForNextTurn(session, currentUserText: null);
            return new ChatSessionContextWindowSnapshot
            {
                EstimatedTokenCount = 0,
                ContextWindowTokens = window.ContextWindowTokens,
                AgentName = window.AgentName,
                ModelName = window.ModelName
            };
        }

        private ContextWindowCandidate? ResolveSmallestContextWindow(SessionFlowCompiledGraph graph)
        {
            var agentsById = LoadAgentMap();
            ContextWindowCandidate? smallest = null;

            foreach (var compiledNode in graph.NodesById.Values
                         .Where(node => node.Node.Kind == SessionFlowNodeKind.Agent)
                         .OrderBy(node => node.Node.Title, StringComparer.OrdinalIgnoreCase))
            {
                var agentId = compiledNode.Node.AgentId?.Trim() ?? string.Empty;
                if (agentId.Length == 0 || !agentsById.TryGetValue(agentId, out var agent))
                {
                    continue;
                }

                var candidate = ResolveSmallestContextWindow(agent, compiledNode.Node.Title);
                if (candidate == null)
                {
                    continue;
                }

                if (smallest == null || candidate.ContextWindowTokens < smallest.ContextWindowTokens)
                {
                    smallest = candidate;
                }
            }

            return smallest;
        }

        private ContextWindowCandidate? ResolveSmallestContextWindow(AgentDefinition agent, string nodeTitle)
        {
            var candidates = _languageModelResolver.GetCandidateModels(agent);
            var effectiveCandidates = candidates.Any(model => model.InterfaceSettings.IsFullyConfigured)
                ? candidates.Where(model => model.InterfaceSettings.IsFullyConfigured)
                : candidates;

            var model = effectiveCandidates
                .OrderBy(item => item.EffectiveContextWindowTokens)
                .FirstOrDefault();
            if (model == null)
            {
                return null;
            }

            return new ContextWindowCandidate(
                model.EffectiveContextWindowTokens,
                string.IsNullOrWhiteSpace(nodeTitle) ? agent.DisplayNameOrFallback : nodeTitle.Trim(),
                GetModelDisplayName(model));
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



        private static string GetModelDisplayName(LanguageModelDefinition model)
        {
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

            return string.IsNullOrWhiteSpace(model.Key) ? "未命名模型" : model.Key.Trim();
        }

        private sealed record ContextWindowCandidate(
            int ContextWindowTokens,
            string AgentName,
            string ModelName);
    }
}
