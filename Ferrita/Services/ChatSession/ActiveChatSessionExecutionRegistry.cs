using Ferrita.Models.ChatSession;

namespace Ferrita.Services.ChatSession
{
    public enum ActiveChatSessionExecutionKind
    {
        Foreground,
        Shell,
        Background
    }

    public sealed class ActiveChatSessionExecutionSnapshot
    {
        public string SessionId { get; init; } = string.Empty;

        public string SessionTitle { get; init; } = string.Empty;

        public string FlowName { get; init; } = string.Empty;

        public ActiveChatSessionExecutionKind Kind { get; init; }

        public string StatusText { get; init; } = string.Empty;

        public string? CurrentNodeTitle { get; init; }

        public string? CurrentAgentId { get; init; }

        public string? ModelId { get; init; }

        public string? UserTextPreview { get; init; }

        public string LatestOutput { get; init; } = string.Empty;

        public DateTime StartedAtUtc { get; init; }

        public DateTime UpdatedAtUtc { get; init; }
    }

    public sealed class ActiveChatSessionExecutionRegistry
    {
        private sealed class ActiveExecutionRecord
        {
            public string SessionId { get; init; } = string.Empty;

            public string SessionTitle { get; set; } = string.Empty;

            public string FlowName { get; set; } = string.Empty;

            public ActiveChatSessionExecutionKind Kind { get; init; }

            public string StatusText { get; set; } = string.Empty;

            public string? CurrentNodeTitle { get; set; }

            public string? CurrentAgentId { get; set; }

            public string? ModelId { get; set; }

            public string? UserTextPreview { get; init; }

            public string LatestOutput { get; set; } = string.Empty;

            public DateTime StartedAtUtc { get; init; }

            public DateTime UpdatedAtUtc { get; set; }

            public CancellationTokenSource CancellationSource { get; init; } = null!;

            /// <summary>
            /// 运行中的会话对象内存实例
            /// </summary>
            public ChatSessionModel? Session { get; set; }

            public ActiveChatSessionExecutionSnapshot ToSnapshot()
            {
                return new ActiveChatSessionExecutionSnapshot
                {
                    SessionId = SessionId,
                    SessionTitle = SessionTitle,
                    FlowName = FlowName,
                    Kind = Kind,
                    StatusText = StatusText,
                    CurrentNodeTitle = CurrentNodeTitle,
                    CurrentAgentId = CurrentAgentId,
                    ModelId = ModelId,
                    UserTextPreview = UserTextPreview,
                    LatestOutput = LatestOutput,
                    StartedAtUtc = StartedAtUtc,
                    UpdatedAtUtc = UpdatedAtUtc
                };
            }
        }

        public static ActiveChatSessionExecutionRegistry Instance { get; } = new();

        private readonly object _gate = new();
        private readonly Dictionary<string, ActiveExecutionRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        private ActiveChatSessionExecutionRegistry()
        {
        }

        public event EventHandler? Changed;

        public event EventHandler<ChatSessionRuntimeEvent>? RuntimeEventApplied;

        public IReadOnlyList<ActiveChatSessionExecutionSnapshot> GetSnapshot()
        {
            lock (_gate)
            {
                return _records.Values
                    .OrderByDescending(record => record.StartedAtUtc)
                    .Select(record => record.ToSnapshot())
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取当前处于运行状态的活跃会话对象内存引用
        /// </summary>
        public ChatSessionModel? GetActiveSession(string? sessionId)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            if (normalizedSessionId.Length == 0)
            {
                return null;
            }

            lock (_gate)
            {
                return _records.TryGetValue(normalizedSessionId, out var record) ? record.Session : null;
            }
        }

        public void Register(
            ChatSessionModel session,
            CancellationTokenSource cancellationSource,
            string? userText)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(cancellationSource);

            var sessionId = session.SessionId?.Trim() ?? string.Empty;
            if (sessionId.Length == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            lock (_gate)
            {
                _records[sessionId] = new ActiveExecutionRecord
                {
                    SessionId = sessionId,
                    Session = session,
                    SessionTitle = string.IsNullOrWhiteSpace(session.Name) ? sessionId : session.Name.Trim(),
                    FlowName = session.BoundFlowDisplayName,
                    Kind = ResolveKind(session),
                    StatusText = "正在准备运行",
                    UserTextPreview = CreatePreview(userText),
                    StartedAtUtc = now,
                    UpdatedAtUtc = now,
                    CancellationSource = cancellationSource
                };
            }

            RaiseChanged();
        }

        public void ApplyRuntimeEvent(ChatSessionRuntimeEvent runtimeEvent)
        {
            ArgumentNullException.ThrowIfNull(runtimeEvent);

            var sessionId = runtimeEvent.SessionId?.Trim() ?? string.Empty;
            if (sessionId.Length == 0)
            {
                return;
            }

            var changed = false;
            lock (_gate)
            {
                if (!_records.TryGetValue(sessionId, out var record))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(runtimeEvent.SessionTitle))
                {
                    record.SessionTitle = runtimeEvent.SessionTitle.Trim();
                }

                if (!string.IsNullOrWhiteSpace(runtimeEvent.FlowName))
                {
                    record.FlowName = runtimeEvent.FlowName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(runtimeEvent.NodeTitle))
                {
                    record.CurrentNodeTitle = runtimeEvent.NodeTitle.Trim();
                }

                if (!string.IsNullOrWhiteSpace(runtimeEvent.AgentId))
                {
                    record.CurrentAgentId = runtimeEvent.AgentId.Trim();
                }

                if (!string.IsNullOrWhiteSpace(runtimeEvent.ModelId))
                {
                    record.ModelId = runtimeEvent.ModelId.Trim();
                }

                record.StatusText = ResolveStatusText(runtimeEvent, record.StatusText);
                if (runtimeEvent.Kind == ChatSessionRuntimeEventKind.TextDelta && !string.IsNullOrEmpty(runtimeEvent.TextDelta))
                {
                    record.LatestOutput += runtimeEvent.TextDelta;
                }
                record.UpdatedAtUtc = DateTime.UtcNow;
                changed = true;
            }

            if (changed)
            {
                RaiseChanged();
                RuntimeEventApplied?.Invoke(this, runtimeEvent);
            }
        }

