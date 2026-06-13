using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using Skyweaver.Commands;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.ChatSessionControl.Models;
using Skyweaver.Controls.ChatSessionControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.ChatSession;
using Skyweaver.Services.Localization;
using Skyweaver.Services.Memory;
using Skyweaver.Services.ShellIntegration;
using Skyweaver.Windows;

namespace Skyweaver.Controls.ShellChatSessionControl.ViewModels
{
    public sealed class ShellChatSessionControlViewModel : ObservableObject
    {
        private static readonly TimeSpan StreamingProjectionRefreshInterval = TimeSpan.FromMilliseconds(32);
        private const string ShellIdleAvatarPath = "pack://application:,,,/Resources/SkyweaverAppLogo.png";

        private readonly ShellChatStartupContext _startupContext;
        private readonly ChatSessionRepository _chatSessionRepository;
        private readonly ChatSessionRuntimeService _runtimeService;
        private readonly ChatSessionPresentationProjector _presentationProjector;
        private readonly ToolInvocationPresentationService _toolInvocationPresentationService;
        private readonly MemoryService _memoryService;
        private readonly IReadOnlyDictionary<string, string> _agentAvatarPathsById;
        private readonly string _shellContextSnapshotXml;

        private ChatSessionModel? _sessionModel;
        private CancellationTokenSource? _executionCancellationSource;
        private readonly object _streamingRuntimeEventSync = new();
        private ChatSessionRuntimeEvent? _pendingStreamingRuntimeEvent;
        private bool _isStreamingRuntimeEventDispatchQueued;
        private System.Windows.Threading.DispatcherTimer? _streamingProjectionTimer;
        private int _streamingProjectionIdleTickCount;
        private string _inputText = string.Empty;
        private string _statusText = L("ShellChat.Status.Ready", "Ready");
        private bool _isExecutionActive;
        private bool _hasInjectedShellContext;
        private string _activeAgentAvatarPath = ShellIdleAvatarPath;
        private ChatMessagePartModel? _latestToolCallPart;

        public ShellChatSessionControlViewModel()
            : this(ShellChatStartupContext.Empty)
        {
        }

        public ShellChatSessionControlViewModel(ShellChatStartupContext startupContext)
        {
            _startupContext = startupContext ?? ShellChatStartupContext.Empty;
            _chatSessionRepository = new ChatSessionRepository();
            _runtimeService = new ChatSessionRuntimeService();
            _presentationProjector = new ChatSessionPresentationProjector();
            _toolInvocationPresentationService = new ToolInvocationPresentationService();
            _memoryService = new MemoryService();
            _agentAvatarPathsById = LoadAgentAvatarPathsById();
            _shellContextSnapshotXml = _startupContext.HasContext
                ? BuildShellContextText(_startupContext)
                : string.Empty;

            SendCommand = new AsyncRelayCommand(SendMessageAsync, () => CanSendMessage);
            CancelCommand = new RelayCommand(CancelExecution, () => CanCancelExecution);
            CloseCommand = new RelayCommand(ExecuteClose);

            try
            {
                _sessionModel = CreateShellSession();
                StatusText = BuildInitialStatusText(_sessionModel);
                RefreshMessagesFromTranscript();
            }
            catch (Exception ex)
            {
                StatusText = L("ShellChat.Status.Unavailable", "Shell Chat unavailable");
                AddSystemStatusMessage(ex.Message, L("ShellChat.Status.SessionCreateFailed", "Session creation failed"));
            }
        }

        public ObservableCollection<ChatMessageModel> Messages { get; } = new();

