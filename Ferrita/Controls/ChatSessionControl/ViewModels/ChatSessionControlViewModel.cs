using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Ferrita.Commands;
using Ferrita.Controls.ChatSessionControl.Models;
using Ferrita.Controls.ChatSessionControl.Services;
using Ferrita.Controls.LanguageModelConfigurationControl.Services;
using Ferrita.Controls.WorkflowEditorControl.Services;
using Ferrita.Infrastructure.Mvvm;
using Ferrita.Models.ChatSession;
using Ferrita.Panels.DocumentWorkspace.Contracts;
using Ferrita.Services.AgentLoop;
using Ferrita.Services.ChatSession;
using Ferrita.Services;
using Ferrita.Services.Localization;
using Ferrita.Services.Memory;
using Ferrita.Windows;

namespace Ferrita.Controls.ChatSessionControl.ViewModels
{
    public sealed class ChatSessionControlViewModel : ObservableObject, IWorkspaceDocumentCloseAware
    {
        private const string UserAvatarPath = "pack://application:,,,/Resources/image.png";
        private const string AssistantAvatarPath = "pack://application:,,,/Resources/GuideBot.png";
        private const string SystemAvatarPath = "pack://application:,,,/Resources/QuestionBot.png";

        private readonly ChatSessionModel? _sessionModel;
        private readonly ChatSessionRepository? _chatSessionRepository;
        private readonly ChatSessionFlowBindingService _flowBindingService;
        private readonly ChatSessionRuntimeService _runtimeService;
        private readonly ChatSessionContextWindowService _contextWindowService;
        private readonly ChatComposerImageAttachmentService _composerImageAttachmentService;
        private readonly ToolInvocationPresentationService _toolInvocationPresentationService;
        private readonly ChatSessionPresentationProjector _presentationProjector;
        private MemoryService? _memoryService;
        private string _sessionFlowValidationSummary = L("ChatSessionControl.BoundFlow.ValidationPending", "Session flow validation is pending.");

        private string _draftMessageText = string.Empty;
        private ChatMessageModel? _selectedMessage;
        private bool _isExecutionActive;
        private string _executionStatusText = L("ChatSessionControl.Status.Ready", "就绪");
        private bool _suppressPersistence;
        private readonly object _streamingRuntimeEventSync = new();
        private ChatSessionRuntimeEvent? _pendingStreamingRuntimeEvent;
        private bool _isStreamingRuntimeEventDispatchQueued;
        private System.Windows.Threading.DispatcherTimer? _streamingProjectionTimer;
        private int _streamingProjectionIdleTickCount;
        private CancellationTokenSource? _executionCancellationSource;

        private static readonly TimeSpan StreamingProjectionRefreshInterval = TimeSpan.FromMilliseconds(33);

        private MemoryService MemoryService => _memoryService ??= new MemoryService();

        private double _contextWindowUsageRatio;
        private string _contextWindowUsageText = L("ChatSessionControl.ContextWindow.Reading", "正在读取上下文窗口。");
        private string _contextWindowStatusText = L("ChatSessionControl.ContextWindow.StatusUnavailable", "上下文 --");

        public ObservableCollection<ChatMessageModel> Messages { get; } = new();

        public ObservableCollection<ChatComposerAttachmentModel> PendingComposerImages { get; } = new();

        public string SessionTitle { get; }

        public string? SessionSubtitle { get; }

        public string? SessionId => _sessionModel?.SessionId;

        public bool HasBoundSessionFlow => _sessionModel?.HasBoundFlow == true;

        public string BoundSessionFlowName => _sessionModel?.BoundFlowDisplayName ?? L("ChatSession.BoundFlow.Unbound", "未绑定会话流");

        public string BoundSessionFlowHeaderText => LF("ChatSessionControl.BoundFlow.HeaderFormat", "会话流：{0}", BoundSessionFlowName);

        public string BoundSessionFlowSummary => HasBoundSessionFlow
            ? LF("ChatSessionControl.BoundFlow.SummaryFormat", "当前会话流：{0}", BoundSessionFlowName)
            : L("ChatSessionControl.BoundFlow.UnboundSummary", "未绑定会话流。");

        public string SessionFlowValidationSummary
        {
            get => _sessionFlowValidationSummary;
            private set => SetProperty(ref _sessionFlowValidationSummary, value ?? string.Empty);
        }

