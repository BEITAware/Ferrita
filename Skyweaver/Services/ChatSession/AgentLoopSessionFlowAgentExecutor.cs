using Skyweaver.Services.AgentLoop;

namespace Skyweaver.Services.ChatSession
{
    public sealed class AgentLoopSessionFlowAgentExecutor : ISessionFlowAgentExecutor
    {
        private readonly AgentLoopService _agentLoopService;

        public AgentLoopSessionFlowAgentExecutor()
            : this(new AgentLoopService())
        {
        }

        public AgentLoopSessionFlowAgentExecutor(AgentLoopService agentLoopService)
        {
            _agentLoopService = agentLoopService ?? throw new ArgumentNullException(nameof(agentLoopService));
        }

        public async Task<AgentLoopResult> ExecuteAsync(
            SessionFlowAgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Agent);

            var agentLoopRequest = new AgentLoopRequest
            {
                Agent = request.Agent,
                Input = request.Input,
                InputContentBlocks = request.InputContentBlocks,
                History = request.History,
                ToolContext = request.ToolContext,
                EnableGemmaThoughtCompatibility = request.EnableGemmaThoughtCompatibility,
                IsSubAgent = request.IsSubAgent,
                MinCompactionEnabled = request.MinCompactionEnabled,
                MaxCompactionEnabled = request.MaxCompactionEnabled,
                CompactionFilePath = request.CompactionFilePath,
                ToolCallIdFactory = request.ToolCallIdFactory,
                AsyncToolStateScopeId = request.AsyncToolStateScopeId,
                ToolCallResourceFolderPath = request.ToolCallResourceFolderPath,
                ToolConfirmationCallback = request.ToolConfirmationCallback,
                IsScheduledTaskSession = request.IsScheduledTaskSession
            };

            if (request.EventSink != null)
            {
                return await _agentLoopService.RunStreamingAsync(
                    agentLoopRequest,
                    request.EventSink,
                    cancellationToken).ConfigureAwait(false);
            }

            return await _agentLoopService.RunAsync(agentLoopRequest, cancellationToken).ConfigureAwait(false);
        }
    }
}
