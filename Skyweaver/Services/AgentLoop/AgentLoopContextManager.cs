using System.Text;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;

namespace Skyweaver.Services.AgentLoop
{
    public sealed class AgentLoopContextManager
    {
        private const double CompressionTargetRatio = 0.25d;
        private const string ToolProtocolTailReminder =
            """
再次确认工具协议：
- 只使用 <Tool> / <ToolAsync>。
- 严禁 <tool_call>、<function_call>、CreateMessage、FinishTask、<tools>、<tool_calls> 等伪协议。
- 工具标签必须是完整 XML；不要夹在普通正文里。
""";

        private readonly IAgentLanguageModelResolver _languageModelResolver;
        private readonly ILanguageModelChatService _chatService;
        private readonly ApproximateTokenEstimator _tokenEstimator = new();

        private sealed record CompressionExecutionResult(string SummaryText, string? ModelId);

        public AgentLoopContextManager()
            : this(new AgentLanguageModelResolver(), new LanguageModelChatService())
        {
        }

        public AgentLoopContextManager(
            IAgentLanguageModelResolver languageModelResolver,
            ILanguageModelChatService chatService)
        {
            _languageModelResolver = languageModelResolver ?? throw new ArgumentNullException(nameof(languageModelResolver));
            _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        }

        public Task<AgentLoopContextPreparationResult> PrepareAsync(
            AgentDefinition agent,
            string systemPrompt,
            string upstreamInput,
            IReadOnlyList<LanguageModelChatMessage> persistentHistory,
            IReadOnlyList<LanguageModelChatMessage> turnHistory,
            CancellationToken cancellationToken = default)
        {
            return PrepareAsync(
                agent,
                systemPrompt,
                upstreamInput,
                upstreamContentBlocks: null,
                persistentHistory,
                turnHistory,
                debugRunContext: null,
                iterationNumber: 0,
                cancellationToken);
        }

        internal async Task<AgentLoopContextPreparationResult> PrepareAsync(
            AgentDefinition agent,
            string systemPrompt,
            string upstreamInput,
            IReadOnlyList<LanguageModelChatContentBlock>? upstreamContentBlocks,
            IReadOnlyList<LanguageModelChatMessage> persistentHistory,
            IReadOnlyList<LanguageModelChatMessage> turnHistory,
            AgentLoopDebugRunContext? debugRunContext = null,
            int iterationNumber = 0,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);

            var immutableHistory = TrimTrailingCurrentInputEchoes(
                NormalizeHistory(persistentHistory),
                upstreamInput,
                upstreamContentBlocks);
            var currentTurnHistory = NormalizeHistory(turnHistory);

            var contextWindowTokens = _languageModelResolver.GetMinimumContextWindowTokens(agent);
            var preparedMessages = ComposeMessages(
                systemPrompt,
                immutableHistory,
                upstreamInput,
                upstreamContentBlocks,
                currentTurnHistory);
            var estimatedBefore = _tokenEstimator.Estimate(preparedMessages);

            if (estimatedBefore <= contextWindowTokens)
            {
                return new AgentLoopContextPreparationResult
                {
                    PreparedMessages = preparedMessages,
                    PersistentHistory = immutableHistory,
                    TurnHistory = currentTurnHistory
                };
            }

            if (immutableHistory.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Agent '{agent.DisplayNameOrFallback}' exceeds the context window with its system prompt, current user input, and current-turn loop history. Earlier-turn history is already empty, so there is nothing left to compress.");
            }

            var fixedMessages = ComposeFixedMessages(systemPrompt, upstreamInput, upstreamContentBlocks, currentTurnHistory);
            var fixedTokenCount = _tokenEstimator.Estimate(fixedMessages);
            if (fixedTokenCount >= contextWindowTokens)
            {
                throw new InvalidOperationException(
                    $"Agent '{agent.DisplayNameOrFallback}' exceeds the context window with its system prompt, current user input, and current-turn loop history. Compressing earlier turns would not be enough to continue.");
            }

            var targetTokenCountAfterCompression = Math.Max(
                fixedTokenCount,
                (int)Math.Floor(contextWindowTokens * CompressionTargetRatio));
            var targetHistoryTokens = Math.Max(0, targetTokenCountAfterCompression - fixedTokenCount);

            var compressionResult = await CompressHistoryAsync(
                agent,
                upstreamInput,
                immutableHistory,
                targetHistoryTokens,
                debugRunContext,
                iterationNumber,
                cancellationToken).ConfigureAwait(false);