        public string MessageCountText => LF("ChatSessionControl.MessageCountFormat", "当前消息 {0} 条", Messages.Count);

        public double ContextWindowUsageRatio
        {
            get => _contextWindowUsageRatio;
            set => SetProperty(ref _contextWindowUsageRatio, value);
        }

        public string ContextWindowUsageText
        {
            get => _contextWindowUsageText;
            set => SetProperty(ref _contextWindowUsageText, value ?? string.Empty);
        }

        public string ContextWindowStatusText
        {
            get => _contextWindowStatusText;
            set => SetProperty(ref _contextWindowStatusText, value ?? string.Empty);
        }

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
            ? L("ChatSessionControl.Composer.Hint.Running", "当前轮次正在运行。")
            : L("ChatSessionControl.Composer.Hint.Ready", "按 Enter 发送；Ctrl+Enter 或 Shift+Enter 换行。");

        public string ComposerPrimaryButtonText => IsExecutionActive
            ? L("ChatSessionControl.Stop", "停止")
            : L("ChatSessionControl.Send", "发送");

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

        public ICommand AddVideoCommand { get; }

        public ICommand AddDocumentCommand { get; }

        public ICommand AddClipboardCommand { get; }

        public ChatSessionControlViewModel(
            string sessionTitle,
            string? sessionSubtitle = null,
            ChatSessionModel? sessionModel = null,
            ChatSessionRepository? chatSessionRepository = null,
            ChatSessionRuntimeService? runtimeService = null)
        {
            SessionTitle = string.IsNullOrWhiteSpace(sessionTitle) ? L("ChatSessionControl.SessionTitleFallback", "聊天会话") : sessionTitle;
            SessionSubtitle = string.IsNullOrWhiteSpace(sessionSubtitle) ? null : sessionSubtitle;
            _sessionModel = sessionModel;
            _chatSessionRepository = chatSessionRepository;
            _runtimeService = runtimeService ?? new ChatSessionRuntimeService();
            _contextWindowService = new ChatSessionContextWindowService();
            _composerImageAttachmentService = new ChatComposerImageAttachmentService();
            _toolInvocationPresentationService = new ToolInvocationPresentationService();
            _presentationProjector = new ChatSessionPresentationProjector();
            _flowBindingService = new ChatSessionFlowBindingService();
            _sessionFlowValidationSummary = BuildInitialSessionFlowValidationSummary(sessionModel);

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
            AddVideoCommand = new RelayCommand(AddVideo, () => !IsExecutionActive);
            AddDocumentCommand = new RelayCommand(AddDocument, () => !IsExecutionActive);
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
                RefreshMessagesFromTranscript(refreshContextWindow: false);
                QueueInitialSessionDiagnostics();
            }
            else
            {
                ApplyUnavailableContextWindowStatus(L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"));
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
                AddSystemStatusMessage(
                    L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"),
                    L("ChatSessionControl.Status.RuntimeUnavailable", "运行时不可用"));
                return;
            }

            if (_chatSessionRepository == null)
            {
                AddSystemStatusMessage(
                    L("ChatSessionControl.Error.UnboundRepository", "此聊天视图未绑定到 ChatSessionRepository。"),
                    L("ChatSessionControl.Status.RuntimeUnavailable", "运行时不可用"));
                return;
            }

            var userContentBlocks = pendingImages
                .Select(CreateContentBlockForAttachment)
                .ToArray();

            var historyAttachments = pendingImages.Select(img => new PromptHistoryAttachmentInfo
            {
                DisplayName = img.DisplayName,
                ResourcePath = img.ResourcePath,
                MediaType = img.MediaType,
                PreservedContentXml = img.PreservedContentXml
            }).ToList();
            PromptHistoryService.Instance.AddHistoryEntry(trimmedText, "Normal", historyAttachments);

            DraftMessageText = string.Empty;
            PendingComposerImages.Clear();

            _executionCancellationSource?.Dispose();
            _executionCancellationSource = new CancellationTokenSource();
            IsExecutionActive = true;

            ExecutionStatusText = LF("ChatSessionControl.Status.RunningFlowFormat", "正在运行：{0}", BoundSessionFlowName);
            await Task.Yield();

            var sessionModel = _sessionModel!;
            ChatSessionRuntimeResult? runtimeResult = null;
            try
            {
                runtimeResult = await _runtimeService.ExecuteTurnAsync(
                    new ChatSessionRuntimeRequest
                    {
                        Session = sessionModel,
                        UserText = trimmedText,
                        UserContentBlocks = userContentBlocks,
                        HostInjectedHistoryMessageFactory = ct => MemoryService.RetrieveBackfillMessagesAsync(
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

                AddSystemStatusMessage(ex.Message, L("ChatSessionControl.Status.ExecutionFailed", "执行失败"));
            }
            finally
            {
                _executionCancellationSource?.Dispose();
                _executionCancellationSource = null;
                IsExecutionActive = false;

                ExecutionStatusText = runtimeResult switch
                {
                    { IsCompleted: true } => L("ChatSessionControl.Status.Ready", "就绪"),
                    { IsCancelled: true } => L("ChatSessionControl.Status.Cancelled", "已取消"),
                    _ => L("ChatSessionControl.Status.Failed", "失败")
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
            ExecutionStatusText = L("ChatSessionControl.Status.Cancelling", "正在取消当前执行...");
        }

        public bool AddPastedImage(BitmapSource? image)
        {
            if (image == null)
            {
                return false;
            }

            if (_sessionModel == null)
            {
                AddSystemStatusMessage(
                    L("ChatSessionControl.Error.PastedImageUnboundModel", "无法保存粘贴的图片，因为当前视图未绑定到 ChatSessionModel。"),
                    L("ChatSessionControl.Status.PasteImageFailed", "图片粘贴失败"));
                return false;
            }

            try
            {
                PendingComposerImages.Add(_composerImageAttachmentService.SavePastedImage(_sessionModel, image));
                return true;
            }
            catch (Exception ex)
            {
                AddSystemStatusMessage(ex.Message, L("ChatSessionControl.Status.PasteImageFailed", "图片粘贴失败"));
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

        public void HandleFileDrop(System.Collections.Generic.IEnumerable<string> filePaths)
        {
            if (filePaths == null)
            {
                return;
            }

            if (_sessionModel == null)
            {
                AddSystemStatusMessage(
                    L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"),
                    L("ChatSessionControl.Status.FileUnavailable", "文件不可用"));
                return;
            }

            foreach (var filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                {
                    continue;
                }

                try
                {
                    var attachment = _composerImageAttachmentService.SaveMediaFile(_sessionModel, filePath);
                    PendingComposerImages.Add(attachment);
                }
                catch (Exception ex)
                {
                    AddSystemStatusMessage(
                        LF("ChatSessionControl.Error.AddFileFailedFormat", "无法添加文件 {0}: {1}", filePath, ex.Message),
                        L("ChatSessionControl.Status.AddFileFailed", "添加文件失败"));
                }
            }
        }

        private void AddImage()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L("ChatSessionControl.Dialog.ImageFilter", "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*"),
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
                    AddSystemStatusMessage(
                        LF("ChatSessionControl.Error.LoadImageFailedFormat", "无法加载图片 {0}: {1}", fileName, ex.Message),
                        L("ChatSessionControl.Status.AddImageFailed", "添加图片失败"));
                }
            }
        }

        private void AddAudio()
        {
            if (_sessionModel == null)
            {
                AddSystemStatusMessage(
                    L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"),
                    L("ChatSessionControl.Status.AudioUnavailable", "语音不可用"));
                return;
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L("ChatSessionControl.Dialog.AudioFilter", "Audio Files|*.wav;*.mp3;*.m4a;*.ogg;*.flac|All Files|*.*"),
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
                    AddSystemStatusMessage(
                        LF("ChatSessionControl.Error.AddAudioFailedFormat", "无法添加音频文件 {0}: {1}", fileName, ex.Message),
                        L("ChatSessionControl.Status.AddAudioFailed", "添加音频失败"));
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
                AddSystemStatusMessage(errorMessage, L("ChatSessionControl.Status.ReadClipboardFailed", "读取剪贴板失败"));
            }
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
            ExecutionStatusText = runtimeEvent.Kind switch
            {
                ChatSessionRuntimeEventKind.ExecutionStarted => LF("ChatSessionControl.Status.RunningFlowFormat", "正在运行：{0}", runtimeEvent.FlowName),
                ChatSessionRuntimeEventKind.NodeStarted => LF("ChatSessionControl.Status.NodeStartedFormat", "正在运行节点：{0}", runtimeEvent.NodeTitle),
                ChatSessionRuntimeEventKind.NodeCompleted => LF("ChatSessionControl.Status.NodeCompletedFormat", "节点已完成：{0}", runtimeEvent.NodeTitle),
                ChatSessionRuntimeEventKind.AgentIterationStarted => runtimeEvent.IterationNumber is int started
                    ? LF("ChatSessionControl.Status.AgentIterationStartedFormat", "代理 {0} 迭代 {1}", runtimeEvent.NodeTitle, started)
                    : LF("ChatSessionControl.Status.AgentStartedFormat", "代理 {0} 已启动", runtimeEvent.NodeTitle),
                ChatSessionRuntimeEventKind.AgentIterationCompleted => runtimeEvent.IterationNumber is int completed
                    ? LF("ChatSessionControl.Status.AgentIterationCompletedFormat", "代理 {0} 已完成迭代 {1}", runtimeEvent.NodeTitle, completed)
                    : LF("ChatSessionControl.Status.AgentCompletedFormat", "代理 {0} 已完成", runtimeEvent.NodeTitle),
                ChatSessionRuntimeEventKind.ExecutionCompleted => L("ChatSessionControl.Status.ExecutionCompleted", "执行已完成"),
                ChatSessionRuntimeEventKind.ExecutionFailed => L("ChatSessionControl.Status.ExecutionFailed", "执行失败"),
                ChatSessionRuntimeEventKind.ExecutionCancelled => L("ChatSessionControl.Status.ExecutionCancelled", "执行已取消"),
                ChatSessionRuntimeEventKind.MediaProcessingProgressUpdated when !string.IsNullOrWhiteSpace(runtimeEvent.MediaProcessingProgress?.Progress.StatusText) =>
                    runtimeEvent.MediaProcessingProgress!.Progress.StatusText,
                ChatSessionRuntimeEventKind.ToolProgressUpdated when !string.IsNullOrWhiteSpace(runtimeEvent.ToolProgress?.StatusText) =>
                    runtimeEvent.ToolProgress!.StatusText,
                _ => ExecutionStatusText
            };

            var shouldRefreshProjection = ShouldRefreshProjection(runtimeEvent);
            if (shouldRefreshProjection)
            {
                RefreshMessagesFromTranscript(ShouldRefreshContextWindowAfterRuntimeEvent(runtimeEvent));
            }

            if (runtimeEvent.ContextCompression != null)
            {
                ApplyRuntimeContextWindowStatus(runtimeEvent.ContextCompression);
            }

            if (runtimeEvent.TokenUsage != null)
            {
                ApplyRuntimeTokenUsageStatus(runtimeEvent.TokenUsage);
            }
        }

        private void AddVideo()
        {
            AddMediaFiles(
                L("ChatSessionControl.Dialog.VideoFilter", "Video Files|*.mp4;*.mpeg;*.mpg;*.mov;*.avi;*.webm;*.mkv|All Files|*.*"),
                L("ChatSessionControl.Status.VideoUnavailable", "视频不可用"),
                L("ChatSessionControl.Status.AddVideoFailed", "添加视频失败"),
                "ChatSessionControl.Error.AddVideoFailedFormat",
                "无法添加视频文件 {0}: {1}");
        }

        private void AddDocument()
        {
            AddMediaFiles(
                L("ChatSessionControl.Dialog.FileFilter", "Files|*.pdf;*.txt;*.md;*.csv;*.html;*.htm;*.json;*.xml;*.xaml;*.cs;*.csproj;*.sln;*.props;*.targets;*.js;*.jsx;*.ts;*.tsx;*.css;*.scss;*.less;*.ps1;*.psm1;*.psd1;*.bat;*.cmd;*.sh;*.py;*.java;*.cpp;*.cc;*.cxx;*.c;*.h;*.hpp;*.sql;*.ini;*.cfg;*.conf;*.log;*.yaml;*.yml;*.toml;*.smd;*.doc;*.docx;*.ppt;*.pptx;*.xls;*.xlsx|All Files|*.*"),
                L("ChatSessionControl.Status.FileUnavailable", "文件不可用"),
                L("ChatSessionControl.Status.AddFileFailed", "添加文件失败"),
                "ChatSessionControl.Error.AddFileFailedFormat",
                "无法添加文件 {0}: {1}");
        }

        private void AddMediaFiles(
            string filter,
            string unavailableTitle,
            string failureTitle,
            string failureFormatKey,
            string failureFallbackFormat)
        {
            if (_sessionModel == null)
            {
                AddSystemStatusMessage(
                    L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"),
                    unavailableTitle);
                return;
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter,
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
                    AddSystemStatusMessage(
                        LF(failureFormatKey, failureFallbackFormat, fileName, ex.Message),
                        failureTitle);
                }
            }
        }

        private static LanguageModelChatContentBlock CreateContentBlockForAttachment(ChatComposerAttachmentModel attachment)
        {
            if (attachment.IsText)
            {
                return LanguageModelChatContentBlock.CreateHostPreservedContent(
                    attachment.PreservedContentXml ??
                    FerritaPreservedTextContentXml.Build(
                        string.Empty,
                        attachment.DisplayName,
                        attachment.ResourcePath,
                        attachment.MediaType));
            }

            if (attachment.IsAudio)
            {
                return LanguageModelChatContentBlock.CreateAudio(attachment.ResourcePath, attachment.MediaType);
            }

            if (attachment.IsVideo)
            {
                return LanguageModelChatContentBlock.CreateVideo(attachment.ResourcePath, attachment.MediaType);
            }

            return attachment.IsDocument
                ? LanguageModelChatContentBlock.CreateDocument(attachment.ResourcePath, attachment.MediaType)
                : LanguageModelChatContentBlock.CreateImage(attachment.ResourcePath, attachment.MediaType);
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
                PromptText = LF("ChatSessionControl.ToolConfirmation.PromptFormat", "代理 {0} 请求执行下面的工具调用。确认后才会继续执行。", request.Agent.DisplayNameOrFallback),
                MetadataText = LF("ChatSessionControl.ToolConfirmation.MetadataFormat", "工具：{0}    迭代：{1}    调用序号：{2}", request.Invocation.ToolName, request.IterationNumber, toolCallIndex),
                InvocationXml = request.Invocation.InvocationXml,
                InvocationPreview = previewHandle.View
            })
            {
                Owner = Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true
                ? AgentToolConfirmationResult.Approve()
                : AgentToolConfirmationResult.Reject(L("ChatSessionControl.ToolConfirmation.Rejected", "用户拒绝了此工具调用。"));
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
                errorMessage ?? L("ChatSessionControl.Error.WriteClipboardFailed", "无法写入系统剪贴板。"),
                L("ChatSessionControl.Status.CopyFailed", "复制失败"),
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
                L("ChatSessionControl.Edit.NotImplemented", "消息编辑功能尚未实现，这里先保留入口位。"),
                L("ChatSessionControl.ContextMenu.Edit", "编辑"),
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
                ChatSessionTranscriptEntry[] removedEntries;
                lock (_sessionModel.Transcript.SyncRoot)
                {
                    removedEntries = _sessionModel.Transcript.Entries
                        .Where(candidate => sourceEntryIds.Contains(candidate.EntryId))
                        .ToArray();
                }

                if (removedEntries.Length > 0)
                {
                    lock (_sessionModel.Transcript.SyncRoot)
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
                    }

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
                lock (_sessionModel.Transcript.SyncRoot)
                {
                    _sessionModel.Transcript.Turns.Clear();
                    _sessionModel.Transcript.Entries.Clear();
                    _sessionModel.Transcript.Touch();
                }

                PersistSession();
            }

            RefreshMessagesFromTranscript();
        }

        private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(MessageCountText));
            CommandManager.InvalidateRequerySuggested();
        }

