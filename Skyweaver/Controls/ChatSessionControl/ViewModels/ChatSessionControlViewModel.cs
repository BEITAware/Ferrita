using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Skyweaver.Commands;
using Skyweaver.Controls.ChatSessionControl.Models;
using Skyweaver.Controls.ChatSessionControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.ChatSession;
using Skyweaver.Services;
using Skyweaver.Windows;

namespace Skyweaver.Controls.ChatSessionControl.ViewModels
{
    public sealed class ChatSessionControlViewModel : ObservableObject
    {
        private const string UserAvatarPath = "pack://application:,,,/Resources/image.png";
        private const string AssistantAvatarPath = "pack://application:,,,/Resources/GuideBot.png";
        private const string SystemAvatarPath = "pack://application:,,,/Resources/QuestionBot.png";

        private readonly ChatSessionModel? _sessionModel;
        private readonly ChatSessionRepository? _chatSessionRepository;
        private readonly ChatSessionFlowBindingService _flowBindingService;
        private readonly ChatSessionRuntimeService _runtimeService;
        private readonly ChatComposerImageAttachmentService _composerImageAttachmentService;
        private readonly ToolInvocationPresentationService _toolInvocationPresentationService;
        private readonly ChatSessionPresentationProjector _presentationProjector;
        private readonly string _sessionFlowValidationSummary;

        private string _draftMessageText = string.Empty;
        private ChatMessageModel? _selectedMessage;
        private bool _isExecutionActive;
        private string _executionStatusText = "就绪";
        private bool _suppressPersistence;
        private DateTime _lastStreamingProjectionRefreshUtc = DateTime.MinValue;
        private CancellationTokenSource? _executionCancellationSource;

        private static readonly TimeSpan StreamingProjectionRefreshInterval = TimeSpan.FromMilliseconds(80);

        public ObservableCollection<ChatMessageModel> Messages { get; } = new();

        public ObservableCollection<ChatComposerAttachmentModel> PendingComposerImages { get; } = new();

        public string SessionTitle { get; }

        public string? SessionSubtitle { get; }

        public bool HasBoundSessionFlow => _sessionModel?.HasBoundFlow == true;

        public string BoundSessionFlowName => _sessionModel?.BoundFlowDisplayName ?? "未绑定会话流";

        public string BoundSessionFlowSummary => HasBoundSessionFlow
            ? $"当前会话流：{BoundSessionFlowName}"
            : "未绑定会话流。";

        public string SessionFlowValidationSummary => _sessionFlowValidationSummary;