        public bool Cancel(string? sessionId)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            if (normalizedSessionId.Length == 0)
            {
                return false;
            }

            CancellationTokenSource? cancellationSource;
            lock (_gate)
            {
                if (!_records.TryGetValue(normalizedSessionId, out var record))
                {
                    return false;
                }

                record.StatusText = "正在中止";
                record.UpdatedAtUtc = DateTime.UtcNow;
                cancellationSource = record.CancellationSource;
            }

            RaiseChanged();

            try
            {
                cancellationSource.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Unregister(string? sessionId)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            if (normalizedSessionId.Length == 0)
            {
                return;
            }

            var removed = false;
            lock (_gate)
            {
                removed = _records.Remove(normalizedSessionId);
            }

            if (removed)
            {
                RaiseChanged();
            }
        }

        private static ActiveChatSessionExecutionKind ResolveKind(ChatSessionModel session)
        {
            if (session.IsShellSession)
            {
                return ActiveChatSessionExecutionKind.Shell;
            }

            return session.IsScheduledTaskSession
                ? ActiveChatSessionExecutionKind.Background
                : ActiveChatSessionExecutionKind.Foreground;
        }

        private static string ResolveStatusText(ChatSessionRuntimeEvent runtimeEvent, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(runtimeEvent.ToolProgress?.StatusText))
            {
                return runtimeEvent.ToolProgress.StatusText.Trim();
            }

            if (!string.IsNullOrWhiteSpace(runtimeEvent.Message))
            {
                return runtimeEvent.Message.Trim();
            }

            return runtimeEvent.Kind switch
            {
                ChatSessionRuntimeEventKind.UserMessageCommitted => "用户消息已提交",
                ChatSessionRuntimeEventKind.ExecutionStarted => "正在运行",
                ChatSessionRuntimeEventKind.NodeStarted when !string.IsNullOrWhiteSpace(runtimeEvent.NodeTitle) =>
                    $"正在运行 {runtimeEvent.NodeTitle.Trim()}",
                ChatSessionRuntimeEventKind.NodeStarted => "正在运行节点",
                ChatSessionRuntimeEventKind.AgentIterationStarted when !string.IsNullOrWhiteSpace(runtimeEvent.AgentId) =>
                    $"代理 {runtimeEvent.AgentId.Trim()} 正在思考",
                ChatSessionRuntimeEventKind.AgentIterationStarted => "代理正在思考",
                ChatSessionRuntimeEventKind.ToolCallStarted when !string.IsNullOrWhiteSpace(runtimeEvent.ToolInvocation?.ToolName) =>
                    $"正在调用 {runtimeEvent.ToolInvocation.ToolName}",
                ChatSessionRuntimeEventKind.ToolCallStarted => "正在调用工具",
                ChatSessionRuntimeEventKind.ToolCallUpdated => "正在接收工具调用",
                ChatSessionRuntimeEventKind.ToolOutputReceived => "工具结果已返回",
                ChatSessionRuntimeEventKind.ContextCompressionApplied => "上下文已压缩",
                ChatSessionRuntimeEventKind.TextDelta => "正在生成回复",
                ChatSessionRuntimeEventKind.ReasoningDelta => "正在推理",
                ChatSessionRuntimeEventKind.MediaProcessingProgressUpdated => "正在处理多媒体内容",
                ChatSessionRuntimeEventKind.ExecutionCompleted => "已完成",
                ChatSessionRuntimeEventKind.ExecutionFailed => "已失败",
                ChatSessionRuntimeEventKind.ExecutionCancelled => "已中止",
                _ => string.IsNullOrWhiteSpace(fallback) ? "正在运行" : fallback
            };
        }

        private static string CreatePreview(string? text)
        {
            var normalized = string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : text.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');
            const int maxLength = 80;
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength] + "...";
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
