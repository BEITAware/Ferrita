using Ferrita.Controls.AgentConfigurationControl.Models;
using Ferrita.Controls.LanguageModelConfigurationControl.Services;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Services.AgentLoop
{
    public enum AgentAssistantResponsePartKind
    {
        NaturalLanguage = 0,
        ToolCall = 1
    }

    public enum AgentLoopOutputKind
    {
        NaturalLanguage = 0,
        StructuredXml = 1
    }

    public enum AgentLoopFinalOutputSource
    {
        AssistantText = 0,
        PassdownPayload = 1,
        PassToMainAgentPayload = 2
    }

    public sealed class AgentLoopRequest
    {
        public AgentDefinition Agent { get; init; } = null!;

        public string Input { get; init; } = string.Empty;

        public IReadOnlyList<LanguageModelChatContentBlock> InputContentBlocks { get; init; } =
            Array.Empty<LanguageModelChatContentBlock>();

        public IReadOnlyList<LanguageModelChatMessage> History { get; init; } = Array.Empty<LanguageModelChatMessage>();

        public FerritaToolContext ToolContext { get; init; } = new();

        public bool EnableGemmaThoughtCompatibility { get; init; } = true;

        public bool IsSubAgent { get; init; }

        public bool MinCompactionEnabled { get; init; }

        public bool MaxCompactionEnabled { get; init; }

        public string? CompactionFilePath { get; init; }

        public bool EnableCompactionTools { get; init; }

        public Func<string>? ToolCallIdFactory { get; init; }

        public string? AsyncToolStateScopeId { get; init; }

        public string? ToolCallResourceFolderPath { get; init; }

        public Func<AgentToolConfirmationRequest, CancellationToken, Task<AgentToolConfirmationResult>>? ToolConfirmationCallback { get; init; }

        public bool IsScheduledTaskSession { get; init; }

        public bool OptimizeToolCallPromptEnabled { get; init; }
    }

    public sealed class AgentLoopMediaProcessingProgress
    {
        public LanguageModelMediaProcessingProgress Progress { get; init; } = new();

        public AgentLoopMediaProcessingProgress Normalize()
        {
            return new AgentLoopMediaProcessingProgress
            {
                Progress = Progress.Normalize()
            };
        }
    }

    public sealed class AgentToolConfirmationRequest
    {
        public AgentDefinition Agent { get; init; } = null!;

        public FerritaToolInvocation Invocation { get; init; } = null!;

        public AgentToolEffectiveDecision PermissionDecision { get; init; }

        public int IterationNumber { get; init; }

        public int PartIndex { get; init; }

        public int ToolCallIndex { get; init; }
    }

    public sealed class AgentToolConfirmationResult
    {
        public bool IsApproved { get; init; }

        public string? RejectionMessage { get; init; }

        public static AgentToolConfirmationResult Approve()
        {
            return new AgentToolConfirmationResult
            {
                IsApproved = true
            };
        }

        public static AgentToolConfirmationResult Reject(string? rejectionMessage = null)
        {
            return new AgentToolConfirmationResult
            {
                IsApproved = false,
                RejectionMessage = rejectionMessage
            };
        }
    }

    public sealed class AgentAssistantResponsePart
    {
        private static readonly IReadOnlyList<FerritaToolInvocation> s_emptyToolCalls = Array.Empty<FerritaToolInvocation>();

        private AgentAssistantResponsePart(
            AgentAssistantResponsePartKind kind,
            string content,
            IReadOnlyList<FerritaToolInvocation>? toolCalls = null,
            string? parseError = null,
            int toolCallIndex = 0)
        {
            Kind = kind;
            Content = content ?? string.Empty;
            ToolCalls = toolCalls ?? s_emptyToolCalls;
            ParseError = string.IsNullOrWhiteSpace(parseError) ? null : parseError.Trim();
            ToolCallIndex = toolCallIndex;
        }

        public AgentAssistantResponsePartKind Kind { get; }

        public string Content { get; }

        public IReadOnlyList<FerritaToolInvocation> ToolCalls { get; }

        public string? ParseError { get; }

        public int ToolCallIndex { get; }

        public bool IsNaturalLanguage => Kind == AgentAssistantResponsePartKind.NaturalLanguage;

        public bool IsToolCall => Kind == AgentAssistantResponsePartKind.ToolCall;

        public bool HasParseError => !string.IsNullOrWhiteSpace(ParseError);

        public static AgentAssistantResponsePart CreateNaturalLanguage(string content)
        {
            return new AgentAssistantResponsePart(AgentAssistantResponsePartKind.NaturalLanguage, content);
        }

        public static AgentAssistantResponsePart CreateToolCall(
            string xmlContent,
            IReadOnlyList<FerritaToolInvocation> toolCalls,
            string? parseError = null,
            int toolCallIndex = 0)
        {
            return new AgentAssistantResponsePart(
                AgentAssistantResponsePartKind.ToolCall,
                xmlContent,
                toolCalls,
                parseError,
                toolCallIndex);
        }
    }

    public sealed class AgentAssistantResponse
    {
        public AgentAssistantResponse(string rawContent, IReadOnlyList<AgentAssistantResponsePart> parts)
        {
            RawContent = rawContent ?? string.Empty;
            Parts = parts ?? Array.Empty<AgentAssistantResponsePart>();
        }

        public string RawContent { get; }

        public IReadOnlyList<AgentAssistantResponsePart> Parts { get; }

        public IReadOnlyList<AgentAssistantResponsePart> GetNaturalLanguageParts()
        {
            return Parts.Where(part => part.IsNaturalLanguage).ToArray();
        }

        public IReadOnlyList<AgentAssistantResponsePart> GetToolCallParts()
        {
            return Parts.Where(part => part.IsToolCall).ToArray();
        }

        public IReadOnlyList<FerritaToolInvocation> GetToolCalls()
        {
            return Parts
                .Where(part => part.IsToolCall && !part.HasParseError)
                .SelectMany(part => part.ToolCalls)
                .ToArray();
        }

        public string GetNaturalLanguageText(string separator = "\n\n")
        {
            return string.Join(
                separator,
                GetNaturalLanguageParts()
                    .Select(part => part.Content)
                    .Where(content => !string.IsNullOrWhiteSpace(content)));
        }
    }

    public sealed class AgentToolBackfill
    {
        public int PartIndex { get; init; }

        public int ToolCallIndex { get; init; }

        public string? ToolCallId { get; init; }

        public string ToolsReturnXml { get; init; } = string.Empty;

        public IReadOnlyList<FerritaToolReturnPayload> ToolReturns { get; init; } = Array.Empty<FerritaToolReturnPayload>();
    }

    public sealed class AgentLoopFinalOutput
    {
        public string Content { get; init; } = string.Empty;

        public AgentLoopOutputKind Kind { get; init; }

        public AgentLoopFinalOutputSource Source { get; init; } = AgentLoopFinalOutputSource.AssistantText;

        public bool IsStructuredXml => Kind == AgentLoopOutputKind.StructuredXml;

        public bool IsFromPassdownPayload => Source == AgentLoopFinalOutputSource.PassdownPayload;

        public string? NaturalLanguageText => Kind == AgentLoopOutputKind.NaturalLanguage ? Content : null;

        public string? XmlText => Kind == AgentLoopOutputKind.StructuredXml ? Content : null;
    }

    public sealed class AgentLoopContextCompressionInfo
    {
        public int ContextWindowTokens { get; init; }

        public int EstimatedTokenCountBeforeCompression { get; init; }

        public int EstimatedTokenCountAfterCompression { get; init; }

        public int TargetTokenCountAfterCompression { get; init; }

        public string CompressionLayerKey { get; init; } = string.Empty;

        public string? CompressionModelId { get; init; }

        public IReadOnlyList<string> CompactedToolCallIds { get; init; } = Array.Empty<string>();
    }

    public sealed class AgentLoopTokenUsageInfo
    {
        public int ContextWindowTokens { get; init; }

        public int EstimatedInputTokenCount { get; init; }

        public int EstimatedOutputTokenCount { get; init; }

        public string? ModelId { get; init; }

        public int EstimatedTotalTokenCount => Math.Max(0, EstimatedInputTokenCount) +
                                               Math.Max(0, EstimatedOutputTokenCount);

        public double UsageRatio => ContextWindowTokens <= 0
            ? 0d
            : Math.Clamp(EstimatedTotalTokenCount / (double)ContextWindowTokens, 0d, 1d);
    }

    public sealed class AgentLoopIteration
    {
        public int IterationNumber { get; init; }

        public string? ModelId { get; init; }

        public AgentAssistantResponse AssistantResponse { get; init; } =
            new(string.Empty, Array.Empty<AgentAssistantResponsePart>());

        public IReadOnlyList<AgentToolBackfill> ToolBackfills { get; init; } = Array.Empty<AgentToolBackfill>();

        public AgentLoopFinalOutput? FinalOutput { get; init; }

        public AgentLoopContextCompressionInfo? ContextCompression { get; init; }
    }

    public sealed class AgentLoopResult
    {
        public bool IsCompleted { get; init; }

        public string? FailureReason { get; init; }

        public string? LastModelId { get; init; }

        public AgentLoopFinalOutput? FinalOutput { get; init; }

        public IReadOnlyList<AgentLoopIteration> Iterations { get; init; } = Array.Empty<AgentLoopIteration>();

        public IReadOnlyList<FerritaToolInvocation> GetAllToolCalls()
        {
            return Iterations
                .SelectMany(iteration => iteration.AssistantResponse.GetToolCalls())
                .ToArray();
        }

        public IReadOnlyList<string> GetAllNaturalLanguageSegments()
        {
            return Iterations
                .SelectMany(iteration => iteration.AssistantResponse.GetNaturalLanguageParts())
                .Select(part => part.Content)
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .ToArray();
        }
    }

    public enum AgentLoopRuntimeEventKind
    {
        IterationStarted = 0,
        TextDelta = 1,
        ReasoningDelta = 2,
        AssistantToolCallsReceived = 3,
        ToolCallStarted = 4,
        ToolCallUpdated = 5,
        MalformedToolCall = 6,
        ToolOutputReceived = 7,
        ContextCompressionApplied = 8,
        FinalOutputProduced = 9,
        IterationCompleted = 10,
        ToolProgressUpdated = 11,
        MediaProcessingProgressUpdated = 12
    }

    public sealed class AgentLoopRuntimeEvent
    {
        public AgentLoopRuntimeEventKind Kind { get; init; }

        public int IterationNumber { get; init; }

        public string? ModelId { get; init; }

        public string? Message { get; init; }

        public string? TextDelta { get; init; }

        public string? ReasoningDelta { get; init; }

        public bool IsReasoningCollapsible { get; init; } = true;

        public AgentLoopOutputKind? TextDeltaOutputKind { get; init; }

        public int? PartIndex { get; init; }

        public int? ToolCallIndex { get; init; }

        public string? ToolCallId { get; init; }

        public FerritaToolInvocation? ToolInvocation { get; init; }

        public FerritaStreamingToolCallSnapshot? ToolCallSnapshot { get; init; }

        public string? ToolXml { get; init; }

        public string? ErrorMessage { get; init; }

        public string? ToolOutputXml { get; init; }

        public IReadOnlyList<FerritaToolReturnPayload> ToolReturns { get; init; } = Array.Empty<FerritaToolReturnPayload>();

        public FerritaToolProgressUpdate? ToolProgress { get; init; }

        public AgentLoopMediaProcessingProgress? MediaProcessingProgress { get; init; }

        public AgentLoopFinalOutput? FinalOutput { get; init; }

        public AgentLoopContextCompressionInfo? ContextCompression { get; init; }

        public AgentLoopTokenUsageInfo? TokenUsage { get; init; }
    }
}
