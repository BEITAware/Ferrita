using System.Globalization;
using System.Text.Json;
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

            lock (transcript.SyncRoot)
            {
                return ProjectLocked(transcript.Entries, includeDebugEntries);
            }
        }

        private static IReadOnlyList<ChatMessageModel> ProjectLocked(
            IReadOnlyList<ChatSessionTranscriptEntry> entries,
            bool includeDebugEntries)
        {
            var messages = new List<ChatMessageModel>();
            var assistantGroups = new Dictionary<string, ChatMessageModel>(StringComparer.OrdinalIgnoreCase);
            var projectionState = BuildProjectionState(entries);

            foreach (var entry in entries)
            {
                if (!ShouldProjectEntry(entry, includeDebugEntries, projectionState))
                {
                    TrackVisibleAssistantMessage(entry, projectionState);
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

                    AddEntryParts(groupedMessage, entry, projectionState);
                    TrackVisibleAssistantMessage(entry, projectionState);
                    continue;
                }

                var message = CreatePresentationMessage(entry, entry.EntryId);
                AddEntryParts(message, entry, projectionState);
                if (message.Parts.Count > 0)
                {
                    messages.Add(message);
                }

                TrackVisibleAssistantMessage(entry, projectionState);
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
            ChatSessionTranscriptEntry entry,
            ProjectionState projectionState)
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
                MergeToolOutputPresentation(message, entry, part, projectionState);
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
                or ChatSessionTranscriptBlockKind.Video
                or ChatSessionTranscriptBlockKind.Document
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
            ChatSessionTranscriptEntry entry,
            bool includeDebugEntries,
            ProjectionState projectionState)
        {
            if (IsMergedToolOutputEntry(entry, projectionState))
            {
                return false;
            }

            if (IsSyntheticRuntimeUserEntry(entry) || IsRedundantFinalOutput(entry, projectionState))
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
            var partType = ToPartType(block, entry.Kind);
            var isCollapsible = IsCollapsible(entry, block);
            var part = new ChatMessagePartModel(
                partType,
                ResolvePresentationContent(block),
                ResolvePartTitle(entry, block),
                block.Language,
                ResolveBadgeText(block, entry.Kind),
                entry.Status == ChatSessionEntryStatus.Streaming,
                entry.ToolCallId,
                entry.AgentId,
                partType == ChatMessagePartType.TextAttachment
                    ? ResolvePreservedTextPath(block)
                    : block.Kind is ChatSessionTranscriptBlockKind.Image
                        or ChatSessionTranscriptBlockKind.Audio
                        or ChatSessionTranscriptBlockKind.Video
                        or ChatSessionTranscriptBlockKind.Document
                    ? block.ResourcePath ?? block.Content
                    : block.ResourcePath,
                IsUserVisible(entry),
                isCollapsible,
                ResolveInitialExpandedState(partType, isCollapsible),
                isUserMessage: entry.Role == ChatSessionParticipantRole.User);
            part.PresentationKind = ResolvePresentationKind(entry, block);
            part.ToolProgress = ResolveToolProgress(entry, block);
            return part;
        }

        private static void MergeToolOutputPresentation(
            ChatMessageModel message,
            ChatSessionTranscriptEntry entry,
            ChatMessagePartModel part,
            ProjectionState projectionState)
        {
            if (entry.Kind != ChatSessionTranscriptEntryKind.ToolCall ||
                part.PartType != ChatMessagePartType.ToolCall ||
                !TryFindMergedToolOutputEntry(entry, projectionState, out var toolOutputEntry) ||
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

        private static string ResolvePresentationContent(ChatSessionTranscriptBlock block)
        {
            if (block.Kind == ChatSessionTranscriptBlockKind.ResourceReference &&
                PreservedTextContentXml.TryParse(block.Content, out var textContent))
            {
                return textContent.Text;
            }

            return block.Content;
        }

        private static string? ResolvePartTitle(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block)
        {
            if (entry.Kind == ChatSessionTranscriptEntryKind.Reasoning)
            {
                return string.IsNullOrWhiteSpace(block.Title) ? "思考" : block.Title;
            }

            if (block.Kind == ChatSessionTranscriptBlockKind.ResourceReference &&
                PreservedTextContentXml.TryParse(block.Content, out var textContent))
            {
                return textContent.DisplayName;
            }

            return block.Title;
        }

        private static ChatMessagePartType ToPartType(
            ChatSessionTranscriptBlock block,
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

            if (block.Kind == ChatSessionTranscriptBlockKind.ResourceReference &&
                PreservedTextContentXml.IsTextContent(block.Content))
            {
                return ChatMessagePartType.TextAttachment;
            }

            return block.Kind switch
            {
                ChatSessionTranscriptBlockKind.Code => ChatMessagePartType.Code,
                ChatSessionTranscriptBlockKind.StructuredXml => ChatMessagePartType.StructuredXml,
                ChatSessionTranscriptBlockKind.ToolInvocationXml => ChatMessagePartType.ToolCall,
                ChatSessionTranscriptBlockKind.ToolOutputXml => ChatMessagePartType.ToolOutput,
                ChatSessionTranscriptBlockKind.Image => ChatMessagePartType.Image,
                ChatSessionTranscriptBlockKind.Audio => ChatMessagePartType.Audio,
                ChatSessionTranscriptBlockKind.Video => ChatMessagePartType.Video,
                ChatSessionTranscriptBlockKind.Document => ChatMessagePartType.Document,
                ChatSessionTranscriptBlockKind.ReasoningText => ChatMessagePartType.Reasoning,
                ChatSessionTranscriptBlockKind.StatusText or ChatSessionTranscriptBlockKind.ErrorText => ChatMessagePartType.Status,
                ChatSessionTranscriptBlockKind.ResourceReference => ChatMessagePartType.HostPreservedContent,
                _ => ChatMessagePartType.Text
            };
        }

        private static string? ResolvePreservedTextPath(ChatSessionTranscriptBlock block)
        {
            return PreservedTextContentXml.TryParse(block.Content, out var textContent)
                ? textContent.Path
                : block.ResourcePath;
        }

        private static string? ResolveBadgeText(
            ChatSessionTranscriptBlock block,
            ChatSessionTranscriptEntryKind entryKind)
        {
            if (block.Kind == ChatSessionTranscriptBlockKind.ResourceReference &&
                PreservedTextContentXml.IsTextContent(block.Content))
            {
                return "Text";
            }

            return GetBadgeText(block.Kind, entryKind);
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
                    ChatSessionTranscriptBlockKind.Video => "视频",
                    ChatSessionTranscriptBlockKind.Document => "文档",
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
            ChatSessionTranscriptEntry entry,
            ProjectionState projectionState)
        {
            return entry.Kind == ChatSessionTranscriptEntryKind.ToolOutput &&
                   !string.IsNullOrWhiteSpace(entry.EntryId) &&
                   projectionState.MergedToolOutputEntryIds.Contains(entry.EntryId);
        }

        private static bool TryFindMergedToolOutputEntry(
            ChatSessionTranscriptEntry toolCallEntry,
            ProjectionState projectionState,
            out ChatSessionTranscriptEntry toolOutputEntry)
        {
            var toolEntryKey = BuildToolEntryKey(toolCallEntry.TurnId, toolCallEntry.ToolCallId);
            return projectionState.MergedToolOutputsByKey.TryGetValue(toolEntryKey, out toolOutputEntry!);
        }

        private static void TrackVisibleAssistantMessage(
            ChatSessionTranscriptEntry entry,
            ProjectionState projectionState)
        {
            if (entry.Kind != ChatSessionTranscriptEntryKind.AgentMessage ||
                entry.Role != ChatSessionParticipantRole.Assistant ||
                entry.Visibility is not (TranscriptVisibility.Visible or TranscriptVisibility.Collapsed))
            {
                return;
            }

            var messageKey = BuildVisibleAssistantMessageKey(entry);
            if (messageKey.Length > 0)
            {
                projectionState.VisibleAssistantMessageKeys.Add(messageKey);
            }
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
            ChatSessionTranscriptEntry entry,
            ProjectionState projectionState)
        {
            if (entry.Kind != ChatSessionTranscriptEntryKind.AgentFinalOutput)
            {
                return false;
            }

            var messageKey = BuildVisibleAssistantMessageKey(entry);
            return messageKey.Length > 0 &&
                   projectionState.VisibleAssistantMessageKeys.Contains(messageKey);
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

        private static SkyweaverToolProgressUpdate? ResolveToolProgress(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block)
        {
            var phase = TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.Phase);
            var statusText = TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.StatusText);
            var activeItemsJson = TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.ActiveItems);
            var activeItems = ParseActiveItems(activeItemsJson);
            if (string.IsNullOrWhiteSpace(phase) &&
                string.IsNullOrWhiteSpace(statusText) &&
                activeItems.Count == 0 &&
                !TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.ProgressFraction, out _) &&
                !TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.CompletedItems, out _) &&
                !TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.TotalItems, out _))
            {
                return null;
            }

            return new SkyweaverToolProgressUpdate
            {
                Phase = phase ?? string.Empty,
                StatusText = statusText ?? string.Empty,
                CompletedItems = TryParseInt(TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.CompletedItems)),
                TotalItems = TryParseInt(TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.TotalItems)),
                ProgressFraction = TryParseDouble(TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.ProgressFraction)),
                IsCompleted = TryParseBoolean(TryGetProgressMetadataValue(entry, block, SkyweaverToolProgressMetadataKeys.IsCompleted)),
                ActiveItems = activeItems
            }.Normalize();
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

        private static string? TryGetProgressMetadataValue(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block,
            string key)
        {
            return TryGetMetadataValue(block.Metadata, key) ??
                   TryGetMetadataValue(entry.Metadata, key);
        }

        private static bool TryGetProgressMetadataValue(
            ChatSessionTranscriptEntry entry,
            ChatSessionTranscriptBlock block,
            string key,
            out string? value)
        {
            value = TryGetProgressMetadataValue(entry, block, key);
            return value != null;
        }

        private static int? TryParseInt(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static double? TryParseDouble(string? value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static bool TryParseBoolean(string? value)
        {
            return bool.TryParse(value, out var parsed) && parsed;
        }

        private static IReadOnlyList<string> ParseActiveItems(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return value
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }
        }

        private static ProjectionState BuildProjectionState(IEnumerable<ChatSessionTranscriptEntry> entries)
        {
            var toolCallKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mergedToolOutputsByKey = new Dictionary<string, ChatSessionTranscriptEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var toolEntryKey = BuildToolEntryKey(entry.TurnId, entry.ToolCallId);
                if (toolEntryKey.Length == 0)
                {
                    continue;
                }

                if (entry.Kind == ChatSessionTranscriptEntryKind.ToolCall)
                {
                    toolCallKeys.Add(toolEntryKey);
                }
                else if (entry.Kind == ChatSessionTranscriptEntryKind.ToolOutput)
                {
                    mergedToolOutputsByKey[toolEntryKey] = entry;
                }
            }

            var mergedToolOutputEntryIds = mergedToolOutputsByKey
                .Where(pair => toolCallKeys.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.EntryId))
                .Select(pair => pair.Value.EntryId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new ProjectionState(mergedToolOutputsByKey, mergedToolOutputEntryIds);
        }

        private static string BuildToolEntryKey(string? turnId, string? toolCallId)
        {
            var normalizedToolCallId = toolCallId?.Trim() ?? string.Empty;
            if (normalizedToolCallId.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ":",
                turnId?.Trim() ?? string.Empty,
                normalizedToolCallId);
        }

        private static string BuildVisibleAssistantMessageKey(ChatSessionTranscriptEntry entry)
        {
            var comparableText = GetComparableEntryText(entry);
            if (comparableText.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ":",
                entry.TurnId,
                entry.NodeId ?? string.Empty,
                comparableText);
        }

        private sealed class ProjectionState
        {
            public ProjectionState(
                IReadOnlyDictionary<string, ChatSessionTranscriptEntry> mergedToolOutputsByKey,
                IReadOnlySet<string> mergedToolOutputEntryIds)
            {
                MergedToolOutputsByKey = mergedToolOutputsByKey;
                MergedToolOutputEntryIds = mergedToolOutputEntryIds;
            }

            public IReadOnlyDictionary<string, ChatSessionTranscriptEntry> MergedToolOutputsByKey { get; }

            public IReadOnlySet<string> MergedToolOutputEntryIds { get; }

            public HashSet<string> VisibleAssistantMessageKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
