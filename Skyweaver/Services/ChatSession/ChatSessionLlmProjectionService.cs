using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.AgentLoop;

namespace Skyweaver.Services.ChatSession
{
    public enum LlmCompressionStrategy
    {
        None = 0,
        PreferSummaries = 1
    }

    public sealed class LlmProjectionProfile
    {
        public string ProfileId { get; set; } = "DefaultChatProfile";

        public bool IncludeHiddenAgentEntries { get; set; } = true;

        public bool IncludeInternalTools { get; set; }

        public bool IncludeReasoning { get; set; }

        public bool IncludeStatusMessages { get; set; }

        public bool IncludeToolProtocol { get; set; } = true;

        public int? TokenBudget { get; set; }

        public LlmCompressionStrategy CompressionStrategy { get; set; } = LlmCompressionStrategy.PreferSummaries;

        public static LlmProjectionProfile DefaultChatProfile()
        {
            return new LlmProjectionProfile();
        }
    }

    public sealed class ChatSessionProjectionTraceItem
    {
        public string EntryId { get; init; } = string.Empty;

        public string? BlockId { get; init; }

        public bool Included { get; init; }

        public string Reason { get; init; } = string.Empty;

        public LanguageModelChatRole? FinalRole { get; init; }

        public int TokenEstimate { get; init; }

        public string ProjectionProfileId { get; init; } = string.Empty;
    }

    public sealed class ChatSessionProjectionTrace
    {
        public List<ChatSessionProjectionTraceItem> Items { get; } = new();
    }

    public sealed class ChatSessionLlmProjectionResult
    {
        public IReadOnlyList<LanguageModelChatMessage> Messages { get; init; } =
            Array.Empty<LanguageModelChatMessage>();

        public ChatSessionProjectionTrace Trace { get; init; } = new();
    }

    public sealed class ChatSessionLlmProjectionService
    {
        public ChatSessionLlmProjectionResult Project(
            ChatSessionModel session,
            LlmProjectionProfile? profile = null,
            string? currentUserText = null,
            IReadOnlyList<LanguageModelChatContentBlock>? currentUserContentBlocks = null,
            string? nodeId = null,
            string? agentId = null)
        {
            ArgumentNullException.ThrowIfNull(session);

            lock (session.Transcript.SyncRoot)
            {
            var effectiveProfile = profile ?? LlmProjectionProfile.DefaultChatProfile();
            var trace = new ChatSessionProjectionTrace();
            var messages = new List<LanguageModelChatMessage>();
            var includedToolCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentTurnIds = session.Transcript.Turns
                .Where(turn => turn.Status is ChatSessionTurnStatus.Pending or ChatSessionTurnStatus.Running)
                .Select(turn => turn.TurnId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in SelectEntries(session.Transcript, effectiveProfile))
            {
                if (IsSyntheticRuntimeUserEntry(entry))
                {
                    Trace(entry, null, false, "Synthetic runtime turn entry.", null, trace, effectiveProfile);
                    continue;
                }

                if (IsRedundantFinalOutput(session.Transcript, entry))
                {
                    Trace(entry, null, false, "Duplicate final output already represented by streamed assistant message.", null, trace, effectiveProfile);
                    continue;
                }

                if (IsCurrentTurnEntry(entry, currentTurnIds, currentUserText, currentUserContentBlocks))
                {
                    Trace(entry, null, false, "Current turn entry is supplied separately.", null, trace, effectiveProfile);
                    continue;
                }

                if (!ShouldIncludeEntry(entry, effectiveProfile, includedToolCalls, out var exclusionReason))
                {
                    Trace(entry, null, false, exclusionReason, null, trace, effectiveProfile);
                    continue;
                }

                if (entry.Kind == ChatSessionTranscriptEntryKind.ToolOutput &&
                    !HasIncludedToolCall(entry, includedToolCalls))
                {
                    Trace(entry, null, false, "Tool output has no included parent tool call.", null, trace, effectiveProfile);
                    continue;
                }

                foreach (var block in entry.Blocks)
                {
                    if (!TryProjectBlock(entry, block, effectiveProfile, out var message, out var reason) || message == null)
                    {
                        Trace(entry, block, false, reason, null, trace, effectiveProfile);
                        continue;
                    }

                    messages.Add(message);
                    if (entry.Kind is ChatSessionTranscriptEntryKind.ToolCall or ChatSessionTranscriptEntryKind.MalformedToolCall &&
                        !string.IsNullOrWhiteSpace(entry.ToolCallId))
                    {
                        includedToolCalls.Add(entry.ToolCallId);
                    }

                    Trace(entry, block, true, reason, message.Role, trace, effectiveProfile);
                }
            }

            return new ChatSessionLlmProjectionResult
            {
                Messages = ApplyTokenBudget(messages, effectiveProfile),
                Trace = trace
            };
            }
        }

