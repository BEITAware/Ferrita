using System.Xml.Linq;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Services.ChatSession
{
    public sealed class ChatSessionUserInput
    {
        public string Text { get; init; } = string.Empty;

        public IReadOnlyList<LanguageModelChatContentBlock> ContentBlocks { get; init; } =
            Array.Empty<LanguageModelChatContentBlock>();
    }

    public sealed class ChatSessionTranscriptWriter
    {
        internal const string SyntheticRuntimeTurnInputText = "(runtime turn without explicit user input)";

        private readonly object _syncRoot = new();
        private readonly Dictionary<string, string> _activeTurnIds = new(StringComparer.OrdinalIgnoreCase);

        public ChatSessionTurnRecord BeginTurn(ChatSessionModel session, ChatSessionUserInput input)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(input);

            lock (_syncRoot)
            {
                var transcript = session.Transcript;
                var turn = new ChatSessionTurnRecord
                {
                    TurnId = Guid.NewGuid().ToString("N"),
                    TurnNumber = transcript.Turns.Count + 1,
                    StartedAtUtc = DateTime.UtcNow,
                    Status = ChatSessionTurnStatus.Running
                };

                var userEntry = new ChatSessionTranscriptEntry
                {
                    EntryId = Guid.NewGuid().ToString("N"),
                    TurnId = turn.TurnId,
                    Kind = ChatSessionTranscriptEntryKind.UserMessage,
                    Role = ChatSessionParticipantRole.User,
                    TimestampUtc = DateTime.UtcNow,
                    Visibility = TranscriptVisibility.Visible,
                    LlmPolicy = TranscriptLlmPolicy.Include,
                    HandoffPolicy = TranscriptHandoffPolicy.ExcludeByDefault,
                    Status = ChatSessionEntryStatus.Completed
                };

                var normalizedText = Normalize(input.Text);
                if (normalizedText.Length > 0)
                {
                    userEntry.Blocks.Add(new ChatSessionTranscriptBlock
                    {
                        Kind = ChatSessionTranscriptBlockKind.Text,
                        Content = normalizedText
                    });
                }

                foreach (var block in input.ContentBlocks.Where(block => block != null))
                {
                    var transcriptBlock = CreateUserContentBlock(block);
                    if (transcriptBlock != null)
                    {
                        userEntry.Blocks.Add(transcriptBlock);
                    }
                }

                if (userEntry.Blocks.Count == 0)
                {
                    throw new InvalidOperationException("用户输入不能为空。");
                }

                turn.UserEntryId = userEntry.EntryId;
                transcript.Turns.Add(turn);
                transcript.Entries.Add(userEntry);
                userEntry.Touch();
                transcript.Touch();
                _activeTurnIds[session.SessionId] = turn.TurnId;
                session.ContextSummary = BuildContextSummary(session);
                return turn;
            }
        }

        public void ApplyRuntimeEvent(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(runtimeEvent);

            lock (_syncRoot)
            {
                switch (runtimeEvent.Kind)
                {
                    case ChatSessionRuntimeEventKind.ExecutionStarted:
                        MarkTurnRunning(session, runtimeEvent);
                        AppendStatusEntry(session, runtimeEvent, ChatSessionTranscriptEntryKind.ExecutionStatus);
                        break;

                    case ChatSessionRuntimeEventKind.NodeStarted:
                    case ChatSessionRuntimeEventKind.NodeCompleted:
                    case ChatSessionRuntimeEventKind.AgentIterationStarted:
                    case ChatSessionRuntimeEventKind.AgentIterationCompleted:
                        AppendStatusEntry(session, runtimeEvent, ChatSessionTranscriptEntryKind.ExecutionStatus);
                        break;

                    case ChatSessionRuntimeEventKind.TextDelta:
                        AppendTextDelta(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.ReasoningDelta:
                        AppendReasoningDelta(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.ToolCallStarted:
                    case ChatSessionRuntimeEventKind.ToolCallUpdated:
                        UpsertToolCall(session, runtimeEvent, isMalformed: false);
                        break;

                    case ChatSessionRuntimeEventKind.MalformedToolCall:
                        UpsertToolCall(session, runtimeEvent, isMalformed: true);
                        break;

                    case ChatSessionRuntimeEventKind.ToolOutputReceived:
                        AppendToolOutput(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.ContextCompressionApplied:
                        AppendContextCompression(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.AgentFinalOutputProduced:
                        AppendFinalOutput(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.StructuredOutputProduced:
                        AppendStructuredPayload(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.ExecutionCompleted:
                        CompleteTurn(session, runtimeEvent);
                        break;

                    case ChatSessionRuntimeEventKind.ExecutionFailed:
                        FailTurn(session, runtimeEvent.Message ?? "执行失败。");
                        break;

                    case ChatSessionRuntimeEventKind.ExecutionCancelled:
                        CancelTurn(session, runtimeEvent.Message ?? "执行已取消。");
                        break;
                }

                session.ContextSummary = BuildContextSummary(session);
            }
        }

        public void CompleteTurn(ChatSessionModel session, ChatSessionRuntimeResult result)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(result);

            lock (_syncRoot)
            {
                if (result.IsCancelled)
                {
                    CancelTurnCore(session, result.FailureReason ?? "执行已取消。");
                }
                else if (!result.IsCompleted)
                {
                    FailTurnCore(session, result.FailureReason ?? "执行失败。");
                }
                else
                {
                    CompleteTurnCore(session);
                }

                session.ContextSummary = BuildContextSummary(session);
            }
        }

        public void FailTurn(ChatSessionModel session, string message)
        {
            ArgumentNullException.ThrowIfNull(session);

            lock (_syncRoot)
            {
                FailTurnCore(session, message);
                session.ContextSummary = BuildContextSummary(session);
            }
        }

        public void CancelTurn(ChatSessionModel session, string message)
        {
            ArgumentNullException.ThrowIfNull(session);

            lock (_syncRoot)
            {
                CancelTurnCore(session, message);
                session.ContextSummary = BuildContextSummary(session);
            }
        }

        private void MarkTurnRunning(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            var turn = GetOrCreateActiveTurn(session);
            if (turn.Status == ChatSessionTurnStatus.Pending)
            {
                turn.Status = ChatSessionTurnStatus.Running;
                Touch(session.Transcript);
            }
        }

        private void AppendTextDelta(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            if (string.IsNullOrEmpty(runtimeEvent.TextDelta))
            {
                return;
            }

            var entry = FindOrCreateStreamingEntry(
                session,
                runtimeEvent,
                ChatSessionTranscriptEntryKind.AgentMessage,
                ChatSessionParticipantRole.Assistant,
                runtimeEvent.TextDeltaOutputKind == AgentLoopOutputKind.StructuredXml
                    ? ChatSessionTranscriptBlockKind.StructuredXml
                    : ChatSessionTranscriptBlockKind.Text,
                runtimeEvent.TextDeltaOutputKind == AgentLoopOutputKind.StructuredXml ? "结构化 XML" : null,
                TranscriptLlmPolicy.Include,
                TranscriptHandoffPolicy.ExcludeByDefault);

            AppendToSingleBlock(session.Transcript, entry, runtimeEvent.TextDelta);
        }

        private void AppendReasoningDelta(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            if (string.IsNullOrEmpty(runtimeEvent.ReasoningDelta))
            {
                return;
            }

            var entry = FindOrCreateStreamingEntry(
                session,
                runtimeEvent,
                ChatSessionTranscriptEntryKind.Reasoning,
                ChatSessionParticipantRole.Assistant,
                ChatSessionTranscriptBlockKind.ReasoningText,
                "推理过程",
                TranscriptLlmPolicy.Exclude,
                TranscriptHandoffPolicy.ExcludeByDefault);

            entry.Visibility = runtimeEvent.IsHiddenAgent
                ? TranscriptVisibility.Hidden
                : runtimeEvent.IsReasoningCollapsible
                    ? TranscriptVisibility.Collapsed
                    : TranscriptVisibility.Visible;
            entry.Metadata["ReasoningCollapsible"] = runtimeEvent.IsReasoningCollapsible ? "true" : "false";
            var block = entry.Blocks.FirstOrDefault();
            if (block != null)
            {
                block.Metadata["ReasoningCollapsible"] = runtimeEvent.IsReasoningCollapsible ? "true" : "false";
            }

            AppendToSingleBlock(session.Transcript, entry, runtimeEvent.ReasoningDelta);
        }

        private void UpsertToolCall(
            ChatSessionModel session,
            ChatSessionRuntimeEvent runtimeEvent,
            bool isMalformed)
        {
            if (string.IsNullOrWhiteSpace(runtimeEvent.ToolXml) &&
                runtimeEvent.ToolCallSnapshot == null &&
                runtimeEvent.ToolInvocation == null)
            {
                return;
            }

            var transcript = session.Transcript;
            var turn = GetOrCreateActiveTurn(session);
            var toolCallId = NormalizeToolCallId(runtimeEvent);
            var entry = transcript.Entries.LastOrDefault(candidate =>
                string.Equals(candidate.TurnId, turn.TurnId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase) &&
                candidate.Kind is ChatSessionTranscriptEntryKind.ToolCall or ChatSessionTranscriptEntryKind.MalformedToolCall);

            if (entry == null)
            {
                entry = CreateEntry(
                    turn,
                    runtimeEvent,
                    isMalformed ? ChatSessionTranscriptEntryKind.MalformedToolCall : ChatSessionTranscriptEntryKind.ToolCall,
                    ChatSessionParticipantRole.Assistant,
                    ResolveVisibility(runtimeEvent, defaultVisibility: TranscriptVisibility.Collapsed),
                    isMalformed ? TranscriptLlmPolicy.Exclude : TranscriptLlmPolicy.ToolProtocol,
                    TranscriptHandoffPolicy.ExcludeByDefault);
                entry.ToolCallId = toolCallId;
                entry.ToolCallIndex = runtimeEvent.ToolCallIndex;
                entry.ToolName = ResolveToolName(runtimeEvent);
                transcript.Entries.Add(entry);
            }

            entry.Kind = isMalformed ? ChatSessionTranscriptEntryKind.MalformedToolCall : ChatSessionTranscriptEntryKind.ToolCall;
            entry.Status = isMalformed
                ? ChatSessionEntryStatus.Malformed
                : runtimeEvent.ToolCallSnapshot?.IsInvocationClosed == true || runtimeEvent.ToolInvocation != null
                    ? ChatSessionEntryStatus.Completed
                    : ChatSessionEntryStatus.Streaming;
            entry.ToolName = ResolveToolName(runtimeEvent) ?? entry.ToolName;

            var content = FirstNonEmpty(
                runtimeEvent.ToolInvocation?.InvocationXml,
                runtimeEvent.ToolXml,
                runtimeEvent.ToolCallSnapshot?.ToolXmlFragment,
                runtimeEvent.Message);
            if (content.Length > 0)
            {
                var block = entry.Blocks.FirstOrDefault()
                    ?? AddBlock(entry, ChatSessionTranscriptBlockKind.ToolInvocationXml, content);
                block.Content = content;
                block.Title = entry.ToolName;
                block.Touch();
            }

            if (isMalformed && !string.IsNullOrWhiteSpace(runtimeEvent.Message))
            {
                entry.Metadata["Error"] = runtimeEvent.Message.Trim();
            }

            Touch(transcript, entry);
        }

        private void AppendToolOutput(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            var output = Normalize(runtimeEvent.ToolOutputXml);
            if (output.Length == 0)
            {
                output = Normalize(runtimeEvent.Message);
            }

            if (output.Length == 0)
            {
                return;
            }

            var transcript = session.Transcript;
            var turn = GetOrCreateActiveTurn(session);
            var toolCallId = NormalizeToolCallId(runtimeEvent);
            var presentationHints = ResolveToolOutputPresentationHints(runtimeEvent.ToolReturns);
            var parent = transcript.Entries.LastOrDefault(candidate =>
                string.Equals(candidate.TurnId, turn.TurnId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ToolCallId, toolCallId, StringComparison.OrdinalIgnoreCase) &&
                candidate.Kind is ChatSessionTranscriptEntryKind.ToolCall or ChatSessionTranscriptEntryKind.MalformedToolCall);

            var entry = CreateEntry(
                turn,
                runtimeEvent,
                ChatSessionTranscriptEntryKind.ToolOutput,
                ChatSessionParticipantRole.Tool,
                ResolveVisibility(
                    runtimeEvent,
                    defaultVisibility: presentationHints.IsUserVisible
                        ? TranscriptVisibility.Visible
                        : TranscriptVisibility.Hidden),
                TranscriptLlmPolicy.ToolProtocol,
                TranscriptHandoffPolicy.Evidence);
            entry.ParentEntryId = parent?.EntryId;
            entry.ToolCallId = toolCallId;
            entry.ToolCallIndex = runtimeEvent.ToolCallIndex;
            entry.ToolName = ResolveToolName(runtimeEvent) ?? parent?.ToolName;
            entry.Status = ChatSessionEntryStatus.Completed;
            var block = new ChatSessionTranscriptBlock
            {
                Kind = ChatSessionTranscriptBlockKind.ToolOutputXml,
                Content = output,
                Title = entry.ToolName
            };
            ApplyToolOutputPresentationMetadata(block, presentationHints);
            entry.Blocks.Add(block);

            transcript.Entries.Add(entry);
            Touch(transcript, entry);
        }

        private void AppendContextCompression(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            var info = runtimeEvent.ContextCompression;
            var content = info == null
                ? Normalize(runtimeEvent.Message)
                : string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        Normalize(runtimeEvent.Message),
                        $"CompressionModelId: {info.CompressionModelId}",
                        $"ContextWindowTokens: {info.ContextWindowTokens}",
                        $"EstimatedBefore: {info.EstimatedTokenCountBeforeCompression}",
                        $"EstimatedAfter: {info.EstimatedTokenCountAfterCompression}",
                        $"TargetAfter: {info.TargetTokenCountAfterCompression}"
                    }.Where(line => !string.IsNullOrWhiteSpace(line)));

            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var turn = GetOrCreateActiveTurn(session);
            var entry = CreateEntry(
                turn,
                runtimeEvent,
                ChatSessionTranscriptEntryKind.ContextCompression,
                ChatSessionParticipantRole.Runtime,
                TranscriptVisibility.DebugOnly,
                TranscriptLlmPolicy.Summarize,
                TranscriptHandoffPolicy.Summary);
            entry.Blocks.Add(new ChatSessionTranscriptBlock
            {
                Kind = ChatSessionTranscriptBlockKind.CompressedSummary,
                Content = content,
                Title = "上下文压缩"
            });

            session.Transcript.Entries.Add(entry);
            Touch(session.Transcript, entry);
        }

        private void AppendFinalOutput(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            if (runtimeEvent.Payload == null)
            {
                return;
            }

            var turn = GetOrCreateActiveTurn(session);
            var payloadBlock = CreatePayloadBlock(runtimeEvent.Payload);
            var existingVisibleOutput = FindEquivalentVisibleAgentMessage(
                session.Transcript,
                turn,
                runtimeEvent,
                payloadBlock.Content);
            if (existingVisibleOutput != null)
            {
                existingVisibleOutput.Kind = ChatSessionTranscriptEntryKind.AgentFinalOutput;
                existingVisibleOutput.Status = ChatSessionEntryStatus.Completed;
                existingVisibleOutput.LlmPolicy = TranscriptLlmPolicy.Include;
                existingVisibleOutput.HandoffPolicy = TranscriptHandoffPolicy.FinalOutput;
                existingVisibleOutput.Metadata["IsPayloadFromPassdown"] = runtimeEvent.IsPayloadFromPassdown ? "true" : "false";
                turn.FinalEntryId = existingVisibleOutput.EntryId;
                Touch(session.Transcript, existingVisibleOutput);
                return;
            }

            var entry = CreateEntry(
                turn,
                runtimeEvent,
                ChatSessionTranscriptEntryKind.AgentFinalOutput,
                ChatSessionParticipantRole.Assistant,
                ResolveVisibility(runtimeEvent, defaultVisibility: TranscriptVisibility.Visible),
                TranscriptLlmPolicy.Include,
                TranscriptHandoffPolicy.FinalOutput);
            entry.Status = ChatSessionEntryStatus.Completed;
            entry.Metadata["IsPayloadFromPassdown"] = runtimeEvent.IsPayloadFromPassdown ? "true" : "false";
            entry.Blocks.Add(payloadBlock);

            session.Transcript.Entries.Add(entry);
            turn.FinalEntryId = entry.EntryId;
            Touch(session.Transcript, entry);
        }

        private void AppendStructuredPayload(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            if (runtimeEvent.Payload == null || !runtimeEvent.Payload.IsStructuredXml)
            {
                return;
            }

            var turn = GetOrCreateActiveTurn(session);
            var entry = CreateEntry(
                turn,
                runtimeEvent,
                ChatSessionTranscriptEntryKind.StructuredPayload,
                ChatSessionParticipantRole.Runtime,
                TranscriptVisibility.DebugOnly,
                TranscriptLlmPolicy.Exclude,
                TranscriptHandoffPolicy.Evidence);
            entry.Blocks.Add(CreatePayloadBlock(runtimeEvent.Payload));

            session.Transcript.Entries.Add(entry);
            Touch(session.Transcript, entry);
        }

        private void AppendStatusEntry(
            ChatSessionModel session,
            ChatSessionRuntimeEvent runtimeEvent,
            ChatSessionTranscriptEntryKind kind)
        {
            var content = Normalize(runtimeEvent.Message);
            if (content.Length == 0 && runtimeEvent.Kind is not ChatSessionRuntimeEventKind.ExecutionStarted)
            {
                return;
            }

            var turn = GetOrCreateActiveTurn(session);
            var entry = CreateEntry(
                turn,
                runtimeEvent,
                kind,
                ChatSessionParticipantRole.Runtime,
                TranscriptVisibility.DebugOnly,
                TranscriptLlmPolicy.Exclude,
                TranscriptHandoffPolicy.ExcludeByDefault);
            entry.Blocks.Add(new ChatSessionTranscriptBlock
            {
                Kind = ChatSessionTranscriptBlockKind.StatusText,
                Content = content.Length == 0 ? runtimeEvent.Kind.ToString() : content,
                Title = runtimeEvent.Kind.ToString()
            });

            session.Transcript.Entries.Add(entry);
            Touch(session.Transcript, entry);
        }

        private void CompleteTurn(ChatSessionModel session, ChatSessionRuntimeEvent runtimeEvent)
        {
            if (TryGetActiveTurn(session, out var turn) && turn != null)
            {
                AppendStatusEntry(session, runtimeEvent, ChatSessionTranscriptEntryKind.ExecutionStatus);
            }

            CompleteTurnCore(session);
        }

        private void CompleteTurnCore(ChatSessionModel session)
        {
            if (!TryGetActiveTurn(session, out var turn) || turn == null)
            {
                return;
            }

            turn.Status = ChatSessionTurnStatus.Completed;
            turn.CompletedAtUtc = DateTime.UtcNow;
            CompleteOpenEntries(session.Transcript, ChatSessionEntryStatus.Completed);
            _activeTurnIds.Remove(session.SessionId);
            Touch(session.Transcript);
        }

        private void FailTurnCore(ChatSessionModel session, string message)
        {
            var turn = GetOrCreateActiveTurn(session);
            turn.Status = ChatSessionTurnStatus.Failed;
            turn.CompletedAtUtc = DateTime.UtcNow;
            CompleteOpenEntries(session.Transcript, ChatSessionEntryStatus.Failed);

            var entry = new ChatSessionTranscriptEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                TurnId = turn.TurnId,
                Kind = ChatSessionTranscriptEntryKind.Error,
                Role = ChatSessionParticipantRole.Runtime,
                TimestampUtc = DateTime.UtcNow,
                Visibility = TranscriptVisibility.Visible,
                LlmPolicy = TranscriptLlmPolicy.Exclude,
                HandoffPolicy = TranscriptHandoffPolicy.ExcludeByDefault,
                Status = ChatSessionEntryStatus.Failed
            };
            entry.Blocks.Add(new ChatSessionTranscriptBlock
            {
                Kind = ChatSessionTranscriptBlockKind.ErrorText,
                Content = Normalize(message),
                Title = "执行失败"
            });

            session.Transcript.Entries.Add(entry);
            _activeTurnIds.Remove(session.SessionId);
            Touch(session.Transcript, entry);
        }

        private void CancelTurnCore(ChatSessionModel session, string message)
        {
            var turn = GetOrCreateActiveTurn(session);
            turn.Status = ChatSessionTurnStatus.Cancelled;
            turn.CompletedAtUtc = DateTime.UtcNow;
            CompleteOpenEntries(session.Transcript, ChatSessionEntryStatus.Cancelled);

            var entry = new ChatSessionTranscriptEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                TurnId = turn.TurnId,
                Kind = ChatSessionTranscriptEntryKind.SystemStatus,
                Role = ChatSessionParticipantRole.Runtime,
                TimestampUtc = DateTime.UtcNow,
                Visibility = TranscriptVisibility.Visible,
                LlmPolicy = TranscriptLlmPolicy.Exclude,
                HandoffPolicy = TranscriptHandoffPolicy.ExcludeByDefault,
                Status = ChatSessionEntryStatus.Cancelled
            };
            entry.Blocks.Add(new ChatSessionTranscriptBlock
            {
                Kind = ChatSessionTranscriptBlockKind.StatusText,
                Content = Normalize(message),
                Title = "执行已取消"
            });

            session.Transcript.Entries.Add(entry);
            _activeTurnIds.Remove(session.SessionId);
            Touch(session.Transcript, entry);
        }

        private ChatSessionTranscriptEntry FindOrCreateStreamingEntry(
            ChatSessionModel session,
            ChatSessionRuntimeEvent runtimeEvent,
            ChatSessionTranscriptEntryKind kind,
            ChatSessionParticipantRole role,
            ChatSessionTranscriptBlockKind blockKind,
            string? title,
            TranscriptLlmPolicy llmPolicy,
            TranscriptHandoffPolicy handoffPolicy)
        {
            var transcript = session.Transcript;
            var turn = GetOrCreateActiveTurn(session);
            var key = BuildStreamingKey(kind, runtimeEvent, blockKind);
            var entry = transcript.Entries.LastOrDefault(candidate =>
                string.Equals(candidate.TurnId, turn.TurnId, StringComparison.OrdinalIgnoreCase) &&
                candidate.Kind == kind &&
                candidate.Status == ChatSessionEntryStatus.Streaming &&
                candidate.Metadata.TryGetValue("StreamingKey", out var existingKey) &&
                string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase));

            if (entry != null)
            {
                return entry;
            }

            entry = CreateEntry(
                turn,
                runtimeEvent,
                kind,
                role,
                ResolveVisibility(runtimeEvent, defaultVisibility: TranscriptVisibility.Visible),
                llmPolicy,
                handoffPolicy);
            entry.Status = ChatSessionEntryStatus.Streaming;
            entry.Metadata["StreamingKey"] = key;
            entry.Blocks.Add(new ChatSessionTranscriptBlock
            {
                Kind = blockKind,
                Title = title
            });

            transcript.Entries.Add(entry);
            Touch(transcript, entry);
            return entry;
        }

        private static string BuildStreamingKey(
            ChatSessionTranscriptEntryKind kind,
            ChatSessionRuntimeEvent runtimeEvent,
            ChatSessionTranscriptBlockKind blockKind)
        {
            return string.Join(
                ":",
                kind,
                runtimeEvent.NodeId ?? string.Empty,
                runtimeEvent.AgentId ?? string.Empty,
                runtimeEvent.IterationNumber?.ToString() ?? string.Empty,
                runtimeEvent.PartIndex?.ToString() ?? string.Empty,
                blockKind);
        }

        private static void AppendToSingleBlock(
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry entry,
            string delta)
        {
            var block = entry.Blocks.FirstOrDefault()
                ?? AddBlock(entry, ChatSessionTranscriptBlockKind.Text, string.Empty);
            block.Content += delta;
            block.Touch();
            Touch(transcript, entry);
        }

        private static ChatSessionTranscriptEntry CreateEntry(
            ChatSessionTurnRecord turn,
            ChatSessionRuntimeEvent runtimeEvent,
            ChatSessionTranscriptEntryKind kind,
            ChatSessionParticipantRole role,
            TranscriptVisibility visibility,
            TranscriptLlmPolicy llmPolicy,
            TranscriptHandoffPolicy handoffPolicy)
        {
            var entry = new ChatSessionTranscriptEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                TurnId = turn.TurnId,
                Kind = kind,
                Role = role,
                TimestampUtc = DateTime.UtcNow,
                NodeId = NullIfWhiteSpace(runtimeEvent.NodeId),
                NodeTitle = NullIfWhiteSpace(runtimeEvent.NodeTitle),
                AgentId = NullIfWhiteSpace(runtimeEvent.AgentId),
                AgentName = NullIfWhiteSpace(runtimeEvent.NodeTitle),
                IterationNumber = runtimeEvent.IterationNumber,
                Visibility = visibility,
                LlmPolicy = llmPolicy,
                HandoffPolicy = handoffPolicy,
                Status = ChatSessionEntryStatus.Completed
            };

            if (!string.IsNullOrWhiteSpace(runtimeEvent.ModelId))
            {
                entry.Metadata["ModelId"] = runtimeEvent.ModelId.Trim();
            }

            if (runtimeEvent.NodeKind != null)
            {
                entry.Metadata["NodeKind"] = runtimeEvent.NodeKind.Value.ToString();
            }

            return entry;
        }

        private ChatSessionTurnRecord GetOrCreateActiveTurn(ChatSessionModel session)
        {
            if (TryGetActiveTurn(session, out var turn) && turn != null)
            {
                return turn;
            }

            return CreateSyntheticRuntimeTurn(session);
        }

        private ChatSessionTurnRecord CreateSyntheticRuntimeTurn(ChatSessionModel session)
        {
            var transcript = session.Transcript;
            var turn = new ChatSessionTurnRecord
            {
                TurnId = Guid.NewGuid().ToString("N"),
                TurnNumber = transcript.Turns.Count + 1,
                StartedAtUtc = DateTime.UtcNow,
                Status = ChatSessionTurnStatus.Running
            };
            turn.Metadata["SyntheticRuntimeTurn"] = "true";

            transcript.Turns.Add(turn);
            _activeTurnIds[session.SessionId] = turn.TurnId;
            Touch(transcript);
            return turn;
        }

        private bool TryGetActiveTurn(ChatSessionModel session, out ChatSessionTurnRecord? turn)
        {
            turn = null;
            if (_activeTurnIds.TryGetValue(session.SessionId, out var turnId))
            {
                turn = session.Transcript.Turns.FirstOrDefault(candidate =>
                    string.Equals(candidate.TurnId, turnId, StringComparison.OrdinalIgnoreCase));
            }

            turn ??= session.Transcript.Turns.LastOrDefault(candidate =>
                candidate.Status is ChatSessionTurnStatus.Pending or ChatSessionTurnStatus.Running);
            if (turn != null)
            {
                _activeTurnIds[session.SessionId] = turn.TurnId;
                return true;
            }

            return false;
        }

        private static ChatSessionTranscriptBlock? CreateUserContentBlock(LanguageModelChatContentBlock source)
        {
            return source.Kind switch
            {
                LanguageModelChatContentBlockKind.Image => new ChatSessionTranscriptBlock
                {
                    Kind = ChatSessionTranscriptBlockKind.Image,
                    Content = source.ResourcePath ?? source.Content,
                    ResourcePath = source.ResourcePath ?? source.Content,
                    MediaType = source.MediaType
                },
                LanguageModelChatContentBlockKind.Audio => new ChatSessionTranscriptBlock
                {
                    Kind = ChatSessionTranscriptBlockKind.Audio,
                    Content = source.ResourcePath ?? source.Content,
                    ResourcePath = source.ResourcePath ?? source.Content,
                    MediaType = source.MediaType
                },
                LanguageModelChatContentBlockKind.HostPreservedContent => new ChatSessionTranscriptBlock
                {
                    Kind = ChatSessionTranscriptBlockKind.ResourceReference,
                    Content = source.Content
                },
                LanguageModelChatContentBlockKind.Text when !string.IsNullOrWhiteSpace(source.Content) =>
                    new ChatSessionTranscriptBlock
                    {
                        Kind = ChatSessionTranscriptBlockKind.Text,
                        Content = source.Content.Trim()
                    },
                _ => null
            };
        }

        private static ChatSessionTranscriptBlock CreatePayloadBlock(SessionFlowPayload payload)
        {
            return new ChatSessionTranscriptBlock
            {
                Kind = payload.IsStructuredXml
                    ? ChatSessionTranscriptBlockKind.StructuredXml
                    : ChatSessionTranscriptBlockKind.Text,
                Content = payload.Content
            };
        }

        private static ChatSessionTranscriptEntry? FindEquivalentVisibleAgentMessage(
            ChatSessionTranscript transcript,
            ChatSessionTurnRecord turn,
            ChatSessionRuntimeEvent runtimeEvent,
            string payloadContent)
        {
            var normalizedPayload = NormalizeComparable(payloadContent);
            if (normalizedPayload.Length == 0)
            {
                return null;
            }

            return transcript.Entries.LastOrDefault(entry =>
                string.Equals(entry.TurnId, turn.TurnId, StringComparison.OrdinalIgnoreCase) &&
                entry.Kind == ChatSessionTranscriptEntryKind.AgentMessage &&
                entry.Role == ChatSessionParticipantRole.Assistant &&
                entry.Visibility is TranscriptVisibility.Visible or TranscriptVisibility.Collapsed &&
                string.Equals(entry.NodeId ?? string.Empty, runtimeEvent.NodeId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetEntryText(entry), normalizedPayload, StringComparison.Ordinal));
        }

        private static string GetEntryText(ChatSessionTranscriptEntry entry)
        {
            return NormalizeComparable(string.Join(
                Environment.NewLine + Environment.NewLine,
                entry.Blocks
                    .Where(block => block.Kind is ChatSessionTranscriptBlockKind.Text
                        or ChatSessionTranscriptBlockKind.StructuredXml
                        or ChatSessionTranscriptBlockKind.Code)
                    .Select(block => block.Content)
                    .Where(content => !string.IsNullOrWhiteSpace(content))));
        }

        private static ChatSessionTranscriptBlock AddBlock(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlockKind kind,
            string content)
        {
            var block = new ChatSessionTranscriptBlock
            {
                Kind = kind,
                Content = content
            };
            entry.Blocks.Add(block);
            return block;
        }

        private static void CompleteOpenEntries(
            ChatSessionTranscript transcript,
            ChatSessionEntryStatus status)
        {
            foreach (var entry in transcript.Entries.Where(entry => entry.Status == ChatSessionEntryStatus.Streaming))
            {
                entry.Status = status == ChatSessionEntryStatus.Completed
                    ? ChatSessionEntryStatus.Completed
                    : status;
                entry.Touch();
            }
        }

        private static TranscriptVisibility ResolveVisibility(
            ChatSessionRuntimeEvent runtimeEvent,
            TranscriptVisibility defaultVisibility)
        {
            if (ChatSessionInternalToolVisibility.IsInternalToolRuntimeEvent(runtimeEvent))
            {
                return TranscriptVisibility.InternalOnly;
            }

            return runtimeEvent.IsHiddenAgent
                ? TranscriptVisibility.Hidden
                : defaultVisibility;
        }

        private static SkyweaverToolResultPresentationHints ResolveToolOutputPresentationHints(
            IReadOnlyList<SkyweaverToolReturnPayload> toolReturns)
        {
            return toolReturns
                .Select(item => item.Result.PresentationHints)
                .LastOrDefault(item => item != null && item.HasAnyValue)
                ?? SkyweaverToolResultPresentationHints.None;
        }

        private static void ApplyToolOutputPresentationMetadata(
            ChatSessionTranscriptBlock block,
            SkyweaverToolResultPresentationHints presentationHints)
        {
            if (!string.IsNullOrWhiteSpace(presentationHints.PresentationKind))
            {
                block.Metadata[SkyweaverToolResultPresentationMetadataKeys.PresentationKind] =
                    presentationHints.PresentationKind;
            }
        }

        private static string NormalizeToolCallId(ChatSessionRuntimeEvent runtimeEvent)
        {
            return ChatSessionToolCallIdGenerator.Normalize(runtimeEvent.ToolCallId) switch
            {
                { Length: > 0 } normalized => normalized,
                _ => $"UNTRACKED{runtimeEvent.IterationNumber ?? 0}{runtimeEvent.PartIndex ?? 0}{runtimeEvent.ToolCallIndex ?? 0}"
            };
        }

        private static string? ResolveToolName(ChatSessionRuntimeEvent runtimeEvent)
        {
            return NullIfWhiteSpace(runtimeEvent.ToolInvocation?.ToolName)
                ?? NullIfWhiteSpace(runtimeEvent.ToolCallSnapshot?.ToolName)
                ?? NullIfWhiteSpace(runtimeEvent.ToolReturns.Count == 1 ? runtimeEvent.ToolReturns[0].ToolName : null)
                ?? TryExtractToolName(runtimeEvent.ToolXml)
                ?? TryExtractToolName(runtimeEvent.ToolOutputXml);
        }

        private static string? TryExtractToolName(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            try
            {
                var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root;
                var element = root == null
                    ? null
                    : string.Equals(root.Name.LocalName, "Tool", StringComparison.OrdinalIgnoreCase)
                        ? root
                        : root.Descendants().FirstOrDefault(descendant =>
                            string.Equals(descendant.Name.LocalName, "Tool", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(descendant.Name.LocalName, "ToolReturn", StringComparison.OrdinalIgnoreCase));

                return element?.Attributes()
                    .FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ToolName", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(attribute.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    ?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static void Touch(ChatSessionTranscript transcript, ChatSessionTranscriptEntry entry)
        {
            entry.Touch();
            transcript.Touch();
        }

        private static void Touch(ChatSessionTranscript transcript)
        {
            transcript.Touch();
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string NormalizeComparable(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.TrimEnd();
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string BuildContextSummary(ChatSessionModel session)
        {
            var completedTurns = session.Transcript.Turns.Count(turn => turn.Status == ChatSessionTurnStatus.Completed);
            return session.Transcript.Entries.Count == 0
                ? "空会话。"
                : $"会话记录 {session.Transcript.Entries.Count} 条，已完成轮次 {completedTurns} 次。";
        }
    }
}
