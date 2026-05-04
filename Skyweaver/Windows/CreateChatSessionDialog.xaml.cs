using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Models;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Services;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.ChatSession;

namespace Skyweaver.Windows
{
    public partial class CreateChatSessionDialog : Window
    {
        private readonly CreateChatSessionDialogViewModel _viewModel;

        public string SessionName => _viewModel.SessionName.Trim();

        public ChatSessionFlowBinding? SelectedFlowBinding => _viewModel.SelectedFlowBinding?.ToBinding();

        public CreateChatSessionDialog(
            IReadOnlyList<ChatSessionFlowBindingOption>? sessionFlowOptions = null,
            string? initialSessionName = null)
        {
            _viewModel = new CreateChatSessionDialogViewModel(sessionFlowOptions, initialSessionName);
            InitializeComponent();
            DataContext = _viewModel;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SessionNameTextBox.Focus();
            SessionNameTextBox.SelectAll();
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SessionName))
            {
                MessageBox.Show(this, "请输入会话名称。", "创建会话", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedFlowBinding == null)
            {
                MessageBox.Show(this, "请先为新会话选择一个会话流。", "创建会话", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }
    }

    internal sealed class CreateChatSessionDialogViewModel : ObservableObject
    {
        private static readonly Brush InfoBackgroundBrush = CreateBrush(Color.FromArgb(0x2A, 0x68, 0x94, 0xD6));
        private static readonly Brush InfoBorderBrush = CreateBrush(Color.FromArgb(0x66, 0x9C, 0xC8, 0xFF));
        private static readonly Brush InfoForegroundBrush = CreateBrush(Color.FromArgb(0xFF, 0xE8, 0xF4, 0xFF));
        private static readonly Brush WarningBackgroundBrush = CreateBrush(Color.FromArgb(0x2E, 0xB7, 0x8A, 0x2F));
        private static readonly Brush WarningBorderBrush = CreateBrush(Color.FromArgb(0x66, 0xD8, 0xB4, 0x4E));
        private static readonly Brush WarningForegroundBrush = CreateBrush(Color.FromArgb(0xFF, 0xFF, 0xF1, 0xCC));
        private static readonly Brush ErrorBackgroundBrush = CreateBrush(Color.FromArgb(0x32, 0xB8, 0x49, 0x49));
        private static readonly Brush ErrorBorderBrush = CreateBrush(Color.FromArgb(0x66, 0xE2, 0x57, 0x57));
        private static readonly Brush ErrorForegroundBrush = CreateBrush(Color.FromArgb(0xFF, 0xFF, 0xE2, 0xE2));

        private readonly SessionFlowRepository _sessionFlowRepository;
        private readonly SessionFlowRuntimeCompiler _runtimeCompiler;
        private readonly Dictionary<string, AgentDefinition> _agentsById;
        private readonly Dictionary<string, LanguageModelDefinition> _languageModelsByKey;
        private readonly Dictionary<string, CapabilityLayerDefinition> _capabilityLayersByKey;
        private readonly List<string> _globalStatusMessages = new();

        private string _sessionName;
        private string _flowDisplayName = "未选择会话流";
        private string _flowSubtitle = "请选择一个会话流以预览将要创建的 ChatSession。";
        private string _flowStatusText = "尚未选择会话流";
        private string _agentCountText = "0";
        private string _modelCountText = "0";
        private string _nodeCountText = "0";
        private string _connectionCountText = "0";
        private bool _hasRecentSessions;
        private bool _isHistoryEmpty = true;
        private bool _hasAgentPreviews;
        private bool _isAgentPreviewEmpty = true;
        private bool _hasModelPreviews;
        private bool _isModelPreviewEmpty = true;
        private CreateChatSessionFlowOptionViewModel? _selectedFlowOption;
        private CreateChatSessionHistoryItemViewModel? _selectedHistorySession;

        public CreateChatSessionDialogViewModel(
            IReadOnlyList<ChatSessionFlowBindingOption>? sessionFlowOptions,
            string? initialSessionName)
        {
            _sessionName = string.IsNullOrWhiteSpace(initialSessionName) ? "新建会话" : initialSessionName.Trim();
            SessionFlowOptions = new ObservableCollection<CreateChatSessionFlowOptionViewModel>();
            RecentSessions = new ObservableCollection<CreateChatSessionHistoryItemViewModel>();
            AgentPreviews = new ObservableCollection<CreateChatSessionAgentPreviewItemViewModel>();
            ModelPreviews = new ObservableCollection<CreateChatSessionModelPreviewItemViewModel>();
            FlowStatusItems = new ObservableCollection<CreateChatSessionIssueItemViewModel>();

            _sessionFlowRepository = new SessionFlowRepository(new SessionFlowPathProvider());
            _runtimeCompiler = new SessionFlowRuntimeCompiler();

            var languageModelPathProvider = new LanguageModelConfigurationPathProvider();
            _agentsById = LoadAgents();
            _languageModelsByKey = LoadLanguageModels(languageModelPathProvider);
            _capabilityLayersByKey = LoadCapabilityLayers(languageModelPathProvider);

            LoadSessionFlowOptions(sessionFlowOptions);
            LoadRecentSessions();
            RefreshFlowPreview();
        }

        public string SessionName
        {
            get => _sessionName;
            set
            {
                if (SetProperty(ref _sessionName, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SessionNamePreview));
                    OnPropertyChanged(nameof(CanCreate));
                }
            }
        }

        public string SessionNamePreview
        {
            get
            {
                var trimmed = SessionName.Trim();
                return trimmed.Length == 0 ? "未命名会话" : trimmed;
            }
        }

        public ObservableCollection<CreateChatSessionFlowOptionViewModel> SessionFlowOptions { get; }

        public CreateChatSessionFlowOptionViewModel? SelectedFlowOption
        {
            get => _selectedFlowOption;
            set
            {
                if (SetProperty(ref _selectedFlowOption, value))
                {
                    RefreshFlowPreview();
                    OnPropertyChanged(nameof(CanCreate));
                }
            }
        }

        public ChatSessionFlowBindingOption? SelectedFlowBinding => SelectedFlowOption?.BindingOption;

        public ObservableCollection<CreateChatSessionHistoryItemViewModel> RecentSessions { get; }

        public CreateChatSessionHistoryItemViewModel? SelectedHistorySession
        {
            get => _selectedHistorySession;
            set => SetProperty(ref _selectedHistorySession, value);
        }

        public bool HasRecentSessions
        {
            get => _hasRecentSessions;
            private set => SetProperty(ref _hasRecentSessions, value);
        }

        public bool IsHistoryEmpty
        {
            get => _isHistoryEmpty;
            private set => SetProperty(ref _isHistoryEmpty, value);
        }

        public string FlowDisplayName
        {
            get => _flowDisplayName;
            private set => SetProperty(ref _flowDisplayName, value);
        }

        public string FlowSubtitle
        {
            get => _flowSubtitle;
            private set => SetProperty(ref _flowSubtitle, value);
        }

        public string FlowStatusText
        {
            get => _flowStatusText;
            private set => SetProperty(ref _flowStatusText, value);
        }

        public string AgentCountText
        {
            get => _agentCountText;
            private set => SetProperty(ref _agentCountText, value);
        }

        public string ModelCountText
        {
            get => _modelCountText;
            private set => SetProperty(ref _modelCountText, value);
        }

        public string NodeCountText
        {
            get => _nodeCountText;
            private set => SetProperty(ref _nodeCountText, value);
        }

        public string ConnectionCountText
        {
            get => _connectionCountText;
            private set => SetProperty(ref _connectionCountText, value);
        }

        public ObservableCollection<CreateChatSessionAgentPreviewItemViewModel> AgentPreviews { get; }

        public bool HasAgentPreviews
        {
            get => _hasAgentPreviews;
            private set => SetProperty(ref _hasAgentPreviews, value);
        }

        public bool IsAgentPreviewEmpty
        {
            get => _isAgentPreviewEmpty;
            private set => SetProperty(ref _isAgentPreviewEmpty, value);
        }

        public ObservableCollection<CreateChatSessionModelPreviewItemViewModel> ModelPreviews { get; }

        public bool HasModelPreviews
        {
            get => _hasModelPreviews;
            private set => SetProperty(ref _hasModelPreviews, value);
        }

        public bool IsModelPreviewEmpty
        {
            get => _isModelPreviewEmpty;
            private set => SetProperty(ref _isModelPreviewEmpty, value);
        }

        public ObservableCollection<CreateChatSessionIssueItemViewModel> FlowStatusItems { get; }

        public bool CanCreate => !string.IsNullOrWhiteSpace(SessionName) && SelectedFlowBinding != null;

        private void LoadSessionFlowOptions(IReadOnlyList<ChatSessionFlowBindingOption>? sessionFlowOptions)
        {
            SessionFlowOptions.Clear();

            foreach (var option in sessionFlowOptions ?? Array.Empty<ChatSessionFlowBindingOption>())
            {
                SessionFlowOptions.Add(new CreateChatSessionFlowOptionViewModel(option));
            }

            if (SessionFlowOptions.Count > 0)
            {
                _selectedFlowOption = SessionFlowOptions[0];
                OnPropertyChanged(nameof(SelectedFlowOption));
            }
        }

        private void LoadRecentSessions()
        {
            RecentSessions.Clear();

            try
            {
                var repository = new ChatSessionRepository();
                foreach (var session in repository.LoadAll().Take(8))
                {
                    RecentSessions.Add(new CreateChatSessionHistoryItemViewModel
                    {
                        Title = session.Name,
                        FlowName = session.HasBoundFlow ? session.BoundFlowDisplayName : "未绑定会话流",
                        UpdatedAtText = FormatTimestamp(session.UpdatedAtUtc),
                        IconPath = string.IsNullOrWhiteSpace(session.IconPath)
                            ? "pack://application:,,,/Resources/NewNodeGraphAlt.png"
                            : session.IconPath
                    });
                }
            }
            catch (Exception ex)
            {
                _globalStatusMessages.Add($"历史会话列表加载失败：{ex.Message}");
            }

            HasRecentSessions = RecentSessions.Count > 0;
            IsHistoryEmpty = !HasRecentSessions;
        }

        private void RefreshFlowPreview()
        {
            AgentPreviews.Clear();
            ModelPreviews.Clear();
            FlowStatusItems.Clear();

            if (SelectedFlowOption == null)
            {
                FlowDisplayName = "未选择会话流";
                FlowSubtitle = "请选择一个会话流以预览将要创建的 ChatSession。";
                FlowStatusText = "尚未选择会话流";
                AgentCountText = "0";
                ModelCountText = "0";
                NodeCountText = "0";
                ConnectionCountText = "0";
                ApplyEmptyFlags();
                AppendGlobalStatusItems();
                if (FlowStatusItems.Count == 0)
                {
                    FlowStatusItems.Add(CreateInfoIssue("等待选择", "选择会话流后，这里会展示代理、模型和流状态。"));
                }

                return;
            }

            SessionFlowGraphDocumentModel? document = null;
            SessionFlowCompilationResult? compilationResult = null;
            string? loadError = null;

            try
            {
                document = _sessionFlowRepository.Load(SelectedFlowOption.BindingOption.FilePath);
                compilationResult = _runtimeCompiler.Compile(document);
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
            }

            if (document == null)
            {
                FlowDisplayName = SelectedFlowOption.DisplayName;
                FlowSubtitle = "无法读取当前会话流文件。";
                FlowStatusText = "读取失败";
                AgentCountText = "0";
                ModelCountText = "0";
                NodeCountText = "0";
                ConnectionCountText = "0";
                ApplyEmptyFlags();
                AppendGlobalStatusItems();
                FlowStatusItems.Add(CreateErrorIssue("无法读取", loadError ?? "未能读取当前会话流。"));
                return;
            }

            var graph = document.Graph ?? new SessionFlowGraphModel();
            var agentGroups = graph.Nodes
                .Where(node => node.Kind == SessionFlowNodeKind.Agent)
                .GroupBy(GetAgentGroupingKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var modelUsageMap = new Dictionary<string, CreateChatSessionModelUsageAccumulator>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in agentGroups)
            {
                AgentPreviews.Add(BuildAgentPreview(group.ToArray(), modelUsageMap));
            }

            foreach (var preview in modelUsageMap.Values
                         .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(item => item.ModelIdText, StringComparer.CurrentCultureIgnoreCase)
                         .Select(item => item.ToViewModel()))
            {
                ModelPreviews.Add(preview);
            }

            FlowDisplayName = string.IsNullOrWhiteSpace(document.Name) ? SelectedFlowOption.DisplayName : document.Name;
            FlowSubtitle = $"文件：{Path.GetFileName(document.FilePath)} · 更新于 {FormatTimestamp(document.UpdatedAtUtc)}";
            FlowStatusText = BuildFlowStatusText(compilationResult);
            AgentCountText = AgentPreviews.Count.ToString(CultureInfo.InvariantCulture);
            ModelCountText = ModelPreviews.Count.ToString(CultureInfo.InvariantCulture);
            NodeCountText = graph.Nodes.Count.ToString(CultureInfo.InvariantCulture);
            ConnectionCountText = graph.Connections.Count.ToString(CultureInfo.InvariantCulture);

            AppendGlobalStatusItems();

            if (compilationResult == null)
            {
                FlowStatusItems.Add(CreateErrorIssue("读取失败", "未能为当前会话流生成状态信息。"));
            }
            else if (compilationResult.Issues.Count == 0)
            {
                FlowStatusItems.Add(CreateInfoIssue("已就绪", "当前会话流编译通过，可以直接用于创建新的 ChatSession。"));
            }
            else
            {
                var nodeMap = graph.Nodes
                    .Where(node => !string.IsNullOrWhiteSpace(node.Id))
                    .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

                foreach (var issue in compilationResult.Issues)
                {
                    FlowStatusItems.Add(CreateIssue(issue, nodeMap));
                }
            }

            ApplyEmptyFlags();
        }

        private void ApplyEmptyFlags()
        {
            HasAgentPreviews = AgentPreviews.Count > 0;
            IsAgentPreviewEmpty = !HasAgentPreviews;
            HasModelPreviews = ModelPreviews.Count > 0;
            IsModelPreviewEmpty = !HasModelPreviews;
        }

        private void AppendGlobalStatusItems()
        {
            foreach (var message in _globalStatusMessages)
            {
                FlowStatusItems.Add(CreateWarningIssue("附加提示", message));
            }
        }

        private CreateChatSessionAgentPreviewItemViewModel BuildAgentPreview(
            IReadOnlyList<SessionFlowNodeModel> nodes,
            IDictionary<string, CreateChatSessionModelUsageAccumulator> modelUsageMap)
        {
            var firstNode = nodes[0];
            var agentId = firstNode.AgentId?.Trim() ?? string.Empty;
            var nodeSummary = BuildNodeSummary(nodes);

            if (agentId.Length == 0)
            {
                return CreateMissingAgentPreview(
                    string.IsNullOrWhiteSpace(firstNode.AgentDisplayName) ? firstNode.Title : firstNode.AgentDisplayName,
                    nodeSummary,
                    "代理节点尚未绑定 AgentDefinition。");
            }

            if (!_agentsById.TryGetValue(agentId, out var agent))
            {
                return CreateMissingAgentPreview(
                    string.IsNullOrWhiteSpace(firstNode.AgentDisplayName) ? firstNode.Title : firstNode.AgentDisplayName,
                    nodeSummary,
                    $"未找到 AgentDefinition：{agentId}",
                    agentId);
            }

            var candidates = ResolveCandidateModels(agent);
            foreach (var candidate in candidates)
            {
                var usage = GetOrCreateModelUsage(modelUsageMap, candidate, agent.LanguageModelSelectionMode);
                usage.AgentNames.Add(agent.DisplayNameOrFallback);
            }

            var status = ResolveAgentStatus(agent, candidates);
            return new CreateChatSessionAgentPreviewItemViewModel
            {
                AvatarPath = string.IsNullOrWhiteSpace(agent.AvatarPreviewPath) ? AgentDefinition.DefaultAvatarPath : agent.AvatarPreviewPath,
                DisplayName = agent.DisplayNameOrFallback,
                AgentIdText = $"ID: {agent.AgentIdOrFallback}",
                NodeSummary = nodeSummary,
                ModeText = agent.IsStructuredXmlIO ? "结构化 XML" : "自然语言",
                SelectionModeText = agent.LanguageModelSelectionMode == AgentLanguageModelSelectionMode.CapabilityLayer ? "功能层级" : "具体模型",
                ModelSummary = BuildAgentModelSummary(agent, candidates),
                DescriptionText = BuildAgentDescription(agent),
                HasStatusBadge = status != null,
                StatusBadgeText = status?.Text ?? string.Empty,
                StatusBadgeBackground = status?.Background ?? Brushes.Transparent,
                StatusBadgeBorderBrush = status?.BorderBrush ?? Brushes.Transparent,
                StatusBadgeForeground = status?.Foreground ?? Brushes.White
            };
        }

        private CreateChatSessionAgentPreviewItemViewModel CreateMissingAgentPreview(
            string? displayName,
            string nodeSummary,
            string message,
            string? agentId = null)
        {
            return new CreateChatSessionAgentPreviewItemViewModel
            {
                AvatarPath = AgentDefinition.DefaultAvatarPath,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "未绑定代理" : displayName.Trim(),
                AgentIdText = string.IsNullOrWhiteSpace(agentId) ? "ID: 未绑定" : $"ID: {agentId.Trim()}",
                NodeSummary = nodeSummary,
                ModeText = "未知模式",
                SelectionModeText = "未绑定",
                ModelSummary = "当前无法解析语言模型。",
                DescriptionText = message,
                HasStatusBadge = true,
                StatusBadgeText = "未绑定",
                StatusBadgeBackground = ErrorBackgroundBrush,
                StatusBadgeBorderBrush = ErrorBorderBrush,
                StatusBadgeForeground = ErrorForegroundBrush
            };
        }

        private IReadOnlyList<LanguageModelDefinition> ResolveCandidateModels(AgentDefinition agent)
        {
            if (agent.LanguageModelSelectionMode == AgentLanguageModelSelectionMode.CapabilityLayer)
            {
                if (string.IsNullOrWhiteSpace(agent.SelectedCapabilityLayerKey) ||
                    !_capabilityLayersByKey.TryGetValue(agent.SelectedCapabilityLayerKey, out var layer))
                {
                    return Array.Empty<LanguageModelDefinition>();
                }

                return layer.LanguageModels
                    .Select(entry => entry.LanguageModelKey?.Trim() ?? string.Empty)
                    .Where(key => key.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(key => _languageModelsByKey.TryGetValue(key, out var model) ? model : null)
                    .Where(model => model != null)
                    .Cast<LanguageModelDefinition>()
                    .ToArray();
            }

            if (string.IsNullOrWhiteSpace(agent.SelectedLanguageModelKey) ||
                !_languageModelsByKey.TryGetValue(agent.SelectedLanguageModelKey, out var selectedModel))
            {
                return Array.Empty<LanguageModelDefinition>();
            }

            return [selectedModel];
        }

        private CreateChatSessionAgentStatus? ResolveAgentStatus(
            AgentDefinition agent,
            IReadOnlyList<LanguageModelDefinition> candidates)
        {
            if (agent.LanguageModelSelectionMode == AgentLanguageModelSelectionMode.CapabilityLayer)
            {
                if (string.IsNullOrWhiteSpace(agent.SelectedCapabilityLayerKey))
                {
                    return new CreateChatSessionAgentStatus("模型缺失", ErrorBackgroundBrush, ErrorBorderBrush, ErrorForegroundBrush);
                }

                if (!_capabilityLayersByKey.ContainsKey(agent.SelectedCapabilityLayerKey) || candidates.Count == 0)
                {
                    return new CreateChatSessionAgentStatus("层级为空", WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
                }

                if (candidates.Any(model => !model.IsFullyConfigured))
                {
                    return new CreateChatSessionAgentStatus("配置不完整", WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
                }

                return null;
            }

            if (string.IsNullOrWhiteSpace(agent.SelectedLanguageModelKey))
            {
                return new CreateChatSessionAgentStatus("模型缺失", ErrorBackgroundBrush, ErrorBorderBrush, ErrorForegroundBrush);
            }

            if (candidates.Count == 0 || candidates.Any(model => !model.IsFullyConfigured))
            {
                return new CreateChatSessionAgentStatus("配置不完整", WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
            }

            return null;
        }

        private string BuildAgentModelSummary(AgentDefinition agent, IReadOnlyList<LanguageModelDefinition> candidates)
        {
            if (agent.LanguageModelSelectionMode == AgentLanguageModelSelectionMode.CapabilityLayer)
            {
                var layerName = GetCapabilityLayerDisplayName(agent.SelectedCapabilityLayerKey);
                return candidates.Count == 0
                    ? $"功能层级：{layerName}（未解析到候选模型）"
                    : $"功能层级：{layerName}（候选 {candidates.Count} 个）";
            }

            if (string.IsNullOrWhiteSpace(agent.SelectedLanguageModelKey))
            {
                return "具体模型：尚未选择语言模型。";
            }

            if (candidates.Count == 0)
            {
                return $"具体模型：未找到 {agent.SelectedLanguageModelKey}。";
            }

            return $"具体模型：{GetLanguageModelDisplayName(candidates[0])}";
        }

        private static string BuildAgentDescription(AgentDefinition agent)
        {
            var segments = new List<string>();

            if (!string.IsNullOrWhiteSpace(agent.InputDescription))
            {
                segments.Add($"输入：{agent.InputDescription.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(agent.OutputDescription))
            {
                segments.Add($"输出：{agent.OutputDescription.Trim()}");
            }

            if (segments.Count > 0)
            {
                return string.Join(" · ", segments);
            }

            return agent.IsStructuredXmlIO
                ? "该代理使用结构化 XML 输入输出。"
                : "该代理使用自然语言输入输出。";
        }

        private static string BuildNodeSummary(IReadOnlyList<SessionFlowNodeModel> nodes)
        {
            var titles = nodes
                .Select(node =>
                {
                    var title = node.Title?.Trim() ?? string.Empty;
                    return title.Length == 0 ? "未命名节点" : title;
                })
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            if (titles.Length == 1)
            {
                return $"节点：{titles[0]}";
            }

            var previewTitles = titles.Take(3).ToArray();
            var suffix = titles.Length > previewTitles.Length ? "..." : string.Empty;
            return $"节点：{titles.Length} 个（{string.Join("、", previewTitles)}{suffix}）";
        }

        private static string GetAgentGroupingKey(SessionFlowNodeModel node)
        {
            var agentId = node.AgentId?.Trim() ?? string.Empty;
            return agentId.Length > 0 ? agentId : node.Id;
        }

        private CreateChatSessionIssueItemViewModel CreateIssue(
            SessionFlowCompilationIssue issue,
            IReadOnlyDictionary<string, SessionFlowNodeModel> nodeMap)
        {
            var message = issue.Message;
            if (!string.IsNullOrWhiteSpace(issue.NodeId) && nodeMap.TryGetValue(issue.NodeId, out var node))
            {
                var nodeTitle = node.Title?.Trim();
                if (!string.IsNullOrWhiteSpace(nodeTitle))
                {
                    message = $"{nodeTitle}：{message}";
                }
            }

            return issue.Severity == SessionFlowCompilationIssueSeverity.Error
                ? CreateErrorIssue("错误", message)
                : CreateWarningIssue("警告", message);
        }

        private static string BuildFlowStatusText(SessionFlowCompilationResult? compilationResult)
        {
            if (compilationResult == null)
            {
                return "无法分析会话流状态";
            }

            var errorCount = compilationResult.Issues.Count(issue => issue.Severity == SessionFlowCompilationIssueSeverity.Error);
            var warningCount = compilationResult.Issues.Count(issue => issue.Severity == SessionFlowCompilationIssueSeverity.Warning);

            if (errorCount == 0 && warningCount == 0)
            {
                return "当前会话流已就绪";
            }

            if (errorCount == 0)
            {
                return $"存在 {warningCount} 个警告";
            }

            if (warningCount == 0)
            {
                return $"存在 {errorCount} 个错误";
            }

            return $"存在 {errorCount} 个错误，{warningCount} 个警告";
        }

        private static string FormatTimestamp(DateTime utcDateTime)
        {
            var localTime = utcDateTime.Kind == DateTimeKind.Utc
                ? utcDateTime.ToLocalTime()
                : utcDateTime;

            return localTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        private Dictionary<string, AgentDefinition> LoadAgents()
        {
            try
            {
                return new AgentConfigurationRepository(new AgentConfigurationPathProvider())
                    .Load()
                    .Where(agent => !string.IsNullOrWhiteSpace(agent.AgentId))
                    .ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _globalStatusMessages.Add($"代理配置读取失败：{ex.Message}");
                return new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private Dictionary<string, LanguageModelDefinition> LoadLanguageModels(LanguageModelConfigurationPathProvider pathProvider)
        {
            try
            {
                return new LanguageModelConfigurationRepository(pathProvider)
                    .Load()
                    .Where(model => !string.IsNullOrWhiteSpace(model.Key))
                    .ToDictionary(model => model.Key, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _globalStatusMessages.Add($"语言模型配置读取失败：{ex.Message}");
                return new Dictionary<string, LanguageModelDefinition>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private Dictionary<string, CapabilityLayerDefinition> LoadCapabilityLayers(LanguageModelConfigurationPathProvider pathProvider)
        {
            try
            {
                return new CapabilityLayerConfigurationRepository(pathProvider)
                    .Load()
                    .Where(layer => !string.IsNullOrWhiteSpace(layer.Key))
                    .ToDictionary(layer => layer.Key, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _globalStatusMessages.Add($"功能层级配置读取失败：{ex.Message}");
                return new Dictionary<string, CapabilityLayerDefinition>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static CreateChatSessionModelUsageAccumulator GetOrCreateModelUsage(
            IDictionary<string, CreateChatSessionModelUsageAccumulator> usageMap,
            LanguageModelDefinition model,
            AgentLanguageModelSelectionMode selectionMode)
        {
            if (usageMap.TryGetValue(model.Key, out var existing))
            {
                existing.SourceTypes.Add(selectionMode == AgentLanguageModelSelectionMode.CapabilityLayer ? "功能层级" : "具体模型");
                return existing;
            }

            var created = new CreateChatSessionModelUsageAccumulator(model);
            created.SourceTypes.Add(selectionMode == AgentLanguageModelSelectionMode.CapabilityLayer ? "功能层级" : "具体模型");
            usageMap[model.Key] = created;
            return created;
        }

        private string GetCapabilityLayerDisplayName(string? capabilityLayerKey)
        {
            if (string.IsNullOrWhiteSpace(capabilityLayerKey))
            {
                return "未选择功能层级";
            }

            if (_capabilityLayersByKey.TryGetValue(capabilityLayerKey, out var layer))
            {
                return string.IsNullOrWhiteSpace(layer.Name)
                    ? $"未命名功能层级 ({TrimKey(capabilityLayerKey)})"
                    : layer.Name.Trim();
            }

            return capabilityLayerKey.Trim();
        }

        private static string GetLanguageModelDisplayName(LanguageModelDefinition model)
        {
            var displayName = model.DisplayName?.Trim() ?? string.Empty;
            if (displayName.Length > 0)
            {
                return displayName;
            }

            var summaryModelId = model.SummaryModelId?.Trim() ?? string.Empty;
            if (summaryModelId.Length > 0)
            {
                return $"未命名模型 ({summaryModelId})";
            }

            return $"未命名模型 ({TrimKey(model.Key)})";
        }

        private static string TrimKey(string? key)
        {
            var value = key?.Trim() ?? string.Empty;
            return value.Length <= 8 ? value : value[..8];
        }

        private static Brush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static CreateChatSessionIssueItemViewModel CreateInfoIssue(string title, string message)
        {
            return CreateIssueItem(title, message, InfoBackgroundBrush, InfoBorderBrush, InfoForegroundBrush);
        }

        private static CreateChatSessionIssueItemViewModel CreateWarningIssue(string title, string message)
        {
            return CreateIssueItem(title, message, WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
        }

        private static CreateChatSessionIssueItemViewModel CreateErrorIssue(string title, string message)
        {
            return CreateIssueItem(title, message, ErrorBackgroundBrush, ErrorBorderBrush, ErrorForegroundBrush);
        }

        private static CreateChatSessionIssueItemViewModel CreateIssueItem(
            string title,
            string message,
            Brush background,
            Brush border,
            Brush foreground)
        {
            return new CreateChatSessionIssueItemViewModel
            {
                Title = title,
                Message = message,
                AccentBackground = background,
                AccentBorderBrush = border,
                AccentForeground = foreground
            };
        }
    }

    internal sealed class CreateChatSessionFlowOptionViewModel
    {
        public CreateChatSessionFlowOptionViewModel(ChatSessionFlowBindingOption bindingOption)
        {
            BindingOption = bindingOption ?? throw new ArgumentNullException(nameof(bindingOption));
        }

        public ChatSessionFlowBindingOption BindingOption { get; }

        public string DisplayName => BindingOption.DisplayName;
    }

    internal sealed class CreateChatSessionHistoryItemViewModel
    {
        public string Title { get; init; } = string.Empty;

        public string FlowName { get; init; } = string.Empty;

        public string UpdatedAtText { get; init; } = string.Empty;

        public string IconPath { get; init; } = "pack://application:,,,/Resources/NewNodeGraphAlt.png";
    }

    internal sealed class CreateChatSessionAgentPreviewItemViewModel
    {
        public string AvatarPath { get; init; } = AgentDefinition.DefaultAvatarPath;

        public string DisplayName { get; init; } = string.Empty;

        public string AgentIdText { get; init; } = string.Empty;

        public string NodeSummary { get; init; } = string.Empty;

        public string ModeText { get; init; } = string.Empty;

        public string SelectionModeText { get; init; } = string.Empty;

        public string ModelSummary { get; init; } = string.Empty;

        public string DescriptionText { get; init; } = string.Empty;

        public bool HasStatusBadge { get; init; }

        public string StatusBadgeText { get; init; } = string.Empty;

        public Brush StatusBadgeBackground { get; init; } = Brushes.Transparent;

        public Brush StatusBadgeBorderBrush { get; init; } = Brushes.Transparent;

        public Brush StatusBadgeForeground { get; init; } = Brushes.White;
    }

    internal sealed class CreateChatSessionModelPreviewItemViewModel
    {
        public string DisplayName { get; init; } = string.Empty;

        public string ModelIdText { get; init; } = string.Empty;

        public string InterfaceTypeText { get; init; } = string.Empty;

        public string SourceTypeText { get; init; } = string.Empty;

        public string UsedByText { get; init; } = string.Empty;
    }

    internal sealed class CreateChatSessionIssueItemViewModel
    {
        public string Title { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public Brush AccentBackground { get; init; } = Brushes.Transparent;

        public Brush AccentBorderBrush { get; init; } = Brushes.Transparent;

        public Brush AccentForeground { get; init; } = Brushes.White;
    }

    internal sealed class CreateChatSessionModelUsageAccumulator
    {
        public CreateChatSessionModelUsageAccumulator(LanguageModelDefinition model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public LanguageModelDefinition Model { get; }

        public HashSet<string> AgentNames { get; } = new(StringComparer.CurrentCultureIgnoreCase);

        public HashSet<string> SourceTypes { get; } = new(StringComparer.CurrentCultureIgnoreCase);

        public string DisplayName
        {
            get
            {
                var displayName = Model.DisplayName?.Trim() ?? string.Empty;
                if (displayName.Length > 0)
                {
                    return displayName;
                }

                var summaryModelId = Model.SummaryModelId?.Trim() ?? string.Empty;
                if (summaryModelId.Length > 0)
                {
                    return $"未命名模型 ({summaryModelId})";
                }

                var key = Model.Key?.Trim() ?? string.Empty;
                return key.Length <= 8 ? $"未命名模型 ({key})" : $"未命名模型 ({key[..8]})";
            }
        }

        public string ModelIdText => string.IsNullOrWhiteSpace(Model.SummaryModelId)
            ? $"Key: {Model.Key}"
            : $"模型 ID: {Model.SummaryModelId}";

        public CreateChatSessionModelPreviewItemViewModel ToViewModel()
        {
            var sourceTypeText = string.Join(" / ", SourceTypes.OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase));
            var agentList = AgentNames.OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var usedByText = agentList.Length == 0
                ? "尚未关联代理"
                : $"代理：{string.Join("、", agentList)}";

            return new CreateChatSessionModelPreviewItemViewModel
            {
                DisplayName = DisplayName,
                ModelIdText = ModelIdText,
                InterfaceTypeText = string.IsNullOrWhiteSpace(Model.InterfaceType) ? "未知接口" : Model.InterfaceType,
                SourceTypeText = sourceTypeText.Length == 0 ? "未说明来源" : sourceTypeText,
                UsedByText = usedByText
            };
        }
    }

    internal sealed record CreateChatSessionAgentStatus(
        string Text,
        Brush Background,
        Brush BorderBrush,
        Brush Foreground);
}