        private static IReadOnlyList<ChatSessionTranscriptEntry> SelectEntries(
            ChatSessionTranscript transcript,
            LlmProjectionProfile profile)
        {
            if (profile.CompressionStrategy != LlmCompressionStrategy.PreferSummaries)
            {
                return transcript.Entries.ToArray();
            }

            var summaryEntries = transcript.Entries
                .Where(entry => entry.Kind == ChatSessionTranscriptEntryKind.ContextCompression &&
                                entry.LlmPolicy == TranscriptLlmPolicy.Summarize)
                .ToArray();
            if (summaryEntries.Length == 0)
            {
                return transcript.Entries.ToArray();
            }

            var latestSummary = summaryEntries[^1];
            var latestSummaryIndex = transcript.Entries.IndexOf(latestSummary);
            return transcript.Entries
                .Where((_, index) => index >= latestSummaryIndex ||
                                     transcript.Entries[index].EntryId == latestSummary.EntryId)
                .ToArray();
        }

        private static bool ShouldIncludeEntry(
            ChatSessionTranscriptEntry entry,
            LlmProjectionProfile profile,
            ISet<string> includedToolCalls,
            out string reason)
        {
            if (entry.Visibility == TranscriptVisibility.InternalOnly && !profile.IncludeInternalTools)
            {
                reason = "Internal entry excluded by projection profile.";
                return false;
            }

            if (entry.Visibility == TranscriptVisibility.Hidden && !profile.IncludeHiddenAgentEntries)
            {
                reason = "Hidden entry excluded by projection profile.";
                return false;
            }

            if (entry.Kind == ChatSessionTranscriptEntryKind.Reasoning && !profile.IncludeReasoning)
            {
                reason = "Reasoning excluded by projection profile.";
                return false;
            }

            var isStatusEntry = entry.Kind is ChatSessionTranscriptEntryKind.SystemStatus
                or ChatSessionTranscriptEntryKind.ExecutionStatus;
            if (isStatusEntry && !profile.IncludeStatusMessages)
            {
                reason = "Status entry excluded by projection profile.";
                return false;
            }

            if (entry.LlmPolicy == TranscriptLlmPolicy.Exclude)
            {
                reason = "Entry policy is Exclude.";
                return false;
            }

            if (entry.LlmPolicy == TranscriptLlmPolicy.DebugOnly &&
                !string.Equals(profile.ProfileId, "DebugAgentProfile", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Debug-only entry excluded by projection profile.";
                return false;
            }

            if (entry.LlmPolicy == TranscriptLlmPolicy.ToolProtocol && !profile.IncludeToolProtocol)
            {
                reason = "Tool protocol excluded by projection profile.";
                return false;
            }

            reason = "Included by entry policy.";
            return true;
        }

        private static bool TryProjectBlock(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block,
            LlmProjectionProfile profile,
            out LanguageModelChatMessage? message,
            out string reason)
        {
            message = null;
            var role = ResolveRole(entry);

            if (block.Kind == ChatSessionTranscriptBlockKind.Image)
            {
                var resourcePath = Normalize(block.ResourcePath ?? block.Content);
                if (resourcePath.Length == 0)
                {
                    reason = "Image block has no resource path.";
                    return false;
                }

                message = new LanguageModelChatMessage(
                    role,
                    [LanguageModelChatContentBlock.CreateImage(resourcePath, block.MediaType)])
                {
                    AuthorName = ResolveAuthor(entry, role)
                };
                reason = "Image block included.";
                return true;
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.Audio)
            {
                var resourcePath = Normalize(block.ResourcePath ?? block.Content);
                if (resourcePath.Length == 0)
                {
                    reason = "Audio block has no resource path.";
                    return false;
                }

                message = new LanguageModelChatMessage(
                    role,
                    [LanguageModelChatContentBlock.CreateAudio(resourcePath, block.MediaType)])
                {
                    AuthorName = ResolveAuthor(entry, role)
                };
                reason = "Audio block included.";
                return true;
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.Video)
            {
                var resourcePath = Normalize(block.ResourcePath ?? block.Content);
                if (resourcePath.Length == 0)
                {
                    reason = "Video block has no resource path.";
                    return false;
                }

                message = new LanguageModelChatMessage(
                    role,
                    [LanguageModelChatContentBlock.CreateVideo(resourcePath, block.MediaType)])
                {
                    AuthorName = ResolveAuthor(entry, role)
                };
                reason = "Video block included.";
                return true;
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.Document)
            {
                var resourcePath = Normalize(block.ResourcePath ?? block.Content);
                if (resourcePath.Length == 0)
                {
                    reason = "Document block has no resource path.";
                    return false;
                }

                message = new LanguageModelChatMessage(
                    role,
                    [LanguageModelChatContentBlock.CreateDocument(resourcePath, block.MediaType)])
                {
                    AuthorName = ResolveAuthor(entry, role)
                };
                reason = "Document block included.";
                return true;
            }

            var content = BuildTextContent(entry, block);
            if (content.Length == 0)
            {
                reason = "Block has no projectable content.";
                return false;
            }

            message = new LanguageModelChatMessage(role, content)
            {
                AuthorName = ResolveAuthor(entry, role)
            };
            reason = "Text block included.";
            return true;
        }

        private static LanguageModelChatRole ResolveRole(ChatSessionTranscriptEntry entry)
        {
            if (entry.Kind == ChatSessionTranscriptEntryKind.ToolOutput ||
                entry.Role == ChatSessionParticipantRole.Tool)
            {
                return LanguageModelChatRole.Tool;
            }

            return entry.Role switch
            {
                ChatSessionParticipantRole.System or ChatSessionParticipantRole.Runtime => LanguageModelChatRole.System,
                ChatSessionParticipantRole.Assistant => LanguageModelChatRole.Assistant,
                _ => LanguageModelChatRole.User
            };
        }

        private static string? ResolveAuthor(ChatSessionTranscriptEntry entry, LanguageModelChatRole role)
        {
            if (role == LanguageModelChatRole.Tool)
            {
                return NullIfWhiteSpace(entry.ToolName);
            }

            if (role != LanguageModelChatRole.Assistant)
            {
                return null;
            }

            return NullIfWhiteSpace(entry.NodeTitle)
                ?? NullIfWhiteSpace(entry.AgentName);
        }

        private static string BuildTextContent(ChatSessionTranscriptEntry entry, ChatSessionTranscriptBlock block)
        {
            var content = Normalize(block.Content);
            if (content.Length == 0)
            {
                return string.Empty;
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.ToolInvocationXml)
            {
                content = AgentLoopCompactionStore.EnsureToolCallIdInToolInvocationXml(
                    content,
                    entry.ToolCallId);
            }
            else if (block.Kind == ChatSessionTranscriptBlockKind.ToolOutputXml)
            {
                content = AgentLoopCompactionStore.EnsureToolCallIdInToolsReturnXml(
                    content,
                    entry.ToolCallId);
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.Code)
            {
                return BuildCodeBlock(block, content);
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.ResourceReference)
            {
                return PreservedTextContentXml.TryParse(content, out var textContent)
                    ? textContent.Text
                    : EnsurePreservedContentWrapper(content);
            }

            return PrefixTitle(block.Title, content);
        }

        private static string BuildCodeBlock(ChatSessionTranscriptBlock block, string content)
        {
            var fence = string.IsNullOrWhiteSpace(block.Language)
                ? "```"
                : $"```{block.Language.Trim()}";

            return PrefixTitle(
                block.Title,
                $"{fence}{Environment.NewLine}{content}{Environment.NewLine}```");
        }

        private static string PrefixTitle(string? title, string content)
        {
            var normalizedTitle = Normalize(title);
            return normalizedTitle.Length == 0
                ? content
                : $"{normalizedTitle}{Environment.NewLine}{content}";
        }

        private static string EnsurePreservedContentWrapper(string content)
        {
            return content.StartsWith("<PreservedContent", StringComparison.OrdinalIgnoreCase)
                ? content
                : $"<PreservedContent>{content}</PreservedContent>";
        }

        private static bool IsCurrentTurnEntry(
            ChatSessionTranscriptEntry entry,
            IReadOnlySet<string> currentTurnIds,
            string? currentUserText,
            IReadOnlyList<LanguageModelChatContentBlock>? currentUserContentBlocks)
        {
            if (currentTurnIds.Contains(entry.TurnId))
            {
                return true;
            }

            if (entry.Kind != ChatSessionTranscriptEntryKind.UserMessage)
            {
                return false;
            }

            var normalizedCurrentUserText = Normalize(currentUserText);
            var currentResources = NormalizeResourceKeys(currentUserContentBlocks);
            if (normalizedCurrentUserText.Length == 0 && currentResources.Count == 0)
            {
                return false;
            }

            var entryText = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    entry.Blocks
                        .Where(block => block.Kind is ChatSessionTranscriptBlockKind.Text
                            or ChatSessionTranscriptBlockKind.Code
                            or ChatSessionTranscriptBlockKind.StructuredXml)
                        .Select(block => Normalize(block.Content))
                        .Where(content => content.Length > 0))
                .Trim();

            return string.Equals(entryText, normalizedCurrentUserText, StringComparison.Ordinal) &&
                   ResourceKeysEqual(NormalizeResourceKeys(entry.Blocks), currentResources);
        }

