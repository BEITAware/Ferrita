using Skyweaver.Controls.ChatSessionControl.Models;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.PresentationUI;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Services.ChatSession
{
    public sealed class ChatSessionPresentationProjector
    {
        private const string UserAvatarPath = "pack://application:,,,/Resources/image.png";
        private const string AssistantAvatarPath = "pack://application:,,,/Resources/GuideBot.png";
        private const string SystemAvatarPath = "pack://application:,,,/Resources/QuestionBot.png";

        public IReadOnlyList<ChatMessageModel> Project(
            ChatSessionTranscript transcript,
            bool includeDebugEntries = false)
        {
            ArgumentNullException.ThrowIfNull(transcript);

            var messages = new List<ChatMessageModel>();
            var assistantGroups = new Dictionary<string, ChatMessageModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in transcript.Entries)
            {
                if (!ShouldProjectEntry(transcript, entry, includeDebugEntries))
                {
                    continue;
                }

                if (ShouldGroupAsAssistantBubble(entry))
                {
                    var groupKey = BuildAssistantGroupKey(entry);
                    if (!assistantGroups.TryGetValue(groupKey, out var groupedMessage))
                    {
                        groupedMessage = CreatePresentationMessage(entry, groupKey);
                        assistantGroups[groupKey] = groupedMessage;
                        messages.Add(groupedMessage);
                    }

                    AddEntryParts(groupedMessage, transcript, entry);
                    continue;
                }

                var message = CreatePresentationMessage(entry, entry.EntryId);
                AddEntryParts(message, transcript, entry);
                if (message.Parts.Count > 0)
                {
                    messages.Add(message);
                }
            }

            return messages.Where(message => message.Parts.Count > 0).ToArray();
        }

        private static ChatMessageModel CreatePresentationMessage(
            ChatSessionTranscriptEntry entry,
            string sourceEntryId)
        {
            var role = ToPresentationRole(entry);
            return new ChatMessageModel(
                role,
                ResolveDisplayName(entry, role),
                GetAvatarPath(role),
                entry.TimestampUtc.ToLocalTime(),
                sourceEntryId: sourceEntryId);
        }

        private static void AddEntryParts(
            ChatMessageModel message,
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry entry)
        {
            if (!message.SourceEntryIds.Contains(entry.EntryId, StringComparer.OrdinalIgnoreCase))
            {
                message.SourceEntryIds.Add(entry.EntryId);
            }

            foreach (var block in entry.Blocks)
            {
                if (!ShouldProjectBlock(block))
                {
                    continue;
                }

                var part = ToPresentationPart(entry, block);
                MergeToolOutputPresentation(message, transcript, entry, part);
                PrepareForAdjacentToolCall(message, part);
                message.Parts.Add(part);
            }
        }

        private static void PrepareForAdjacentToolCall(
            ChatMessageModel message,
            ChatMessagePartModel nextPart)
        {
            var isToolPresentationPart = nextPart.PartType == ChatMessagePartType.ToolCall ||
                                         nextPart.PartType == ChatMessagePartType.ToolOutput &&
                                         !string.IsNullOrWhiteSpace(nextPart.PresentationKind);
            if (!isToolPresentationPart ||
                message.Parts.Count == 0)
            {
                return;
            }

            var previousPart = message.Parts[^1];
            if (!CanTrimTrailingPresentationWhitespace(previousPart))
            {
                return;
            }

            previousPart.Content = previousPart.Content.TrimEnd();
            if (IsEmptyPresentationPart(previousPart))
            {
                message.Parts.RemoveAt(message.Parts.Count - 1);
            }
        }

        private static bool CanTrimTrailingPresentationWhitespace(ChatMessagePartModel part)
        {
            return part.PartType is ChatMessagePartType.Text
                or ChatMessagePartType.StructuredXml
                or ChatMessagePartType.Reasoning;
        }

        private static bool IsEmptyPresentationPart(ChatMessagePartModel part)
        {
            return CanTrimTrailingPresentationWhitespace(part) &&
                string.IsNullOrWhiteSpace(part.Content) &&
                string.IsNullOrWhiteSpace(part.Title) &&
                string.IsNullOrWhiteSpace(part.ResourcePath);
        }

        private static bool ShouldProjectBlock(ChatSessionTranscriptBlock block)
        {
            if (block.Kind is ChatSessionTranscriptBlockKind.Image
                or ChatSessionTranscriptBlockKind.Audio
                or ChatSessionTranscriptBlockKind.File
                or ChatSessionTranscriptBlockKind.ResourceReference)
            {
                return !string.IsNullOrWhiteSpace(block.ResourcePath) ||
                       !string.IsNullOrWhiteSpace(block.Content);
            }

            return !string.IsNullOrWhiteSpace(block.Content);
        }

        private static bool ShouldGroupAsAssistantBubble(ChatSessionTranscriptEntry entry)
        {
            return entry.Role == ChatSessionParticipantRole.Assistant &&
                   entry.Kind is ChatSessionTranscriptEntryKind.Reasoning
                       or ChatSessionTranscriptEntryKind.AgentMessage
                       or ChatSessionTranscriptEntryKind.AgentFinalOutput
                       or ChatSessionTranscriptEntryKind.ToolCall
                       or ChatSessionTranscriptEntryKind.MalformedToolCall;
        }

        private static string BuildAssistantGroupKey(ChatSessionTranscriptEntry entry)
        {
            return string.Join(
                ":",
                "assistant",
                entry.TurnId,
                entry.NodeId ?? string.Empty,
                entry.AgentId ?? string.Empty);
        }

        private static bool ShouldProjectEntry(
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry entry,
            bool includeDebugEntries)
        {
            if (IsMergedToolOutputEntry(transcript, entry))
            {
                return false;
            }

            if (IsSyntheticRuntimeUserEntry(entry) || IsRedundantFinalOutput(transcript, entry))
            {
                return false;
            }

            return entry.Visibility switch
            {
                TranscriptVisibility.Visible or TranscriptVisibility.Collapsed => true,
                TranscriptVisibility.DebugOnly => includeDebugEntries,
                _ => false
            };
        }

        private static ChatMessagePartModel ToPresentationPart(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block)
        {
            var partType = ToPartType(block.Kind, entry.Kind);
            var isCollapsible = IsCollapsible(entry, block);
            var part = new ChatMessagePartModel(
                partType,
                block.Content,
                ResolvePartTitle(entry, block),
                block.Language,
                GetBadgeText(block.Kind, entry.Kind),
                entry.Status == ChatSessionEntryStatus.Streaming,
                entry.ToolCallId,
                entry.AgentId,
                block.Kind is ChatSessionTranscriptBlockKind.Image or ChatSessionTranscriptBlockKind.Audio
                    ? block.ResourcePath ?? block.Content
                    : block.ResourcePath,
                IsUserVisible(entry),
                isCollapsible,
                ResolveInitialExpandedState(partType, isCollapsible));
            part.PresentationKind = ResolvePresentationKind(entry, block);
            return part;
        }

        private static void MergeToolOutputPresentation(
            ChatMessageModel message,
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry entry,
            ChatMessagePartModel part)
        {
            if (entry.Kind != ChatSessionTranscriptEntryKind.ToolCall ||
                part.PartType != ChatMessagePartType.ToolCall ||
                !TryFindMergedToolOutputEntry(transcript, entry, out var toolOutputEntry) ||
                toolOutputEntry.Blocks.FirstOrDefault(ShouldProjectBlock) is not { } toolOutputBlock)
            {
                part.ToolResultContent = string.Empty;
                part.ToolResultPresentationKind = null;
                return;
            }

            if (!message.SourceEntryIds.Contains(toolOutputEntry.EntryId, StringComparer.OrdinalIgnoreCase))
            {
                message.SourceEntryIds.Add(toolOutputEntry.EntryId);
            }

            part.ToolResultContent = SkyweaverLineDiffPresentation.ExtractPrimaryMessageOrRawContent(toolOutputBlock.Content);
            part.ToolResultPresentationKind = ResolvePresentationKind(toolOutputEntry, toolOutputBlock);
        }

        private static string? ResolvePartTitle(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block)
        {
            if (entry.Kind == ChatSessionTranscriptEntryKind.Reasoning)
            {
                return string.IsNullOrWhiteSpace(block.Title) ? "思考" : block.Title;
            }

            return block.Title;
        }

        private static ChatMessagePartType ToPartType(
            ChatSessionTranscriptBlockKind blockKind,
            ChatSessionTranscriptEntryKind entryKind)
        {
            if (entryKind == ChatSessionTranscriptEntryKind.ToolCall)
            {
                return ChatMessagePartType.ToolCall;
            }

            if (entryKind is ChatSessionTranscriptEntryKind.ToolOutput or ChatSessionTranscriptEntryKind.MalformedToolCall)
            {
                return ChatMessagePartType.ToolOutput;
            }

            return blockKind switch
            {
                ChatSessionTranscriptBlockKind.Code => ChatMessagePartType.Code,
                ChatSessionTranscriptBlockKind.StructuredXml => ChatMessagePartType.StructuredXml,
                ChatSessionTranscriptBlockKind.ToolInvocationXml => ChatMessagePartType.ToolCall,
                ChatSessionTranscriptBlockKind.ToolOutputXml => ChatMessagePartType.ToolOutput,
                ChatSessionTranscriptBlockKind.Image => ChatMessagePartType.Image,
                ChatSessionTranscriptBlockKind.Audio => ChatMessagePartType.Audio,
                ChatSessionTranscriptBlockKind.ReasoningText => ChatMessagePartType.Reasoning,
                ChatSessionTranscriptBlockKind.StatusText or ChatSessionTranscriptBlockKind.ErrorText => ChatMessagePartType.Status,
                ChatSessionTranscriptBlockKind.ResourceReference => ChatMessagePartType.HostPreservedContent,
                _ => ChatMessagePartType.Text
            };
        }

        private static string? GetBadgeText(
            ChatSessionTranscriptBlockKind blockKind,
            ChatSessionTranscriptEntryKind entryKind)
        {
            return entryKind switch
            {
                ChatSessionTranscriptEntryKind.ToolCall => "工具调用",
                ChatSessionTranscriptEntryKind.ToolOutput => "工具输出",
                ChatSessionTranscriptEntryKind.MalformedToolCall => "工具错误",
                ChatSessionTranscriptEntryKind.Reasoning => "思考",
                ChatSessionTranscriptEntryKind.StructuredPayload => "XML",
                ChatSessionTranscriptEntryKind.Error => "错误",
                _ => blockKind switch
                {
                    ChatSessionTranscriptBlockKind.StructuredXml => "XML",
                    ChatSessionTranscriptBlockKind.Image => "图片",
                    ChatSessionTranscriptBlockKind.Audio => "音频",
                    ChatSessionTranscriptBlockKind.Code => "代码",
                    _ => null
                }
            };
        }

        private static bool IsUserVisible(ChatSessionTranscriptEntry entry)
        {
            return entry.Visibility is TranscriptVisibility.Visible or TranscriptVisibility.Collapsed;
        }

        private static bool IsCollapsible(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block)
        {
            if (entry.Kind != ChatSessionTranscriptEntryKind.Reasoning)
            {
                return true;
            }

            if (TryReadBooleanMetadata(block.Metadata, "ReasoningCollapsible", out var blockValue))
            {
                return blockValue;
            }

            if (TryReadBooleanMetadata(entry.Metadata, "ReasoningCollapsible", out var entryValue))
            {
                return entryValue;
            }

            return entry.Visibility == TranscriptVisibility.Collapsed;
        }

        private static bool ResolveInitialExpandedState(
            ChatMessagePartType partType,
            bool isCollapsible)
        {
            return partType == ChatMessagePartType.Reasoning &&
                   isCollapsible &&
                   !PresentationUIRuntime.Instance.CollapseReasoningByDefault;
        }

        private static bool TryReadBooleanMetadata(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out bool value)
        {
            value = false;
            return metadata.TryGetValue(key, out var rawValue) &&
                   bool.TryParse(rawValue, out value);
        }

        private static ChatMessageRole ToPresentationRole(ChatSessionTranscriptEntry entry)
        {
            return entry.Role switch
            {
                ChatSessionParticipantRole.Assistant => ChatMessageRole.Assistant,
                ChatSessionParticipantRole.System or ChatSessionParticipantRole.Runtime or ChatSessionParticipantRole.Tool => ChatMessageRole.System,
                _ => ChatMessageRole.User
            };
        }

        private static bool IsMergedToolOutputEntry(
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry entry)
        {
            return entry.Kind == ChatSessionTranscriptEntryKind.ToolOutput &&
                   TryFindOwningToolCallEntry(transcript, entry, out _);
        }

        private static bool TryFindMergedToolOutputEntry(
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry toolCallEntry,
            out ChatSessionTranscriptEntry toolOutputEntry)
        {
            toolOutputEntry = null!;
            if (string.IsNullOrWhiteSpace(toolCallEntry.ToolCallId))
            {
                return false;
            }

            toolOutputEntry = transcript.Entries.LastOrDefault(candidate =>
                candidate.Kind == ChatSessionTranscriptEntryKind.ToolOutput &&
                string.Equals(candidate.TurnId, toolCallEntry.TurnId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ToolCallId, toolCallEntry.ToolCallId, StringComparison.OrdinalIgnoreCase))!;
            return toolOutputEntry != null;
        }

        private static bool TryFindOwningToolCallEntry(
            ChatSessionTranscript transcript,
            ChatSessionTranscriptEntry toolOutputEntry,
            out ChatSessionTranscriptEntry toolCallEntry)
        {
            toolCallEntry = null!;
            if (string.IsNullOrWhiteSpace(toolOutputEntry.ToolCallId))
            {
                return false;
            }

            toolCallEntry = transcript.Entries.LastOrDefault(candidate =>
                candidate.Kind == ChatSessionTranscriptEntryKind.ToolCall &&
                string.Equals(candidate.TurnId, toolOutputEntry.TurnId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ToolCallId, toolOutputEntry.ToolCallId, StringComparison.OrdinalIgnoreCase))!;
            return toolCallEntry != null;
        }

        private static string ResolveDisplayName(ChatSessionTranscriptEntry entry, ChatMessageRole role)
        {
            if (!string.IsNullOrWhiteSpace(entry.NodeTitle))
            {
                return entry.NodeTitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.AgentName))
            {
                return entry.AgentName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.ToolName))
            {
                return entry.ToolName.Trim();
            }

            return role switch
            {
                ChatMessageRole.Assistant => "Skyweaver 助手",
                ChatMessageRole.System => "系统",
                _ => "用户"
            };
        }

        private static string GetAvatarPath(ChatMessageRole role)
        {
            return role switch
            {
                ChatMessageRole.Assistant => AssistantAvatarPath,
                ChatMessageRole.System => SystemAvatarPath,
                _ => UserAvatarPath
            };
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
                    candidate.Visibility is TranscriptVisibility.Visible or TranscriptVisibility.Collapsed &&
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

        private static string? ResolvePresentationKind(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block)
        {
            return TryGetMetadataValue(block.Metadata, SkyweaverToolResultPresentationMetadataKeys.PresentationKind) ??
                   TryGetMetadataValue(entry.Metadata, SkyweaverToolResultPresentationMetadataKeys.PresentationKind);
        }

        private static string? TryGetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            return metadata.TryGetValue(key, out var rawValue) &&
                   !string.IsNullOrWhiteSpace(rawValue)
                ? rawValue.Trim()
                : null;
        }
    }
}