        public string InputText
        {
            get => _inputText;
            set
            {
                if (SetProperty(ref _inputText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(CanSendMessage));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value ?? string.Empty);
        }

        public string ActiveAgentAvatarPath
        {
            get => _activeAgentAvatarPath;
            private set => SetProperty(ref _activeAgentAvatarPath, string.IsNullOrWhiteSpace(value) ? ShellIdleAvatarPath : value.Trim());
        }

        public bool IsExecutionActive
        {
            get => _isExecutionActive;
            private set
            {
                if (SetProperty(ref _isExecutionActive, value))
                {
                    OnPropertyChanged(nameof(CanSendMessage));
                    OnPropertyChanged(nameof(CanCancelExecution));
                    OnPropertyChanged(nameof(ComposerPrimaryButtonText));
                    OnPropertyChanged(nameof(ComposerPrimaryCommand));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanSendMessage => !IsExecutionActive && !string.IsNullOrWhiteSpace(InputText);

        public bool CanCancelExecution => IsExecutionActive;

        public string ComposerPrimaryButtonText => IsExecutionActive
            ? L("ShellChat.Button.Stop", "Stop")
            : L("ShellChat.Button.Send", "Send");

        public ICommand ComposerPrimaryCommand => IsExecutionActive ? CancelCommand : SendCommand;

        public ChatMessagePartModel? LatestToolCallPart
        {
            get => _latestToolCallPart;
            private set
            {
                if (SetProperty(ref _latestToolCallPart, value))
                {
                    OnPropertyChanged(nameof(HasLatestToolCall));
                }
            }
        }

        public bool HasLatestToolCall => LatestToolCallPart?.ToolPresentationView != null;

        public ICommand SendCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand CloseCommand { get; }

        public event Action? RequestClose;

        private async Task SendMessageAsync()
        {
            var trimmedText = InputText.Trim();
            if (trimmedText.Length == 0)
            {
                return;
            }

            if (_sessionModel == null)
            {
                AddSystemStatusMessage(
                    L("ShellChat.Error.NoSession", "No shell ChatSession is available."),
                    L("ShellChat.Status.RuntimeUnavailable", "Runtime unavailable"));
                return;
            }

            InputText = string.Empty;

            _executionCancellationSource?.Dispose();
            _executionCancellationSource = new CancellationTokenSource();
            IsExecutionActive = true;
            ActiveAgentAvatarPath = ShellIdleAvatarPath;
            StatusText = L("ShellChat.Status.Running", "Running");

            var userContentBlocks = Array.Empty<LanguageModelChatContentBlock>();
            var hostInjectedMessages = BuildOneShotShellContextMessages();
            _hasInjectedShellContext = true;

            ChatSessionRuntimeResult? runtimeResult = null;
            try
            {
                var sessionModel = _sessionModel;
                runtimeResult = await _runtimeService.ExecuteTurnAsync(
                    new ChatSessionRuntimeRequest
                    {
                        Session = sessionModel,
                        UserText = trimmedText,
                        UserContentBlocks = userContentBlocks,
                        HostInjectedHistoryMessages = hostInjectedMessages,
                        HostInjectedHistoryMessageFactory = ct => _memoryService.RetrieveBackfillMessagesAsync(
                            sessionModel,
                            trimmedText,
                            userContentBlocks,
                            ct),
                        ToolConfirmationCallback = ConfirmToolInvocationAsync
                    },
                    HandleRuntimeEventAsync,
                    _executionCancellationSource.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                runtimeResult = new ChatSessionRuntimeResult
                {
                    FailureReason = ex.Message
                };
                AddSystemStatusMessage(ex.Message, L("ShellChat.Status.ExecutionFailed", "Execution failed"));
            }
            finally
            {
                _executionCancellationSource?.Dispose();
                _executionCancellationSource = null;
                IsExecutionActive = false;

                StatusText = runtimeResult switch
                {
                    { IsCompleted: true } => L("ShellChat.Status.Ready", "Ready"),
                    { IsCancelled: true } => L("ShellChat.Status.Cancelled", "Cancelled"),
                    _ => L("ShellChat.Status.Failed", "Failed")
                };
                ActiveAgentAvatarPath = ShellIdleAvatarPath;

                RefreshMessagesFromTranscript();
            }
        }

        private IReadOnlyList<LanguageModelChatMessage> BuildOneShotShellContextMessages()
        {
            if (_hasInjectedShellContext || string.IsNullOrWhiteSpace(_shellContextSnapshotXml))
            {
                return Array.Empty<LanguageModelChatMessage>();
            }

            return
            [
                new LanguageModelChatMessage(
                    LanguageModelChatRole.System,
                    [LanguageModelChatContentBlock.CreateHostPreservedContent(_shellContextSnapshotXml)])
                {
                    AuthorName = "Skyweaver Shell",
                    IsHostInjectedTail = true
                }
            ];
        }

        private ValueTask HandleRuntimeEventAsync(
            ChatSessionRuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(runtimeEvent);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                ApplyRuntimeEvent(runtimeEvent);
                return ValueTask.CompletedTask;
            }

            if (IsHighFrequencyStreamingProjectionEvent(runtimeEvent))
            {
                QueueStreamingRuntimeEvent(runtimeEvent, dispatcher);
                return ValueTask.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                FlushPendingStreamingRuntimeEvent();
                ApplyRuntimeEvent(runtimeEvent);
                ClearPendingStreamingRuntimeEvent();
                return ValueTask.CompletedTask;
            }

            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    FlushPendingStreamingRuntimeEvent();
                    ApplyRuntimeEvent(runtimeEvent);
                    ClearPendingStreamingRuntimeEvent();
                }),
                System.Windows.Threading.DispatcherPriority.Normal);
            return ValueTask.CompletedTask;
        }

        private void QueueStreamingRuntimeEvent(
            ChatSessionRuntimeEvent runtimeEvent,
            System.Windows.Threading.Dispatcher dispatcher)
        {
            lock (_streamingRuntimeEventSync)
            {
                _pendingStreamingRuntimeEvent = runtimeEvent;
                _streamingProjectionIdleTickCount = 0;
                if (_isStreamingRuntimeEventDispatchQueued)
                {
                    return;
                }

                _isStreamingRuntimeEventDispatchQueued = true;
            }

            dispatcher.BeginInvoke(
                new Action(StartStreamingProjectionPump),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        private void StartStreamingProjectionPump()
        {
            _streamingProjectionTimer ??= CreateStreamingProjectionTimer();
            if (!_streamingProjectionTimer.IsEnabled)
            {
                _streamingProjectionTimer.Start();
            }

            FlushPendingStreamingRuntimeEvent();
        }

        private System.Windows.Threading.DispatcherTimer CreateStreamingProjectionTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = StreamingProjectionRefreshInterval
            };
            timer.Tick += (_, _) => FlushPendingStreamingRuntimeEvent();
            return timer;
        }

        private void FlushPendingStreamingRuntimeEvent()
        {
            ChatSessionRuntimeEvent? runtimeEvent;
            var shouldStopPump = false;
            lock (_streamingRuntimeEventSync)
            {
                runtimeEvent = _pendingStreamingRuntimeEvent;
                _pendingStreamingRuntimeEvent = null;
                if (runtimeEvent == null)
                {
                    _streamingProjectionIdleTickCount++;
                    if (_streamingProjectionIdleTickCount >= 2)
                    {
                        _isStreamingRuntimeEventDispatchQueued = false;
                        _streamingProjectionIdleTickCount = 0;
                        shouldStopPump = true;
                    }
                }
                else
                {
                    _streamingProjectionIdleTickCount = 0;
                }
            }

            if (runtimeEvent != null)
            {
                ApplyRuntimeEvent(runtimeEvent);
            }

            if (shouldStopPump)
            {
                _streamingProjectionTimer?.Stop();
            }
        }

        private void ClearPendingStreamingRuntimeEvent()
        {
            lock (_streamingRuntimeEventSync)
            {
                _pendingStreamingRuntimeEvent = null;
                _isStreamingRuntimeEventDispatchQueued = false;
                _streamingProjectionIdleTickCount = 0;
            }

            _streamingProjectionTimer?.Stop();
        }

        private void ApplyRuntimeEvent(ChatSessionRuntimeEvent runtimeEvent)
        {
            UpdateActiveAgentAvatar(runtimeEvent);

            StatusText = runtimeEvent.Kind switch
            {
                ChatSessionRuntimeEventKind.ExecutionStarted => L("ShellChat.Status.Running", "Running"),
                ChatSessionRuntimeEventKind.NodeStarted when !string.IsNullOrWhiteSpace(runtimeEvent.NodeTitle) =>
                    LF("ShellChat.Status.NodeStartedFormat", "Running {0}", runtimeEvent.NodeTitle),
                ChatSessionRuntimeEventKind.ToolProgressUpdated when !string.IsNullOrWhiteSpace(runtimeEvent.ToolProgress?.StatusText) =>
                    runtimeEvent.ToolProgress!.StatusText,
                ChatSessionRuntimeEventKind.ExecutionCompleted => L("ShellChat.Status.Completed", "Completed"),
                ChatSessionRuntimeEventKind.ExecutionFailed => L("ShellChat.Status.Failed", "Failed"),
                ChatSessionRuntimeEventKind.ExecutionCancelled => L("ShellChat.Status.Cancelled", "Cancelled"),
                _ => StatusText
            };

            if (ShouldRefreshProjection(runtimeEvent))
            {
                RefreshMessagesFromTranscript();
            }
        }

        private void UpdateActiveAgentAvatar(ChatSessionRuntimeEvent runtimeEvent)
        {
            switch (runtimeEvent.Kind)
            {
                case ChatSessionRuntimeEventKind.NodeStarted:
                    ActiveAgentAvatarPath = runtimeEvent.NodeKind == SessionFlowNodeKind.Agent
                        ? ResolveAgentAvatarPath(runtimeEvent.AgentId)
                        : ShellIdleAvatarPath;
                    break;

                case ChatSessionRuntimeEventKind.AgentIterationStarted:
                case ChatSessionRuntimeEventKind.TextDelta:
                case ChatSessionRuntimeEventKind.ReasoningDelta:
                case ChatSessionRuntimeEventKind.ToolCallStarted:
                case ChatSessionRuntimeEventKind.ToolCallUpdated:
                case ChatSessionRuntimeEventKind.ToolProgressUpdated:
                case ChatSessionRuntimeEventKind.ToolOutputReceived:
                case ChatSessionRuntimeEventKind.AgentFinalOutputProduced:
                    ActiveAgentAvatarPath = ResolveAgentAvatarPath(runtimeEvent.AgentId);
                    break;

                case ChatSessionRuntimeEventKind.NodeCompleted
                    when runtimeEvent.NodeKind == SessionFlowNodeKind.Agent:
                case ChatSessionRuntimeEventKind.ExecutionCompleted:
                case ChatSessionRuntimeEventKind.ExecutionFailed:
                case ChatSessionRuntimeEventKind.ExecutionCancelled:
                    ActiveAgentAvatarPath = ShellIdleAvatarPath;
                    break;
            }
        }

        private string ResolveAgentAvatarPath(string? agentId)
        {
            var normalizedAgentId = agentId?.Trim() ?? string.Empty;
            if (normalizedAgentId.Length == 0)
            {
                return AgentDefinition.DefaultAvatarPath;
            }

            return _agentAvatarPathsById.TryGetValue(normalizedAgentId, out var avatarPath) &&
                   !string.IsNullOrWhiteSpace(avatarPath)
                ? avatarPath
                : AgentDefinition.DefaultAvatarPath;
        }

        private static IReadOnlyDictionary<string, string> LoadAgentAvatarPathsById()
        {
            try
            {
                return new AgentConfigurationRepository(new AgentConfigurationPathProvider())
                    .Load()
                    .Where(agent => !string.IsNullOrWhiteSpace(agent.AgentId))
                    .GroupBy(agent => agent.AgentId.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => string.IsNullOrWhiteSpace(group.Last().AvatarPath)
                            ? AgentDefinition.DefaultAvatarPath
                            : group.Last().AvatarPath.Trim(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool ShouldRefreshProjection(ChatSessionRuntimeEvent runtimeEvent)
        {
            return true;
        }

        private static bool IsHighFrequencyStreamingProjectionEvent(ChatSessionRuntimeEvent runtimeEvent)
        {
            return (runtimeEvent.Kind is ChatSessionRuntimeEventKind.TextDelta
                    or ChatSessionRuntimeEventKind.ReasoningDelta
                    or ChatSessionRuntimeEventKind.MediaProcessingProgressUpdated
                    or ChatSessionRuntimeEventKind.ToolProgressUpdated) ||
                runtimeEvent.Kind == ChatSessionRuntimeEventKind.ToolCallUpdated &&
                runtimeEvent.ToolCallSnapshot?.IsInvocationClosed != true &&
                runtimeEvent.ToolInvocation == null;
        }

        private Task<AgentToolConfirmationResult> ConfirmToolInvocationAsync(
            AgentToolConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowToolConfirmationDialog(request));
            }

            return dispatcher.InvokeAsync(() => ShowToolConfirmationDialog(request)).Task;
        }

        private AgentToolConfirmationResult ShowToolConfirmationDialog(AgentToolConfirmationRequest request)
        {
            var toolCallIndex = request.ToolCallIndex > 0 ? request.ToolCallIndex : 1;
            var previewHandle = _toolInvocationPresentationService.CreateConfirmationPresentation(
                request.Invocation,
                toolCallIndex);

            var dialog = new ToolConfirmationDialog(new ToolConfirmationDialogModel
            {
                ToolName = request.Invocation.ToolName,
                PromptText = LF(
                    "ShellChat.ToolConfirmation.PromptFormat",
                    "Agent {0} wants to run this tool call.",
                    request.Agent.DisplayNameOrFallback),
                MetadataText = LF(
                    "ShellChat.ToolConfirmation.MetadataFormat",
                    "Tool: {0}    Iteration: {1}    Call: {2}",
                    request.Invocation.ToolName,
                    request.IterationNumber,
                    toolCallIndex),
                InvocationXml = request.Invocation.InvocationXml,
                InvocationPreview = previewHandle.View
            })
            {
                Owner = Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true
                ? AgentToolConfirmationResult.Approve()
                : AgentToolConfirmationResult.Reject(L("ShellChat.ToolConfirmation.Rejected", "The tool call was rejected by the user."));
        }

        private void CancelExecution()
        {
            if (!IsExecutionActive)
            {
                return;
            }

            _runtimeService.CancelActiveExecution(_sessionModel?.SessionId);
            _executionCancellationSource?.Cancel();
            StatusText = L("ShellChat.Status.Cancelling", "Cancelling");
        }

        private void ExecuteClose()
        {
            if (IsExecutionActive)
            {
                CancelExecution();
            }

            RequestClose?.Invoke();
        }

        private ChatSessionModel CreateShellSession()
        {
            var configuration = ShellIntegrationRuntime.Instance.GetConfiguration();
            var preferredBinding = new ChatSessionFlowBinding
            {
                GraphId = configuration.SessionFlowGraphId,
                GraphName = configuration.SessionFlowGraphName,
                FilePath = configuration.SessionFlowFilePath
            };

            var session = _chatSessionRepository.Create(
                CreateUniqueShellSessionName(),
                preferredBinding.IsBound ? preferredBinding : null);
            session.IsShellSession = true;
            PersistShellContextSnapshot(session);
            _chatSessionRepository.Save(session);
            return session;
        }

        private void PersistShellContextSnapshot(ChatSessionModel session)
        {
            if (string.IsNullOrWhiteSpace(_shellContextSnapshotXml))
            {
                return;
            }

            lock (session.Transcript.SyncRoot)
            {
                if (session.Transcript.Entries.Any(entry =>
                        entry.Metadata.TryGetValue("ShellContextSnapshot", out var value) &&
                        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                var entry = new ChatSessionTranscriptEntry
                {
                    EntryId = Guid.NewGuid().ToString("N"),
                    Kind = ChatSessionTranscriptEntryKind.StructuredPayload,
                    Role = ChatSessionParticipantRole.Runtime,
                    TimestampUtc = DateTime.UtcNow,
                    Visibility = TranscriptVisibility.InternalOnly,
                    LlmPolicy = TranscriptLlmPolicy.Exclude,
                    HandoffPolicy = TranscriptHandoffPolicy.ExcludeByDefault,
                    Status = ChatSessionEntryStatus.Completed
                };
                entry.Metadata["ShellContextSnapshot"] = "true";
                entry.Metadata["ShellContextInjection"] = "FirstTurnOnly";
                entry.Blocks.Add(new ChatSessionTranscriptBlock
                {
                    Kind = ChatSessionTranscriptBlockKind.StructuredXml,
                    Content = _shellContextSnapshotXml,
                    Title = "Shell Context"
                });

                entry.Touch();
                foreach (var block in entry.Blocks)
                {
                    block.Touch();
                }

                session.Transcript.Entries.Add(entry);
                session.Transcript.Touch();
            }
        }

        private string CreateUniqueShellSessionName()
        {
            var baseName = $"Shell Chat {DateTime.Now:yyyyMMdd HHmmss}";
            for (var index = 0; index < 100; index++)
            {
                var candidate = index == 0
                    ? baseName
                    : $"{baseName}-{index + 1}";
                if (!Directory.Exists(Path.Combine(_chatSessionRepository.RootFolderPath, candidate)))
                {
                    return candidate;
                }
            }

            return $"Shell Chat {DateTime.Now:yyyyMMdd HHmmss fff}";
        }

        private static string BuildInitialStatusText(ChatSessionModel session)
        {
            return session.HasBoundFlow
                ? LF("ShellChat.Status.FlowReadyFormat", "Ready: {0}", session.BoundFlowDisplayName)
                : L("ShellChat.Status.Ready", "Ready");
        }

        private void RefreshMessagesFromTranscript()
        {
            if (_sessionModel == null)
            {
                return;
            }

            var projectedMessages = _presentationProjector.Project(_sessionModel.Transcript);
            var existingMessagesBySourceEntryId = Messages
                .Select(message => new
                {
                    Message = message,
                    SourceEntryId = message.SourceEntryId?.Trim() ?? string.Empty
                })
                .Where(item => item.SourceEntryId.Length > 0)
                .GroupBy(item => item.SourceEntryId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Message, StringComparer.OrdinalIgnoreCase);

            for (var targetIndex = 0; targetIndex < projectedMessages.Count; targetIndex++)
            {
                var projectedMessage = projectedMessages[targetIndex];
                var existingMessage = TryGetExistingMessage(existingMessagesBySourceEntryId, projectedMessage);
                if (existingMessage == null)
                {
                    HydrateToolCallPresentations(projectedMessage);
                    Messages.Insert(targetIndex, projectedMessage);
                    continue;
                }

                UpdateMessage(existingMessage, projectedMessage);
                var currentIndex = Messages.IndexOf(existingMessage);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                {
                    Messages.Move(currentIndex, targetIndex);
                }
            }

            for (var index = Messages.Count - 1; index >= projectedMessages.Count; index--)
            {
                Messages.RemoveAt(index);
            }

            UpdateLatestToolCallPart();
        }

        private static ChatMessageModel? TryGetExistingMessage(
            IReadOnlyDictionary<string, ChatMessageModel> existingMessagesBySourceEntryId,
            ChatMessageModel projectedMessage)
        {
            var sourceEntryId = projectedMessage.SourceEntryId?.Trim() ?? string.Empty;
            return sourceEntryId.Length > 0 &&
                   existingMessagesBySourceEntryId.TryGetValue(sourceEntryId, out var existingMessage)
                ? existingMessage
                : null;
        }

        private void UpdateMessage(ChatMessageModel target, ChatMessageModel source)
        {
            target.Role = source.Role;
            target.DisplayName = source.DisplayName;
            target.AvatarPath = source.AvatarPath;
            target.Timestamp = source.Timestamp;
            target.SourceEntryId = source.SourceEntryId;
            ReplaceSourceEntryIds(target, source);
            UpdateParts(target, source);
            HydrateToolCallPresentations(target);
        }

        private static void ReplaceSourceEntryIds(ChatMessageModel target, ChatMessageModel source)
        {
            target.SourceEntryIds.Clear();
            foreach (var entryId in source.SourceEntryIds)
            {
                target.SourceEntryIds.Add(entryId);
            }
        }

        private static void UpdateParts(ChatMessageModel target, ChatMessageModel source)
        {
            for (var index = 0; index < source.Parts.Count; index++)
            {
                var sourcePart = source.Parts[index];
                if (index >= target.Parts.Count)
                {
                    target.Parts.Add(sourcePart);
                    continue;
                }

                UpdatePart(target.Parts[index], sourcePart);
            }

            for (var index = target.Parts.Count - 1; index >= source.Parts.Count; index--)
            {
                target.Parts.RemoveAt(index);
            }
        }

        private static void UpdatePart(ChatMessagePartModel target, ChatMessagePartModel source)
        {
            var preserveExpansionState = target.PartType == ChatMessagePartType.Reasoning &&
                                         source.PartType == ChatMessagePartType.Reasoning &&
                                         target.IsCollapsible &&
                                         source.IsCollapsible;

            if (ShouldDetachToolPresentation(target, source))
            {
                target.DetachToolPresentation();
            }

            target.PartType = source.PartType;
            target.Title = source.Title;
            target.Language = source.Language;
            target.BadgeText = source.BadgeText;
            target.ToolCallId = source.ToolCallId;
            target.CallerAgentId = source.CallerAgentId;
            target.ResourcePath = source.ResourcePath;
            target.PresentationKind = source.PresentationKind;
            target.ToolResultPresentationKind = source.ToolResultPresentationKind;
            target.ToolProgress = source.ToolProgress;
            target.IsUserVisible = source.IsUserVisible;
            target.IsCollapsible = source.IsCollapsible;
            target.IsStreaming = source.IsStreaming;
            target.Content = source.Content;
            target.ToolResultContent = source.ToolResultContent;

            if (!preserveExpansionState)
            {
                target.IsExpanded = source.IsExpanded;
            }
        }

        private static bool ShouldDetachToolPresentation(
            ChatMessagePartModel target,
            ChatMessagePartModel source)
        {
            if (target.ToolPresentationState == null && target.ToolPresentationView == null)
            {
                return false;
            }

            if (source.PartType != ChatMessagePartType.ToolCall ||
                target.PartType != ChatMessagePartType.ToolCall)
            {
                return true;
            }

            if (!string.Equals(target.ToolCallId, source.ToolCallId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(target.Title) &&
                !string.IsNullOrWhiteSpace(source.Title) &&
                !string.Equals(target.Title, source.Title, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private void UpdateLatestToolCallPart()
        {
            LatestToolCallPart = Messages
                .SelectMany(message => message.Parts)
                .LastOrDefault(part =>
                    part.PartType == ChatMessagePartType.ToolCall &&
                    part.ToolPresentationView != null &&
                    part.IsUserVisible);
        }

        private void HydrateToolCallPresentations(ChatMessageModel message)
        {
            var toolCallIndex = 0;
            foreach (var part in message.Parts)
            {
                if (part.PartType != ChatMessagePartType.ToolCall)
                {
                    continue;
                }

                toolCallIndex++;
                _toolInvocationPresentationService.TryAttachPresentation(part, toolCallIndex);
            }
        }

        private void AddSystemStatusMessage(string content, string title)
        {
            var message = new ChatMessageModel(
                ChatMessageRole.System,
                L("ShellChat.DisplayName.System", "System"),
                "pack://application:,,,/Resources/QuestionBot.png",
                DateTime.Now,
                [ChatMessagePartModel.CreateStatus(content, title)]);
            Messages.Add(message);
        }

        private static string BuildShellContextText(ShellChatStartupContext context)
        {
            var contextElement = new XElement(
                "ShellContext",
                new XAttribute("Source", "WindowsExplorer"),
                new XElement(
                    "InvocationKind",
                    context.SelectedPaths.Count > 0 ? "Selection" : "DirectoryBackground"),
                CreateOptionalElement("WorkingDirectory", ResolveWorkingDirectory(context)),
                new XElement(
                    "SelectedItems",
                    context.SelectedPaths.Select(CreateSelectedItemElement)));

            var preservedContentElements = context.SelectedPaths
                .Where(File.Exists)
                .Select(CreatePreservedContentElement)
                .Where(element => element != null)
                .Cast<XElement>()
                .ToArray();

            var snapshotElement = new XElement(
                "ShellContextSnapshot",
                new XAttribute("CapturedAtUtc", DateTime.UtcNow.ToString("O")),
                new XAttribute("InjectionPolicy", "FirstTurnOnly"),
                new XElement(
                    "Instruction",
                    "The user opened Skyweaver from Windows Explorer. Treat this as one-turn shell context for the current user request only. Items marked TransferMode=\"PathOnly\" are path references, not text or multimodal content; do not read, inline, or execute them unless the user explicitly asks and an appropriate confirmed tool is used."),
                contextElement,
                new XElement("PreservedContents", preservedContentElements));

            return snapshotElement.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement CreateSelectedItemElement(string path)
        {
            var normalizedPath = path.Trim();
            var kind = Directory.Exists(normalizedPath)
                ? "Directory"
                : File.Exists(normalizedPath)
                    ? "File"
                    : "Missing";

            return new XElement(
                "Item",
                new XAttribute("Kind", kind),
                new XAttribute("Path", normalizedPath),
                new XAttribute("Name", Path.GetFileName(normalizedPath)),
                CreateOptionalAttribute("Extension", Path.GetExtension(normalizedPath)),
                CreateOptionalAttribute("SizeBytes", TryGetFileSizeText(normalizedPath)),
                CreateOptionalAttribute("TransferMode", ResolveTransferMode(normalizedPath, kind)),
                CreateOptionalAttribute("ContentKind", ResolveContentKind(normalizedPath, kind)));
        }

        private static XElement? CreatePreservedContentElement(string path)
        {
            if (!LanguageModelMediaResourcePolicy.TryResolvePath(path, out var descriptor) ||
                !LanguageModelMediaResourcePolicy.CanReadLocalMediaFile(
                    path,
                    descriptor.Kind,
                    descriptor.MediaType,
                    out var mediaType,
                    out _))
            {
                return null;
            }

            return new XElement(
                "PreservedContent",
                new XElement(
                    descriptor.ElementName,
                    new XAttribute("Path", path),
                    new XAttribute("MediaType", mediaType)));
        }

        private static string ResolveTransferMode(string path, string kind)
        {
            if (!string.Equals(kind, "File", StringComparison.OrdinalIgnoreCase))
            {
                return "PathOnly";
            }

            return LanguageModelMediaResourcePolicy.TryResolvePath(path, out var descriptor) &&
                   LanguageModelMediaResourcePolicy.CanReadLocalMediaFile(
                       path,
                       descriptor.Kind,
                       descriptor.MediaType,
                       out _,
                       out _)
                ? "PreservedContent"
                : "PathOnly";
        }

        private static string? ResolveContentKind(string path, string kind)
        {
            if (!string.Equals(kind, "File", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return LanguageModelMediaResourcePolicy.TryResolvePath(path, out var descriptor) &&
                   LanguageModelMediaResourcePolicy.CanReadLocalMediaFile(
                       path,
                       descriptor.Kind,
                       descriptor.MediaType,
                       out _,
                       out _)
                ? descriptor.ElementName
                : "Path";
        }

        private static string? TryGetFileSizeText(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static string ResolveWorkingDirectory(ShellChatStartupContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.BackgroundDirectoryPath))
            {
                return context.BackgroundDirectoryPath.Trim();
            }

            var firstPath = context.SelectedPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))?.Trim();
            if (string.IsNullOrWhiteSpace(firstPath))
            {
                return string.Empty;
            }

            if (Directory.Exists(firstPath))
            {
                return firstPath;
            }

            return Path.GetDirectoryName(firstPath) ?? string.Empty;
        }

        private static XElement? CreateOptionalElement(string name, string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new XElement(name, value.Trim());
        }

        private static XAttribute? CreateOptionalAttribute(string name, string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new XAttribute(name, value.Trim());
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static string LF(string resourceKey, string fallbackFormat, params object?[] args)
        {
            return string.Format(L(resourceKey, fallbackFormat), args);
        }
    }
}