        private static bool HasIncludedToolCall(
            ChatSessionTranscriptEntry entry,
            ISet<string> includedToolCalls)
        {
            if (string.IsNullOrWhiteSpace(entry.ToolCallId))
            {
                return false;
            }

            return includedToolCalls.Contains(entry.ToolCallId);
        }

        private static bool IsSyntheticRuntimeUserEntry(ChatSessionTranscriptEntry entry)
        {
            return entry.Kind == ChatSessionTranscriptEntryKind.UserMessage &&
                   entry.Blocks.Count == 1 &&
                   string.Equals(
                       entry.Blocks[0].Content?.Trim(),
                       ChatSessionTranscriptWriter.SyntheticRuntimeTurnInputText,
                       StringComparison.Ordinal);
        }

        private static bool IsRedundantFinalOutput(
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry entry)
        {
            if (entry.Kind != ChatSessionTranscriptEntryKind.AgentFinalOutput)
            {
                return false;
            }

            var entryIndex = transcript.Entries.IndexOf(entry);
            if (entryIndex <= 0)
            {
                return false;
            }

            var finalText = GetComparableEntryText(entry);
            if (finalText.Length == 0)
            {
                return false;
            }

            return transcript.Entries
                .Take(entryIndex)
                .Any(candidate =>
                    candidate.Kind == ChatSessionTranscriptEntryKind.AgentMessage &&
                    candidate.Role == ChatSessionParticipantRole.Assistant &&
                    string.Equals(candidate.TurnId, entry.TurnId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.NodeId ?? string.Empty, entry.NodeId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetComparableEntryText(candidate), finalText, StringComparison.Ordinal));
        }