        private void QueueInitialSessionDiagnostics()
        {
            if (_sessionModel == null)
            {
                return;
            }

            _ = RefreshInitialSessionDiagnosticsAsync(_sessionModel);
        }

        private async Task RefreshInitialSessionDiagnosticsAsync(ChatSessionModel sessionModel)
        {
            try
            {
                var diagnostics = await Task.Run(() =>
                {
                    var compilationResult = _flowBindingService.CompileBinding(sessionModel.FlowBinding);
                    var validationSummary = BuildSessionFlowValidationSummary(sessionModel, compilationResult);
                    var snapshot = _contextWindowService.CreateSnapshot(sessionModel, compilationResult);
                    return new InitialSessionDiagnostics(validationSummary, snapshot);
                }).ConfigureAwait(false);

                await ApplyOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(_sessionModel, sessionModel))
                    {
                        return;
                    }

                    SessionFlowValidationSummary = diagnostics.ValidationSummary;
                    ApplyContextWindowSnapshot(diagnostics.ContextWindowSnapshot);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ApplyOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(_sessionModel, sessionModel))
                    {
                        return;
                    }

                    SessionFlowValidationSummary = ex.Message;
                    ApplyUnavailableContextWindowStatus(ex.Message);
                }).ConfigureAwait(false);
            }
        }

        private static Task ApplyOnUiThreadAsync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private void RefreshMessagesFromTranscript(bool refreshContextWindow = true)
        {
            // 确保在 UI 线程上执行，防止 CollectionView 跨线程修改异常
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => RefreshMessagesFromTranscript(refreshContextWindow)), System.Windows.Threading.DispatcherPriority.Normal);
                return;
            }

            _RefreshMessagesFromTranscript(refreshContextWindow);
        }

        private void _RefreshMessagesFromTranscript(bool refreshContextWindow = true)
        {
            if (_sessionModel == null)
            {
                ApplyUnavailableContextWindowStatus(L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"));
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
            _suppressPersistence = true;
            try
            {
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

                SelectedMessage = Messages.LastOrDefault();
            }
            finally
            {
                _suppressPersistence = false;
            }

            if (refreshContextWindow)
            {
                RefreshContextWindowStatusFromBackend();
            }
        }

        private void RefreshContextWindowStatusFromBackend()
        {
            if (_sessionModel == null)
            {
                ApplyUnavailableContextWindowStatus(L("ChatSessionControl.Error.UnboundModel", "此聊天视图未绑定到 ChatSessionModel。"));
                return;
            }

            try
            {
                ApplyContextWindowSnapshot(_contextWindowService.CreateSnapshot(_sessionModel));
            }
            catch (Exception ex)
            {
                ApplyUnavailableContextWindowStatus(ex.Message);
            }
        }

        private void ApplyContextWindowSnapshot(ChatSessionContextWindowSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (!snapshot.IsAvailable)
            {
                ApplyUnavailableContextWindowStatus(snapshot.Message ?? L("ChatSessionControl.ContextWindow.Unavailable", "上下文窗口暂不可用。"));
                return;
            }

            var detail = LF(
                "ChatSessionControl.ContextWindow.EstimatedFormat",
                "估算 {0} / {1} Tokens ({2})",
                FormatTokens(snapshot.EstimatedTokenCount),
                FormatTokens(snapshot.ContextWindowTokens),
                FormatPercent(snapshot.UsageRatio));
            var bottleneck = BuildContextWindowBottleneckText(snapshot.AgentName, snapshot.ModelName);
            if (bottleneck.Length > 0)
            {
                detail += Environment.NewLine + bottleneck;
            }

            ApplyContextWindowStatus(
                snapshot.EstimatedTokenCount,
                snapshot.ContextWindowTokens,
                snapshot.UsageRatio,
                detail);
        }

        private void ApplyRuntimeContextWindowStatus(AgentLoopContextCompressionInfo contextCompression)
        {
            ArgumentNullException.ThrowIfNull(contextCompression);

            if (contextCompression.ContextWindowTokens <= 0)
            {
                return;
            }

            var usedTokens = contextCompression.EstimatedTokenCountAfterCompression > 0
                ? contextCompression.EstimatedTokenCountAfterCompression
                : contextCompression.EstimatedTokenCountBeforeCompression;
            var ratio = usedTokens / (double)contextCompression.ContextWindowTokens;
            var detail = LF(
                "ChatSessionControl.ContextWindow.RuntimeFormat",
                "运行时 {0} / {1} Tokens ({2})",
                FormatTokens(usedTokens),
                FormatTokens(contextCompression.ContextWindowTokens),
                FormatPercent(ratio));
            if (contextCompression.EstimatedTokenCountBeforeCompression > 0 &&
                contextCompression.EstimatedTokenCountAfterCompression > 0)
            {
                detail += Environment.NewLine +
                          LF("ChatSessionControl.ContextWindow.BeforeCompressionFormat", "压缩前 {0} Tokens", FormatTokens(contextCompression.EstimatedTokenCountBeforeCompression));
            }

            ApplyContextWindowStatus(
                usedTokens,
                contextCompression.ContextWindowTokens,
                ratio,
                detail);
        }

        private void ApplyRuntimeTokenUsageStatus(AgentLoopTokenUsageInfo tokenUsage)
        {
            ArgumentNullException.ThrowIfNull(tokenUsage);

            if (tokenUsage.ContextWindowTokens <= 0)
            {
                return;
            }

            var totalTokens = tokenUsage.EstimatedTotalTokenCount;
            var ratio = totalTokens / (double)tokenUsage.ContextWindowTokens;
            var detail = LF(
                "ChatSessionControl.ContextWindow.StreamingEstimateFormat",
                "Streaming estimate {0} / {1} Tokens ({2})",
                FormatTokens(totalTokens),
                FormatTokens(tokenUsage.ContextWindowTokens),
                FormatPercent(ratio));
            detail += Environment.NewLine +
                      LF(
                          "ChatSessionControl.ContextWindow.InputOutputFormat",
                          "Input {0} / Output {1} Tokens",
                          FormatTokens(tokenUsage.EstimatedInputTokenCount),
                          FormatTokens(tokenUsage.EstimatedOutputTokenCount));
            if (!string.IsNullOrWhiteSpace(tokenUsage.ModelId))
            {
                detail += Environment.NewLine + LF("ChatSessionControl.ContextWindow.ModelFormat", "Model: {0}", tokenUsage.ModelId);
            }

            ApplyContextWindowStatus(
                totalTokens,
                tokenUsage.ContextWindowTokens,
                ratio,
                detail);
        }

        private void ApplyContextWindowStatus(
            int usedTokens,
            int contextWindowTokens,
            double usageRatio,
            string usageText)
        {
            var boundedRatio = Math.Clamp(usageRatio, 0d, 1d);
            ContextWindowUsageRatio = boundedRatio;
            ContextWindowUsageText = string.IsNullOrWhiteSpace(usageText)
                ? LF(
                    "ChatSessionControl.ContextWindow.DefaultUsageFormat",
                    "{0} / {1} Tokens ({2})",
                    FormatTokens(usedTokens),
                    FormatTokens(contextWindowTokens),
                    FormatPercent(boundedRatio))
                : usageText;
            ContextWindowStatusText = LF("ChatSessionControl.ContextWindow.StatusFormat", "上下文 {0}", FormatPercent(boundedRatio));
        }

        private void ApplyUnavailableContextWindowStatus(string message)
        {
            ContextWindowUsageRatio = 0d;
            ContextWindowUsageText = string.IsNullOrWhiteSpace(message)
                ? L("ChatSessionControl.ContextWindow.Unavailable", "上下文窗口暂不可用。")
                : message.Trim();
            ContextWindowStatusText = L("ChatSessionControl.ContextWindow.StatusUnavailable", "上下文 --");
        }

        private static string BuildContextWindowBottleneckText(string? agentName, string? modelName)
        {
            var normalizedAgentName = agentName?.Trim() ?? string.Empty;
            var normalizedModelName = modelName?.Trim() ?? string.Empty;
            if (normalizedAgentName.Length == 0 && normalizedModelName.Length == 0)
            {
                return string.Empty;
            }

            if (normalizedAgentName.Length == 0)
            {
                return LF("ChatSessionControl.ContextWindow.ModelBottleneckFormat", "模型：{0}", normalizedModelName);
            }

            if (normalizedModelName.Length == 0)
            {
                return LF("ChatSessionControl.ContextWindow.AgentBottleneckFormat", "瓶颈代理：{0}", normalizedAgentName);
            }

            return LF("ChatSessionControl.ContextWindow.AgentModelBottleneckFormat", "瓶颈代理：{0} / {1}", normalizedAgentName, normalizedModelName);
        }

        private static string FormatTokens(int tokens)
        {
            return Math.Max(0, tokens).ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string FormatPercent(double ratio)
        {
            return Math.Clamp(ratio, 0d, 1d).ToString("P0", CultureInfo.CurrentCulture);
        }

        private static bool ShouldRefreshContextWindowAfterRuntimeEvent(ChatSessionRuntimeEvent runtimeEvent)
        {
            return runtimeEvent.Kind is ChatSessionRuntimeEventKind.ExecutionStarted
                or ChatSessionRuntimeEventKind.ContextCompressionApplied
                or ChatSessionRuntimeEventKind.ExecutionCompleted
                or ChatSessionRuntimeEventKind.ExecutionFailed
                or ChatSessionRuntimeEventKind.ExecutionCancelled;
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

        private void PersistSession()
        {
            if (_suppressPersistence || _sessionModel == null || _chatSessionRepository == null)
            {
                return;
            }

            _chatSessionRepository.Save(_sessionModel);
        }

        public void SaveSessionSnapshot()
        {
            if (_sessionModel == null)
            {
                return;
            }

            try
            {
                _chatSessionRepository?.Save(_sessionModel);
            }
            catch
            {
                // Memory generation can still use the in-memory session if final persistence is unavailable.
            }
        }

        public void QueueMemoryForClosedSession()
        {
            _ = GenerateMemoryForClosedSessionAsync();
        }

        public Task GenerateMemoryForClosedSessionAsync()
        {
            return _sessionModel == null
                ? Task.CompletedTask
                : MemoryService.GenerateForClosedSessionAsync(_sessionModel);
        }

        public void OnWorkspaceDocumentClosed()
        {
            SaveSessionSnapshot();
            QueueMemoryForClosedSession();
        }

        private void AddSystemStatusMessage(string content, string title)
        {
            // 确保在 UI 线程上执行，防止 CollectionView 跨线程修改异常
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => AddSystemStatusMessage(content, title)), System.Windows.Threading.DispatcherPriority.Normal);
                return;
            }

            _AddSystemStatusMessage(content, title);
        }

        private void _AddSystemStatusMessage(string content, string title)
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
                ChatMessageRole.Assistant => L("ChatSessionControl.DisplayName.Assistant", "Ferrita 助手"),
                ChatMessageRole.System => L("ChatSessionControl.DisplayName.System", "系统"),
                _ => L("ChatSessionControl.DisplayName.User", "用户")
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

        private static string BuildInitialSessionFlowValidationSummary(ChatSessionModel? sessionModel)
        {
            return sessionModel?.HasBoundFlow == true
                ? L("ChatSessionControl.BoundFlow.ValidationPending", "Session flow validation is pending.")
                : L("ChatSessionControl.BoundFlow.UnboundSummary", "No session flow is bound.");
        }

        private static string BuildSessionFlowValidationSummary(
            ChatSessionModel? sessionModel,
            SessionFlowCompilationResult compilationResult)
        {
            ArgumentNullException.ThrowIfNull(compilationResult);

            if (sessionModel?.HasBoundFlow != true)
            {
                return L("ChatSessionControl.BoundFlow.UnboundSummary", "No session flow is bound.");
            }

            if (compilationResult.IsSuccess)
            {
                return L("ChatSessionControl.BoundFlow.ValidationSucceeded", "The bound session flow passed runtime validation.");
            }

            return compilationResult.Errors.FirstOrDefault()?.Message
                ?? compilationResult.Issues.FirstOrDefault()?.Message
                ?? L("ChatSessionControl.BoundFlow.ValidationFailed", "The bound session flow did not pass runtime validation.");
        }

        private string BuildSessionFlowValidationSummary(ChatSessionModel? sessionModel)
        {
            if (sessionModel?.HasBoundFlow != true)
            {
                return L("ChatSessionControl.BoundFlow.UnboundSummary", "未绑定会话流。");
            }

            var compilationResult = _flowBindingService.CompileBinding(sessionModel.FlowBinding);
            if (compilationResult.IsSuccess)
            {
                return L("ChatSessionControl.BoundFlow.ValidationSucceeded", "绑定的会话流已通过运行时验证。");
            }

            return compilationResult.Errors.FirstOrDefault()?.Message
                ?? compilationResult.Issues.FirstOrDefault()?.Message
                ?? L("ChatSessionControl.BoundFlow.ValidationFailed", "绑定的会话流未通过运行时验证。");
        }

        private sealed record InitialSessionDiagnostics(
            string ValidationSummary,
            ChatSessionContextWindowSnapshot ContextWindowSnapshot);

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static string LF(string resourceKey, string fallbackFormat, params object?[] args)
        {
            var format = L(resourceKey, fallbackFormat);
            return string.Format(format, args);
        }
    }
}