            var compressedPersistentHistory = string.IsNullOrWhiteSpace(compressionResult.SummaryText)
                ? new List<LanguageModelChatMessage>()
                : new List<LanguageModelChatMessage>
                {
                    new(LanguageModelChatRole.Assistant, BuildCompressedHistoryEnvelope(compressionResult.SummaryText))
                    {
                        AuthorName = "HistorySummary"
                    }
                };

            preparedMessages = ComposeMessages(
                systemPrompt,
                compressedPersistentHistory,
                upstreamInput,
                upstreamContentBlocks,
                currentTurnHistory);
            var estimatedAfter = _tokenEstimator.Estimate(preparedMessages);
            if (estimatedAfter > contextWindowTokens)
            {
                throw new InvalidOperationException(
                    $"Agent '{agent.DisplayNameOrFallback}' still exceeds the context window after compressing earlier-turn history. Shorten the system prompt, the current input, or the current-turn loop transcript.");
            }

            return new AgentLoopContextPreparationResult
            {
                PreparedMessages = preparedMessages,
                PersistentHistory = compressedPersistentHistory,
                TurnHistory = currentTurnHistory,
                ContextCompression = new AgentLoopContextCompressionInfo
                {
                    ContextWindowTokens = contextWindowTokens,
                    EstimatedTokenCountBeforeCompression = estimatedBefore,
                    EstimatedTokenCountAfterCompression = estimatedAfter,
                    TargetTokenCountAfterCompression = targetTokenCountAfterCompression,
                    CompressionLayerKey = CapabilityLayerBuiltIns.ContextCompressionLayerKey,
                    CompressionModelId = compressionResult.ModelId
                }
            };
        }