        private static string GetComparableEntryText(ChatSessionTranscriptEntry entry)
        {
            return string.Join(
                    Environment.NewLine + Environment.NewLine,
                    entry.Blocks
                        .Where(block => block.Kind is ChatSessionTranscriptBlockKind.Text
                            or ChatSessionTranscriptBlockKind.StructuredXml
                            or ChatSessionTranscriptBlockKind.Code)
                        .Select(block => block.Content)
                        .Where(content => !string.IsNullOrWhiteSpace(content)))
                .TrimEnd();
        }

        private static IReadOnlyList<LanguageModelChatMessage> ApplyTokenBudget(
            IReadOnlyList<LanguageModelChatMessage> messages,
            LlmProjectionProfile profile)
        {
            if (profile.TokenBudget is not int tokenBudget || tokenBudget <= 0)
            {
                return messages.ToArray();
            }

            var selected = new Stack<LanguageModelChatMessage>();
            var runningEstimate = 0;
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                var estimate = EstimateTokens(messages[index].Content);
                if (runningEstimate + estimate > tokenBudget && selected.Count > 0)
                {
                    break;
                }

                selected.Push(messages[index]);
                runningEstimate += estimate;
            }

            return selected.ToArray();
        }

        private static void Trace(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock? block,
            bool included,
            string reason,
            LanguageModelChatRole? finalRole,
            ChatSessionProjectionTrace trace,
            LlmProjectionProfile profile)
        {
            trace.Items.Add(new ChatSessionProjectionTraceItem
            {
                EntryId = entry.EntryId,
                BlockId = block?.BlockId,
                Included = included,
                Reason = reason,
                FinalRole = finalRole,
                TokenEstimate = EstimateTokens(block?.Content ?? string.Empty),
                ProjectionProfileId = profile.ProfileId
            });
        }