        public string DraftMessageText
        {
            get => _draftMessageText;
            set
            {
                if (SetProperty(ref _draftMessageText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(CanSendMessage));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public ChatMessageModel? SelectedMessage
        {
            get => _selectedMessage;
            set
            {
                if (SetProperty(ref _selectedMessage, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
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
                    OnPropertyChanged(nameof(ComposerHintText));
                    OnPropertyChanged(nameof(ComposerPrimaryButtonText));
                    OnPropertyChanged(nameof(ComposerPrimaryCommand));
                    OnPropertyChanged(nameof(IsComposerPrimaryButtonLatched));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanSendMessage => !IsExecutionActive &&
                                      (!string.IsNullOrWhiteSpace(DraftMessageText) ||
                                       PendingComposerImages.Count > 0);

        public bool CanCancelExecution => IsExecutionActive;

        public bool IsEmpty => Messages.Count == 0;

        public bool HasPendingComposerImages => PendingComposerImages.Count > 0;

        public string ExecutionStatusText
        {
            get => _executionStatusText;
            private set => SetProperty(ref _executionStatusText, value ?? string.Empty);
        }

        public string ComposerHintText => IsExecutionActive
            ? "当前轮次正在运行。"
            : "按 Enter 发送；Ctrl+Enter 或 Shift+Enter 换行。";

        public string ComposerPrimaryButtonText => IsExecutionActive ? "停止" : "发送";

        public ICommand ComposerPrimaryCommand => IsExecutionActive ? CancelExecutionCommand : SendMessageCommand;

        public bool IsComposerPrimaryButtonLatched => IsExecutionActive;

        public ICommand SendMessageCommand { get; }

        public ICommand CancelExecutionCommand { get; }

        public ICommand RemoveSelectedMessageCommand { get; }

        public ICommand RemoveMessageCommand { get; }

        public ICommand CopyMessageCommand { get; }

        public ICommand CopyMessageAsMarkdownCommand { get; }

        public ICommand CopyMessageAsPlainTextCommand { get; }

        public ICommand EditMessageCommand { get; }

        public ICommand ClearMessagesCommand { get; }

        public ICommand RemoveComposerImageCommand { get; }

        public ICommand AddImageCommand { get; }

        public ICommand AddAudioCommand { get; }

        public ICommand AddClipboardCommand { get; }

        public ChatSessionControlViewModel(
            string sessionTitle,
            string? sessionSubtitle = null,
            ChatSessionModel? sessionModel = null,
            ChatSessionRepository? chatSessionRepository = null,
            ChatSessionRuntimeService? runtimeService = null)
        {
            SessionTitle = string.IsNullOrWhiteSpace(sessionTitle) ? "鑱婂ぉ浼氳瘽" : sessionTitle;
            SessionSubtitle = string.IsNullOrWhiteSpace(sessionSubtitle) ? null : sessionSubtitle;
            _sessionModel = sessionModel;
            _chatSessionRepository = chatSessionRepository;
            _runtimeService = runtimeService ?? new ChatSessionRuntimeService();
            _composerImageAttachmentService = new ChatComposerImageAttachmentService();
            _toolInvocationPresentationService = new ToolInvocationPresentationService();
            _presentationProjector = new ChatSessionPresentationProjector();
            _flowBindingService = new ChatSessionFlowBindingService();
            _sessionFlowValidationSummary = BuildSessionFlowValidationSummary(sessionModel);

            SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => CanSendMessage);
            CancelExecutionCommand = new RelayCommand(CancelExecution, () => CanCancelExecution);
            RemoveSelectedMessageCommand = new RelayCommand(RemoveSelectedMessage, () => SelectedMessage != null && !IsExecutionActive);
            RemoveMessageCommand = new RelayCommand<ChatMessageModel>(RemoveMessage, message => message != null && !IsExecutionActive);
            CopyMessageCommand = new RelayCommand<ChatMessageModel>(
                message => CopyMessageToClipboard(message, ChatMessageCopyFormat.Full),
                message => message != null);
            CopyMessageAsMarkdownCommand = new RelayCommand<ChatMessageModel>(
                message => CopyMessageToClipboard(message, ChatMessageCopyFormat.Markdown),
                message => message != null);
            CopyMessageAsPlainTextCommand = new RelayCommand<ChatMessageModel>(
                message => CopyMessageToClipboard(message, ChatMessageCopyFormat.PlainText),
                message => message != null);
            EditMessageCommand = new RelayCommand<ChatMessageModel>(ShowEditMessagePlaceholder, message => message != null);
            ClearMessagesCommand = new RelayCommand(ClearMessages, () => Messages.Count > 0 && !IsExecutionActive);
            RemoveComposerImageCommand = new RelayCommand<ChatComposerAttachmentModel>(
                RemoveComposerImage,
                attachment => attachment != null && !IsExecutionActive);
            AddImageCommand = new RelayCommand(AddImage, () => !IsExecutionActive);
            AddAudioCommand = new RelayCommand(AddAudio, () => !IsExecutionActive);
            AddClipboardCommand = new RelayCommand(AddClipboard, () => !IsExecutionActive);

            Messages.CollectionChanged += OnMessagesCollectionChanged;
            PendingComposerImages.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasPendingComposerImages));
                OnPropertyChanged(nameof(CanSendMessage));
                CommandManager.InvalidateRequerySuggested();
            };

            if (_sessionModel != null)
            {
                RefreshMessagesFromTranscript();
            }
        }

        private async Task SendMessageAsync()
        {
            var trimmedText = DraftMessageText.Trim();
            var pendingImages = PendingComposerImages.ToArray();
            if (trimmedText.Length == 0 && pendingImages.Length == 0)
            {
                return;
            }

            if (_sessionModel == null)
            {
                AddSystemStatusMessage("此聊天视图未绑定到 ChatSessionModel。", "运行时不可用");
                return;
            }

            if (_chatSessionRepository == null)
            {
                AddSystemStatusMessage("此聊天视图未绑定到 ChatSessionRepository。", "运行时不可用");
                return;
            }

            var userContentBlocks = pendingImages
                .Select(image => image.IsAudio
                    ? LanguageModelChatContentBlock.CreateAudio(
                        image.ResourcePath,
                        image.MediaType)
                    : LanguageModelChatContentBlock.CreateImage(
                        image.ResourcePath,
                        image.MediaType))
                .ToArray();

            DraftMessageText = string.Empty;
            PendingComposerImages.Clear();

            _executionCancellationSource?.Dispose();
            _executionCancellationSource = new CancellationTokenSource();
            IsExecutionActive = true;
            ExecutionStatusText = $"正在运行：{BoundSessionFlowName}";

            ChatSessionRuntimeResult? runtimeResult = null;
            try
            {
                runtimeResult = await _runtimeService.ExecuteTurnAsync(
                    new ChatSessionRuntimeRequest
                    {
                        Session = _sessionModel,
                        UserText = trimmedText,
                        UserContentBlocks = userContentBlocks,
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

                AddSystemStatusMessage(ex.Message, "执行失败");
            }
            finally
            {
                _executionCancellationSource?.Dispose();
                _executionCancellationSource = null;
                IsExecutionActive = false;

                ExecutionStatusText = runtimeResult switch
                {
                    { IsCompleted: true } => "就绪",
                    { IsCancelled: true } => "已取消",
                    _ => "失败"
                };

                RefreshMessagesFromTranscript();
            }
        }

        private void CancelExecution()
        {
            if (!IsExecutionActive)
            {
                return;
            }

            _runtimeService.CancelActiveExecution(_sessionModel?.SessionId);
            _executionCancellationSource?.Cancel();
            ExecutionStatusText = "正在取消当前执行...";
        }

        public bool AddPastedImage(BitmapSource? image)
        {
            if (image == null)
            {
                return false;
            }

            if (_sessionModel == null)
            {
                AddSystemStatusMessage("无法保存粘贴的图片，因为当前视图未绑定到 ChatSessionModel。", "图片粘贴失败");
                return false;
            }

            try
            {
                PendingComposerImages.Add(_composerImageAttachmentService.SavePastedImage(_sessionModel, image));
                return true;
            }
            catch (Exception ex)
            {
                AddSystemStatusMessage(ex.Message, "图片粘贴失败");
                return false;
            }
        }

        private void RemoveComposerImage(ChatComposerAttachmentModel? attachment)
        {
            if (attachment != null)
            {
                PendingComposerImages.Remove(attachment);
            }
        }

        private void AddImage()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() != true)
            {
                return;
            }

            foreach (var fileName in openFileDialog.FileNames)
            {
                try
                {
                    AddPastedImage(new BitmapImage(new Uri(fileName, UriKind.Absolute)));
                }
                catch (Exception ex)
                {
                    AddSystemStatusMessage($"无法加载图片 {fileName}: {ex.Message}", "添加图片失败");
                }
            }
        }

        private void AddAudio()
        {
            if (_sessionModel == null)
            {
                AddSystemStatusMessage("此聊天视图未绑定到 ChatSessionModel。", "语音不可用");
                return;
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Audio Files|*.wav;*.mp3;*.m4a;*.ogg;*.flac|All Files|*.*",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() != true)
            {
                return;
            }

            foreach (var fileName in openFileDialog.FileNames)
            {
                try
                {
                    PendingComposerImages.Add(_composerImageAttachmentService.SaveMediaFile(_sessionModel, fileName));
                }
                catch (Exception ex)
                {
                    AddSystemStatusMessage($"无法添加音频文件 {fileName}: {ex.Message}", "添加音频失败");
                }
            }
        }

        private void AddClipboard()
        {
            if (ClipboardAccessService.TryGetImage(out var image, out var imageError))
            {
                AddPastedImage(image);
                return;
            }

            if (ClipboardAccessService.TryGetText(out var clipboardText, out var textError))
            {
                DraftMessageText += clipboardText;
                return;
            }

            var errorMessage = imageError ?? textError;
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                AddSystemStatusMessage(errorMessage, "读取剪贴板失败");
            }
        }

        private ValueTask HandleRuntimeEventAsync(
            ChatSessionRuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(runtimeEvent);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplyRuntimeEvent(runtimeEvent);
                return ValueTask.CompletedTask;
            }

            return new ValueTask(dispatcher.InvokeAsync(() => ApplyRuntimeEvent(runtimeEvent)).Task);
        }

        private void ApplyRuntimeEvent(ChatSessionRuntimeEvent runtimeEvent)
        {
            ExecutionStatusText = runtimeEvent.Kind switch
            {
                ChatSessionRuntimeEventKind.ExecutionStarted => $"正在运行：{runtimeEvent.FlowName}",
                ChatSessionRuntimeEventKind.NodeStarted => $"正在运行节点：{runtimeEvent.NodeTitle}",
                ChatSessionRuntimeEventKind.NodeCompleted => $"节点已完成：{runtimeEvent.NodeTitle}",
                ChatSessionRuntimeEventKind.AgentIterationStarted => runtimeEvent.IterationNumber is int started
                    ? $"代理 {runtimeEvent.NodeTitle} 迭代 {started}"
                    : $"代理 {runtimeEvent.NodeTitle} 已启动",
                ChatSessionRuntimeEventKind.AgentIterationCompleted => runtimeEvent.IterationNumber is int completed
                    ? $"代理 {runtimeEvent.NodeTitle} 已完成迭代 {completed}"
                    : $"代理 {runtimeEvent.NodeTitle} 已完成",
                ChatSessionRuntimeEventKind.ExecutionCompleted => "执行已完成",
                ChatSessionRuntimeEventKind.ExecutionFailed => "执行失败",
                ChatSessionRuntimeEventKind.ExecutionCancelled => "执行已取消",
                _ => ExecutionStatusText
            };

            if (ShouldRefreshProjection(runtimeEvent))
            {
                RefreshMessagesFromTranscript();
            }
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
                PromptText = $"代理 {request.Agent.DisplayNameOrFallback} 请求执行下面的工具调用。确认后才会继续执行。",
                MetadataText = $"工具：{request.Invocation.ToolName}    迭代：{request.IterationNumber}    调用序号：{toolCallIndex}",
                InvocationXml = request.Invocation.InvocationXml,
                InvocationPreview = previewHandle.View
            })
            {
                Owner = Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true
                ? AgentToolConfirmationResult.Approve()
                : AgentToolConfirmationResult.Reject("用户拒绝了此工具调用。");
        }

        private void RemoveSelectedMessage()
        {
            RemoveMessage(SelectedMessage);
        }

        private void CopyMessageToClipboard(ChatMessageModel? message, ChatMessageCopyFormat format)
        {
            if (message == null)
            {
                return;
            }

            var exportText = ChatMessageClipboardExporter.Build(message, format);
            if (ClipboardAccessService.TrySetText(exportText, out var errorMessage))
            {
                return;
            }

            MessageBox.Show(
                Application.Current?.MainWindow,
                errorMessage ?? "无法写入系统剪贴板。",
                "复制失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static void ShowEditMessagePlaceholder(ChatMessageModel? message)
        {
            if (message == null)
            {
                return;
            }

            MessageBox.Show(
                Application.Current?.MainWindow,
                "消息编辑功能尚未实现，这里先保留入口位。",
                "编辑消息",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void RemoveMessage(ChatMessageModel? message)
        {
            if (message == null || _sessionModel == null)
            {
                return;
            }

            var sourceEntryIds = message.SourceEntryIds.Count > 0
                ? message.SourceEntryIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : string.IsNullOrWhiteSpace(message.SourceEntryId)
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { message.SourceEntryId };

            if (sourceEntryIds.Count > 0)
            {
                var removedEntries = _sessionModel.Transcript.Entries
                    .Where(candidate => sourceEntryIds.Contains(candidate.EntryId))
                    .ToArray();
                if (removedEntries.Length > 0)
                {
                    foreach (var entry in removedEntries)
                    {
                        _sessionModel.Transcript.Entries.Remove(entry);
                    }

                    foreach (var turn in _sessionModel.Transcript.Turns)
                    {
                        if (!string.IsNullOrWhiteSpace(turn.UserEntryId) &&
                            sourceEntryIds.Contains(turn.UserEntryId))
                        {
                            turn.UserEntryId = null;
                        }

                        if (!string.IsNullOrWhiteSpace(turn.FinalEntryId) &&
                            sourceEntryIds.Contains(turn.FinalEntryId))
                        {
                            turn.FinalEntryId = null;
                        }
                    }

                    _sessionModel.Transcript.Touch();
                    PersistSession();
                    RefreshMessagesFromTranscript();
                    return;
                }
            }

            Messages.Remove(message);
            SelectedMessage = Messages.LastOrDefault();
        }

        private void ClearMessages()
        {
            if (_sessionModel != null)
            {
                _sessionModel.Transcript.Turns.Clear();
                _sessionModel.Transcript.Entries.Clear();
                _sessionModel.Transcript.Touch();
                PersistSession();
            }

            RefreshMessagesFromTranscript();
        }

        private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsEmpty));
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshMessagesFromTranscript()
        {
            if (_sessionModel == null)
            {
                return;
            }

            var projectedMessages = _presentationProjector.Project(_sessionModel.Transcript);
            _suppressPersistence = true;
            try
            {
                for (var targetIndex = 0; targetIndex < projectedMessages.Count; targetIndex++)
                {
                    var projectedMessage = projectedMessages[targetIndex];
                    var existingMessage = FindExistingMessage(projectedMessage);
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

                SelectedMessage = Messages.LastOrDefault();
            }
            finally
            {
                _suppressPersistence = false;
            }
        }

        private bool ShouldRefreshProjection(ChatSessionRuntimeEvent runtimeEvent)
        {
            if (!IsHighFrequencyStreamingProjectionEvent(runtimeEvent))
            {
                _lastStreamingProjectionRefreshUtc = DateTime.UtcNow;
                return true;
            }

            var now = DateTime.UtcNow;
            if (now - _lastStreamingProjectionRefreshUtc < StreamingProjectionRefreshInterval)
            {
                return false;
            }

            _lastStreamingProjectionRefreshUtc = now;
            return true;
        }

        private static bool IsHighFrequencyStreamingProjectionEvent(ChatSessionRuntimeEvent runtimeEvent)
        {
            return (runtimeEvent.Kind is ChatSessionRuntimeEventKind.TextDelta
                    or ChatSessionRuntimeEventKind.ReasoningDelta) ||
                runtimeEvent.Kind == ChatSessionRuntimeEventKind.ToolCallUpdated &&
                runtimeEvent.ToolCallSnapshot?.IsInvocationClosed != true &&
                runtimeEvent.ToolInvocation == null;
        }

        private ChatMessageModel? FindExistingMessage(ChatMessageModel projectedMessage)
        {
            return Messages.FirstOrDefault(existing =>
                string.Equals(existing.SourceEntryId, projectedMessage.SourceEntryId, StringComparison.OrdinalIgnoreCase));
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

        private void PersistSession()
        {
            if (_suppressPersistence || _sessionModel == null || _chatSessionRepository == null)
            {
                return;
            }

            _chatSessionRepository.Save(_sessionModel);
        }

        private void AddSystemStatusMessage(string content, string title)
        {
            var message = CreateChatMessage(
                ChatMessageRole.System,
                ChatMessagePartModel.CreateStatus(content, title));
            Messages.Add(message);
            SelectedMessage = message;
        }

        private void HydrateToolCallPresentations(ChatMessageModel message)
        {
            ArgumentNullException.ThrowIfNull(message);

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

        private static ChatMessageModel CreateChatMessage(ChatMessageRole role, params ChatMessagePartModel[] parts)
        {
            return new ChatMessageModel(role, GetDisplayName(role), GetAvatarPath(role), DateTime.Now, parts);
        }

        private static string GetDisplayName(ChatMessageRole role)
        {
            return role switch
            {
                ChatMessageRole.Assistant => "Skyweaver 鍔╂墜",
                ChatMessageRole.System => "绯荤粺",
                _ => "鐢ㄦ埛"
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

        private string BuildSessionFlowValidationSummary(ChatSessionModel? sessionModel)
        {
            if (sessionModel?.HasBoundFlow != true)
            {
                return "未绑定会话流。";
            }

            var compilationResult = _flowBindingService.CompileBinding(sessionModel.FlowBinding);
            if (compilationResult.IsSuccess)
            {
                return "绑定的会话流已通过运行时验证。";
            }

            return compilationResult.Errors.FirstOrDefault()?.Message
                ?? compilationResult.Issues.FirstOrDefault()?.Message
                ?? "绑定的会话流未通过运行时验证。";
        }
    }
}