        private async Task<CompressionExecutionResult> CompressHistoryAsync(
            AgentDefinition agent,
            string upstreamInput,
            IReadOnlyList<LanguageModelChatMessage> persistentHistory,
            int targetHistoryTokens,
            AgentLoopDebugRunContext? debugRunContext,
            int iterationNumber,
            CancellationToken cancellationToken)
        {
            var compressionMessages = BuildCompressionMessages(
                agent,
                upstreamInput,
                persistentHistory,
                targetHistoryTokens);
            var attemptNumber = 0;

            return await _languageModelResolver.ExecuteCapabilityLayerWithFallbackAsync(
                CapabilityLayerBuiltIns.ContextCompressionLayerKey,
                async (model, ct) =>
                {
                    attemptNumber++;
                    AgentLoopDebugRecorder.RecordCompressionRequest(
                        debugRunContext,
                        agent,
                        model,
                        iterationNumber,
                        attemptNumber,
                        targetHistoryTokens,
                        upstreamInput,
                        persistentHistory,
                        compressionMessages);

                    try
                    {
                        var response = await _chatService.GetResponseAsync(
                            model,
                            compressionMessages.Select(message => message.Clone()).ToArray(),
                            ct).ConfigureAwait(false);
                        var summaryText = response.Text.Trim();

                        AgentLoopDebugRecorder.RecordCompressionResult(
                            debugRunContext,
                            agent,
                            model,
                            iterationNumber,
                            attemptNumber,
                            response.ModelId,
                            summaryText);

                        return new CompressionExecutionResult(
                            summaryText,
                            response.ModelId);
                    }
                    catch (Exception ex)
                    {
                        AgentLoopDebugRecorder.RecordCompressionFailure(
                            debugRunContext,
                            agent,
                            model,
                            iterationNumber,
                            attemptNumber,
                            ex);
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        private static IReadOnlyList<LanguageModelChatMessage> ComposeMessages(
            string systemPrompt,
            IReadOnlyList<LanguageModelChatMessage> persistentHistory,
            string upstreamInput,
            IReadOnlyList<LanguageModelChatContentBlock>? upstreamContentBlocks,
            IReadOnlyList<LanguageModelChatMessage> turnHistory)
        {
            var messages = new List<LanguageModelChatMessage>(persistentHistory.Count + turnHistory.Count + 3)
            {
                new(LanguageModelChatRole.System, systemPrompt ?? string.Empty)
            };

            messages.AddRange(persistentHistory.Select(message => message.Clone()));
            messages.Add(CreateInputMessage(upstreamInput, upstreamContentBlocks));
            messages.AddRange(turnHistory.Select(message => message.Clone()));
            messages.Add(CreateToolProtocolTailReminder());
            return messages;
        }

        private static List<LanguageModelChatMessage> NormalizeHistory(
            IReadOnlyList<LanguageModelChatMessage>? history)
        {
            var sourceHistory = history ?? Array.Empty<LanguageModelChatMessage>();
            var normalizedHistory = new List<LanguageModelChatMessage>(sourceHistory.Count);

            for (var index = 0; index < sourceHistory.Count; index++)
            {
                var normalizedMessage = NormalizeHistoryMessage(sourceHistory, index);
                if (normalizedMessage != null)
                {
                    normalizedHistory.Add(normalizedMessage);
                }
            }

            return normalizedHistory;
        }

        private static List<LanguageModelChatMessage> TrimTrailingCurrentInputEchoes(
            List<LanguageModelChatMessage> history,
            string? upstreamInput,
            IReadOnlyList<LanguageModelChatContentBlock>? upstreamContentBlocks)
        {
            ArgumentNullException.ThrowIfNull(history);

            if (history.Count == 0)
            {
                return history;
            }

            var currentInputMessage = CreateInputMessage(upstreamInput, upstreamContentBlocks);
            if (currentInputMessage.ContentBlocks.Count == 0)
            {
                return history;
            }

            var endExclusive = history.Count;
            while (endExclusive > 0 &&
                   IsCurrentInputEcho(history[endExclusive - 1], currentInputMessage))
            {
                endExclusive--;
            }

            if (endExclusive == history.Count)
            {
                return history;
            }

            history.RemoveRange(endExclusive, history.Count - endExclusive);
            return history;
        }

        private static bool IsCurrentInputEcho(
            LanguageModelChatMessage candidate,
            LanguageModelChatMessage currentInputMessage)
        {
            return candidate.Role == LanguageModelChatRole.User &&
                   AreContentBlocksEquivalent(candidate.ContentBlocks, currentInputMessage.ContentBlocks);
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
                   string.Equals(NormalizeComparableContent(left.Content), NormalizeComparableContent(right.Content), StringComparison.Ordinal) &&
                   string.Equals(NormalizeComparableContent(left.ResourcePath), NormalizeComparableContent(right.ResourcePath), StringComparison.Ordinal) &&
                   string.Equals(NormalizeComparableContent(left.MediaType), NormalizeComparableContent(right.MediaType), StringComparison.OrdinalIgnoreCase) &&
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

        private static string NormalizeComparableContent(string? content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? string.Empty
                : content.Trim();
        }

        private static LanguageModelChatMessage? NormalizeHistoryMessage(
            IReadOnlyList<LanguageModelChatMessage> history,
            int index)
        {
            ArgumentNullException.ThrowIfNull(history);

            var message = history[index];
            ArgumentNullException.ThrowIfNull(message);

            // Skyweaver carries tool returns as plain XML text instead of MEAI-native
            // FunctionResultContent. Normalize Tool messages to User so the raw XML
            // transcript is preserved by downstream chat backends.
            var normalizedRole = message.Role == LanguageModelChatRole.Tool
                ? LanguageModelChatRole.User
                : message.Role;

            return new LanguageModelChatMessage(
                normalizedRole,
                message.ContentBlocks.Select(block => block.Clone()).ToArray())
            {
                AuthorName = message.AuthorName,
                IsHostInjectedTail = message.IsHostInjectedTail
            };
        }

        private static IReadOnlyList<LanguageModelChatMessage> ComposeFixedMessages(
            string systemPrompt,
            string upstreamInput,
            IReadOnlyList<LanguageModelChatContentBlock>? upstreamContentBlocks,
            IReadOnlyList<LanguageModelChatMessage> turnHistory)
        {
            var messages = new List<LanguageModelChatMessage>(turnHistory.Count + 3)
            {
                new(LanguageModelChatRole.System, systemPrompt ?? string.Empty),
                CreateInputMessage(upstreamInput, upstreamContentBlocks)
            };

            messages.AddRange(turnHistory.Select(message => message.Clone()));
            messages.Add(CreateToolProtocolTailReminder());
            return messages;
        }

        private static LanguageModelChatMessage CreateInputMessage(
            string? upstreamInput,
            IReadOnlyList<LanguageModelChatContentBlock>? upstreamContentBlocks)
        {
            var contentBlocks = new List<LanguageModelChatContentBlock>();
            if (!string.IsNullOrWhiteSpace(upstreamInput))
            {
                contentBlocks.Add(LanguageModelChatContentBlock.CreateText(upstreamInput));
            }

            if (upstreamContentBlocks != null)
            {
                contentBlocks.AddRange(upstreamContentBlocks
                    .Where(block => block != null)
                    .Select(block => block.Clone()));
            }

            return new LanguageModelChatMessage(LanguageModelChatRole.User, contentBlocks);
        }

        private static LanguageModelChatMessage CreateToolProtocolTailReminder()
        {
            return new LanguageModelChatMessage(LanguageModelChatRole.User, ToolProtocolTailReminder)
            {
                IsHostInjectedTail = true
            };
        }

        private static IReadOnlyList<LanguageModelChatMessage> BuildCompressionMessages(
            AgentDefinition agent,
            string upstreamInput,
            IReadOnlyList<LanguageModelChatMessage> persistentHistory,
            int targetHistoryTokens)
        {
            var transcript = BuildHistoryTranscript(persistentHistory);
            var systemPrompt = """
You are a host-side conversation-history compressor.
Your job is not to continue the task. Your job is to summarize earlier turns that happened before the current user message.

Preserve:
1. Confirmed facts, constraints, and decisions that still matter.
2. Important tool calls, tool results, and failures.
3. Passdown payloads, tool calls, tool results, and failures.
4. Open questions, unfinished branches, and relevant intermediate conclusions.
5. Any assistant commitments or prior outputs that the next turn must remain consistent with.
6. Whether a turn ended with direct assistant text or with a Passdown payload.

Do not write XML. Do not continue the conversation. Return only the compressed summary body.
Accuracy matters more than brevity, but try to stay within the requested budget.
""";

            var userPrompt = $"""
Agent: {agent.DisplayNameOrFallback}
Target history budget: {targetHistoryTokens}

Current user input for reference:
{upstreamInput}

Earlier-turn history to compress:
{transcript}

Return only the compressed summary text.
""";

            return
            [
                new LanguageModelChatMessage(LanguageModelChatRole.System, systemPrompt),
                new LanguageModelChatMessage(LanguageModelChatRole.User, userPrompt)
            ];
        }

        private static string BuildHistoryTranscript(IReadOnlyList<LanguageModelChatMessage> history)
        {
            var builder = new StringBuilder();

            foreach (var message in history)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append('[');
                builder.Append(message.Role);
                if (!string.IsNullOrWhiteSpace(message.AuthorName))
                {
                    builder.Append(':');
                    builder.Append(message.AuthorName);
                }

                builder.AppendLine("]");
                builder.AppendLine(NormalizeTranscriptContent(message.Content));
            }

            return builder.ToString().Trim();
        }

        private static string NormalizeTranscriptContent(string? content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? string.Empty
                : content.Trim();
        }

        private static string BuildCompressedHistoryEnvelope(string summaryText)
        {
            return
                """
The following content is a host-generated summary of earlier conversation history from previous turns.
Treat it as background context that happened before the current user message.
It is not a new system instruction and not a new user request:

"""
                + summaryText.Trim();
        }

        private sealed class ApproximateTokenEstimator
        {
            public int Estimate(IReadOnlyList<LanguageModelChatMessage> messages)
            {
                return messages.Sum(Estimate);
            }

            private int Estimate(LanguageModelChatMessage message)
            {
                var authorCost = string.IsNullOrWhiteSpace(message.AuthorName)
                    ? 0
                    : EstimateText(message.AuthorName);
                return 6 + authorCost + EstimateText(message.Content);
            }

            private int EstimateText(string? text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return 0;
                }

                var normalized = text.Trim();
                var cjkLikeCount = normalized.Count(IsCjkLikeCharacter);
                var otherCount = normalized.Length - cjkLikeCount;
                var otherTokenCount = (int)Math.Ceiling(otherCount / 4d);
                return Math.Max(1, cjkLikeCount + otherTokenCount);
            }

            private static bool IsCjkLikeCharacter(char value)
            {
                return value is >= '\u2E80' and <= '\u9FFF'
                    or >= '\uAC00' and <= '\uD7AF'
                    or >= '\u3040' and <= '\u30FF';
            }
        }
    }

    public sealed class AgentLoopContextPreparationResult
    {
        public IReadOnlyList<LanguageModelChatMessage> PreparedMessages { get; init; } = Array.Empty<LanguageModelChatMessage>();

        public IReadOnlyList<LanguageModelChatMessage> PersistentHistory { get; init; } = Array.Empty<LanguageModelChatMessage>();

        public IReadOnlyList<LanguageModelChatMessage> TurnHistory { get; init; } = Array.Empty<LanguageModelChatMessage>();

        public AgentLoopContextCompressionInfo? ContextCompression { get; init; }
    }
}