        private static IReadOnlyList<string> NormalizeResourceKeys(
            IEnumerable<LanguageModelChatContentBlock>? blocks)
        {
            if (blocks == null)
            {
                return Array.Empty<string>();
            }

            return blocks
                .Select(block => block.Kind switch
                {
                    LanguageModelChatContentBlockKind.Image => BuildResourceKey("image", block.ResourcePath ?? block.Content),
                    LanguageModelChatContentBlockKind.Audio => BuildResourceKey("audio", block.ResourcePath ?? block.Content),
                    LanguageModelChatContentBlockKind.Video => BuildResourceKey("video", block.ResourcePath ?? block.Content),
                    LanguageModelChatContentBlockKind.Document => BuildResourceKey("document", block.ResourcePath ?? block.Content),
                    LanguageModelChatContentBlockKind.HostPreservedContent => BuildResourceKey("host", block.Content),
                    _ => string.Empty
                })
                .Where(key => key.Length > 0)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }

        private static IReadOnlyList<string> NormalizeResourceKeys(
            IEnumerable<ChatSessionTranscriptBlock>? blocks)
        {
            if (blocks == null)
            {
                return Array.Empty<string>();
            }

            return blocks
                .Select(block => block.Kind switch
                {
                    ChatSessionTranscriptBlockKind.Image => BuildResourceKey("image", block.ResourcePath ?? block.Content),
                    ChatSessionTranscriptBlockKind.Audio => BuildResourceKey("audio", block.ResourcePath ?? block.Content),
                    ChatSessionTranscriptBlockKind.Video => BuildResourceKey("video", block.ResourcePath ?? block.Content),
                    ChatSessionTranscriptBlockKind.Document => BuildResourceKey("document", block.ResourcePath ?? block.Content),
                    ChatSessionTranscriptBlockKind.ResourceReference => BuildResourceKey("host", block.Content),
                    _ => string.Empty
                })
                .Where(key => key.Length > 0)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }

        private static string BuildResourceKey(string kind, string? value)
        {
            var normalizedValue = Normalize(value);
            return normalizedValue.Length == 0 ? string.Empty : $"{kind}:{normalizedValue}";
        }

        private static bool ResourceKeysEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            return left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);
        }

        private static int EstimateTokens(string? content)
        {
            var length = string.IsNullOrEmpty(content) ? 0 : content.Length;
            return Math.Max(1, (int)Math.Ceiling(length / 4.0));
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
