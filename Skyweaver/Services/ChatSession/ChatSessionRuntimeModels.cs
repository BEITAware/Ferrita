using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Services;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Services.ChatSession
{
    public enum ChatSessionRuntimeEventKind
    {
        ExecutionStarted = 0,
        NodeStarted = 1,
        NodeCompleted = 2,
        AgentIterationStarted = 3,
        AgentIterationCompleted = 4,
        TextDelta = 5,
        AssistantToolCallsReceived = 6,
        ToolCallStarted = 7,
        ToolCallUpdated = 8,
        MalformedToolCall = 9,
        ToolOutputReceived = 10,
        ContextCompressionApplied = 11,
        AgentFinalOutputProduced = 12,
        StructuredOutputProduced = 13,
        ExecutionCompleted = 14,
        ExecutionFailed = 15,
        ExecutionCancelled = 16,
        ReasoningDelta = 17
    }

    public sealed class ChatSessionRuntimeRequest
    {
        public ChatSessionModel Session { get; init; } = null!;

        public string UserText { get; init; } = string.Empty;

        public IReadOnlyList<LanguageModelChatContentBlock> UserContentBlocks { get; init; } =
            Array.Empty<LanguageModelChatContentBlock>();

        public bool EnableGemmaThoughtCompatibility { get; init; } = true;

        public Func<string>? ToolCallIdFactory { get; init; }

        public IAgentToolConfirmationService? ToolConfirmationService { get; init; }

        public Func<AgentToolConfirmationRequest, CancellationToken, Task<AgentToolConfirmationResult>>? ToolConfirmationCallback { get; init; }
    }

    public sealed class ChatSessionRuntimeResult
    {
        public bool IsCompleted { get; init; }

        public bool IsCancelled { get; init; }

        public string? FailureReason { get; init; }

        public SessionFlowPayload? ReturnPayload { get; init; }

        public SessionFlowCompiledGraph? Graph { get; init; }
    }

    public sealed class ChatSessionRuntimeEvent
    {
        public ChatSessionRuntimeEventKind Kind { get; init; }

        public string SessionId { get; init; } = string.Empty;

        public string SessionTitle { get; init; } = string.Empty;

        public string FlowName { get; init; } = string.Empty;

        public string? NodeId { get; init; }

        public string? NodeTitle { get; init; }

        public string? AgentId { get; init; }

        public SessionFlowNodeKind? NodeKind { get; init; }

        public bool IsHiddenAgent { get; init; }

        public bool IsSkipped { get; init; }

        public int? IterationNumber { get; init; }

        public string? ModelId { get; init; }

        public string? Message { get; init; }

        public string? TextDelta { get; init; }

        public string? ReasoningDelta { get; init; }

        public bool IsReasoningCollapsible { get; init; } = true;

        public AgentLoopOutputKind? TextDeltaOutputKind { get; init; }

        public int? PartIndex { get; init; }

        public int? ToolCallIndex { get; init; }

        public string? ToolCallId { get; init; }

        public SkyweaverToolInvocation? ToolInvocation { get; init; }

        public SkyweaverStreamingToolCallSnapshot? ToolCallSnapshot { get; init; }

        public string? ToolXml { get; init; }

        public string? ToolOutputXml { get; init; }

        public IReadOnlyList<SkyweaverToolReturnPayload> ToolReturns { get; init; } = Array.Empty<SkyweaverToolReturnPayload>();

        public AgentLoopContextCompressionInfo? ContextCompression { get; init; }

        public SessionFlowPayload? Payload { get; init; }

        public bool IsPayloadFromPassdown { get; init; }

        public bool IsPayloadAlreadyPresented { get; init; }

        public IReadOnlyList<SessionFlowCompilationIssue> CompilationIssues { get; init; } = Array.Empty<SessionFlowCompilationIssue>();
    }

    public sealed class SessionFlowExecutionRequest
    {
        public ChatSessionModel Session { get; init; } = null!;

        public SessionFlowCompiledGraph Graph { get; init; } = null!;

        public SessionFlowPayload InitialPayload { get; init; } = null!;

        public IReadOnlyList<LanguageModelChatContentBlock> InitialUserContentBlocks { get; init; } =
            Array.Empty<LanguageModelChatContentBlock>();

        public IList<LanguageModelChatMessage> ConversationHistory { get; init; } =
            new List<LanguageModelChatMessage>();

        public IReadOnlyDictionary<string, AgentDefinition> AgentsById { get; init; } =
            new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);

        public bool EnableGemmaThoughtCompatibility { get; init; } = true;

        public Func<string>? ToolCallIdFactory { get; init; }

        public Func<AgentToolConfirmationRequest, CancellationToken, Task<AgentToolConfirmationResult>>? ToolConfirmationCallback { get; init; }
    }

    public sealed class SessionFlowExecutionResult
    {
        public SessionFlowCompiledGraph Graph { get; init; } = null!;

        public SessionFlowPayload ReturnPayload { get; init; } = null!;

        public bool IsReturnPayloadAlreadyPresented { get; init; }
    }

    public sealed class SessionFlowAgentExecutionRequest
    {
        public AgentDefinition Agent { get; init; } = null!;

        public string Input { get; init; } = string.Empty;

        public IReadOnlyList<LanguageModelChatContentBlock> InputContentBlocks { get; init; } =
            Array.Empty<LanguageModelChatContentBlock>();

        public IReadOnlyList<LanguageModelChatMessage> History { get; init; } =
            Array.Empty<LanguageModelChatMessage>();

        public SkyweaverToolContext ToolContext { get; init; } = new();

        public Func<string>? ToolCallIdFactory { get; init; }

        public Func<AgentLoopRuntimeEvent, CancellationToken, ValueTask>? EventSink { get; init; }

        public bool EnableGemmaThoughtCompatibility { get; init; } = true;

        public bool IsSubAgent { get; init; }

        public Func<AgentToolConfirmationRequest, CancellationToken, Task<AgentToolConfirmationResult>>? ToolConfirmationCallback { get; init; }
    }

    public interface ISessionFlowAgentExecutor
    {
        Task<AgentLoopResult> ExecuteAsync(
            SessionFlowAgentExecutionRequest request,
            CancellationToken cancellationToken = default);
    }
}
