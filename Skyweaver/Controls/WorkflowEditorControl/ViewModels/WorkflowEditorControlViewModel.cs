using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Services;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.WorkflowEditorControl.ViewModels
{
    public sealed partial class WorkflowEditorControlViewModel : ObservableObject
    {
        private sealed class DelegateDisposable : IDisposable
        {
            private readonly Action _disposeAction;
            private bool _disposed;

            public DelegateDisposable(Action disposeAction)
            {
                _disposeAction = disposeAction;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _disposeAction();
            }
        }

        private sealed record PortTemplate(
            string StableId,
            string Name,
            SessionFlowPortType PortType,
            bool IsBooleanCondition = false,
            bool IsTransparentOutput = false);

        private const double DefaultCanvasWidth = 3200;
        private const double DefaultCanvasHeight = 2000;
        private const double DefaultSurfaceScale = 0.75;
        private const double DefaultEndpointNodeGap = 220;
        private const double NodeTopPadding = 44;
        private const double PortRowHeight = 24;
        private readonly AgentConfigurationRepository _agentConfigurationRepository;
        private readonly SessionFlowRepository _sessionFlowRepository;
        private readonly Dictionary<string, SessionFlowAgentOption> _agentOptionMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<SessionFlowNodeModel> _nodes = new();
        private readonly ObservableCollection<SessionFlowConnectionModel> _connections = new();
        private readonly ObservableCollection<SessionFlowAgentOption> _availableAgents = new();
        private int _suspendPersistenceCounter;
        private SessionFlowNodeModel? _selectedNode;
        private string _statusMessage = string.Empty;
        private double _canvasWidth = DefaultCanvasWidth;
        private double _canvasHeight = DefaultCanvasHeight;
        private Thickness _inspectorMargin = new(16, 16, 0, 0);
        private SessionFlowNodeModel? _pendingSourceNode;
        private SessionFlowPortModel? _pendingSourcePort;
        private double _spawnX = 420;
        private double _spawnY = 180;

        public WorkflowEditorControlViewModel(int instanceNumber)
        {
            Title = instanceNumber > 1 ? $"会话流编辑器 {instanceNumber}" : "会话流编辑器";

            var pathProvider = new SessionFlowPathProvider();
            _sessionFlowRepository = new SessionFlowRepository(pathProvider);
            _agentConfigurationRepository = new AgentConfigurationRepository(new AgentConfigurationPathProvider());

            InitializeGraphLibraryCommands();
            AddAgentNodeCommand = new RelayCommand<SessionFlowAgentOption>(AddAgentNode, option => option?.CanCreate == true);
            AddLogicAndNodeCommand = new RelayCommand(() => AddLogicNode(SessionFlowNodeKind.LogicAnd));
            AddLogicOrNodeCommand = new RelayCommand(() => AddLogicNode(SessionFlowNodeKind.LogicOr));
            AddLogicXorNodeCommand = new RelayCommand(() => AddLogicNode(SessionFlowNodeKind.LogicXor));
            AddLogicNotNodeCommand = new RelayCommand(() => AddLogicNode(SessionFlowNodeKind.LogicNot));
            AddLogicExecutionNodeCommand = new RelayCommand(AddLogicExecutionNode);
            AddNextLogicExecutionNodeCommand = new RelayCommand(AddNextLogicExecutionNode);
            DeleteSelectedNodeCommand = new RelayCommand(DeleteSelectedNode, () => SelectedNode?.CanDelete == true);
            ClearSelectionCommand = new RelayCommand(ClearSelection);
            SaveCommand = new RelayCommand(() => PersistGraph("会话流已保存。"));
            OpenConfigurationDirectoryCommand = new RelayCommand(OpenConfigurationDirectory);
            AddExecutionNaturalLanguageInputCommand = new RelayCommand(
                () => AddExecutionTransparentInput(SessionFlowPortType.NaturalLanguage),
                () => SelectedNode?.IsLogicExecutionNode == true);
            AddExecutionXmlInputCommand = new RelayCommand(
                () => AddExecutionTransparentInput(SessionFlowPortType.XmlField),
                () => SelectedNode?.IsLogicExecutionNode == true);
            CancelPendingConnectionCommand = new RelayCommand(ClearPendingConnection, () => _pendingSourceNode != null);

            _nodes.CollectionChanged += OnNodesCollectionChanged;
            _connections.CollectionChanged += (_, _) => CommandManager.InvalidateRequerySuggested();

            LoadAgents();
            InitializeGraphLibrary();
        }

        public string Title { get; }

        public string Description { get; } = "编辑代理会话流图结构，管理节点连接与执行路径。";

        public string Hint { get; } = "开始节点固定为“用户输入”，终止节点固定为“返回”。";

        public string PersistenceFilePath => _currentNodeGraph?.FilePath ?? "未打开节点图";

        public string ConfigurationDirectoryPath => _sessionFlowRepository.ConfigurationDirectoryPath;

        public ObservableCollection<SessionFlowNodeModel> Nodes => _nodes;

        public ObservableCollection<SessionFlowConnectionModel> Connections => _connections;

        public ObservableCollection<SessionFlowAgentOption> AvailableAgents => _availableAgents;

        public bool HasAvailableAgents => _agentOptionMap.Count > 0;

        public string AvailableAgentSummaryText => HasAvailableAgents
            ? $"已接入 {_agentOptionMap.Count} 个代理配置。代理节点的输入/输出类型会自动跟随“代理配置”页面。"
            : "当前没有可用代理配置。请先在“代理配置”页面创建代理。";

        public SessionFlowNodeModel? SelectedNode
        {
            get => _selectedNode;
            private set
            {
                if (!SetProperty(ref _selectedNode, value))
                {
                    return;
                }

                ApplySelectionState(_selectedNode);
                UpdateInspectorState();
                OnPropertyChanged(nameof(IsInspectorVisible));
                OnPropertyChanged(nameof(InspectorTitle));
                OnPropertyChanged(nameof(IsSelectedNodeAgent));
                OnPropertyChanged(nameof(IsSelectedNodeLogicExecution));
                OnPropertyChanged(nameof(SelectedNodeKindHint));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsInspectorVisible => SelectedNode != null;

        public string InspectorTitle => SelectedNode == null
            ? string.Empty
            : $"{SelectedNode.Title} 设置";

        public bool IsSelectedNodeAgent => SelectedNode?.IsAgentNode == true;

        public bool IsSelectedNodeLogicExecution => SelectedNode?.IsLogicExecutionNode == true;

        public string SelectedNodeKindHint => SelectedNode switch
        {
            null => string.Empty,
            { IsAgentNode: true } => "代理节点可配置“是否为隐代理”。",
            { IsLogicExecutionNode: true } => "可新增透传输入端口，每个输入自动生成对应输出。",
            { IsNextLogicExecutionNode: true } => "智能输入端口会在连接后自动定型，并新增下一个智能输入。",
            _ => "该节点暂未提供额外配置。"
        };

        public Thickness InspectorMargin
        {
            get => _inspectorMargin;
            private set => SetProperty(ref _inspectorMargin, value);
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            private set => SetProperty(ref _canvasWidth, Math.Max(DefaultCanvasWidth, value));
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            private set => SetProperty(ref _canvasHeight, Math.Max(DefaultCanvasHeight, value));
        }

        public double SurfaceScale => DefaultSurfaceScale;

        public string PendingConnectionText
        {
            get
            {
                if (_pendingSourceNode == null || _pendingSourcePort == null)
                {
                    return "未选择连接起点";
                }

                return $"起点：{_pendingSourceNode.Title} / {_pendingSourcePort.Name}";
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value ?? string.Empty);
        }

        public ICommand AddAgentNodeCommand { get; }

        public ICommand AddLogicAndNodeCommand { get; }

        public ICommand AddLogicOrNodeCommand { get; }

        public ICommand AddLogicXorNodeCommand { get; }

        public ICommand AddLogicNotNodeCommand { get; }

        public ICommand AddLogicExecutionNodeCommand { get; }

        public ICommand AddNextLogicExecutionNodeCommand { get; }

        public ICommand DeleteSelectedNodeCommand { get; }

        public ICommand ClearSelectionCommand { get; }

        public ICommand SaveCommand { get; }

        public ICommand OpenConfigurationDirectoryCommand { get; }

        public ICommand AddExecutionNaturalLanguageInputCommand { get; }

        public ICommand AddExecutionXmlInputCommand { get; }

        public ICommand CancelPendingConnectionCommand { get; }

        public void SelectNode(SessionFlowNodeModel? node)
        {
            SelectedNode = node;
        }

        public void MoveNode(SessionFlowNodeModel node, double x, double y)
        {
            if (node == null)
            {
                return;
            }

            var normalizedX = Math.Clamp(x, 16, Math.Max(16, CanvasWidth - node.Width - 16));
            var normalizedY = Math.Clamp(y, 16, Math.Max(16, CanvasHeight - 100));

            node.X = normalizedX;
            node.Y = normalizedY;

            RefreshConnectionPathsForNode(node);

            if (ReferenceEquals(SelectedNode, node))
            {
                UpdateInspectorState();
            }
        }

        public void CommitNodeMove(SessionFlowNodeModel node)
        {
            if (node == null)
            {
                return;
            }

            PersistGraph("会话流布局已保存。");
        }

        public void HandlePortClick(SessionFlowNodeModel node, SessionFlowPortModel port)
        {
            if (node == null || port == null)
            {
                return;
            }

            SelectNode(node);

            if (port.Direction == SessionFlowPortDirection.Output)
            {
                _pendingSourceNode = node;
                _pendingSourcePort = port;
                StatusMessage = $"已选择输出端口：{node.Title} / {port.Name}。请点击目标输入端口。";
                OnPropertyChanged(nameof(PendingConnectionText));
                CommandManager.InvalidateRequerySuggested();
                return;
            }

            if (_pendingSourceNode == null || _pendingSourcePort == null)
            {
                StatusMessage = "请先选择一个输出端口作为连接起点。";
                return;
            }

            if (TryConnectPorts(_pendingSourceNode, _pendingSourcePort, node, port))
            {
                PersistGraph("连接已更新。");
            }

            ClearPendingConnection();
        }

        public bool TryNormalizeLogicBoolean(object? value, out bool result)
        {
            return SessionFlowBooleanNormalizer.TryNormalize(value, out result);
        }

        public void RefreshAgentCatalog()
        {
            LoadAgents();

            if (!_nodes.Any(node => node.IsAgentNode))
            {
                StatusMessage = HasAvailableAgents
                    ? "代理目录已刷新。"
                    : "当前没有可用代理配置。请先在“代理配置”页面创建代理。";
                return;
            }

            using (SuspendPersistence())
            {
                EnsureAgentNodePorts();
                SanitizeConnections();
                RefreshAllConnectionPaths();
            }

            PersistGraph("代理节点已根据代理配置同步。");
        }

        private void LoadAgents()
        {
            _availableAgents.Clear();
            _agentOptionMap.Clear();

            IReadOnlyList<AgentDefinition> definitions;
            try
            {
                definitions = _agentConfigurationRepository.Load();
            }
            catch (Exception ex)
            {
                StatusMessage = $"读取代理配置失败：{ex.Message}";
                definitions = Array.Empty<AgentDefinition>();
            }

            foreach (var definition in definitions
                          .Where(item => !string.IsNullOrWhiteSpace(item.AgentId))
                          .Where(item => item.CanRunAsMainAgent)
                          .OrderBy(item => item.DisplayNameOrFallback, StringComparer.OrdinalIgnoreCase))
            {
                var inputFieldPaths = definition.IsStructuredXmlIO
                    ? FlattenXmlFieldPaths(definition.InputSchemaRoot)
                    : Array.Empty<string>();
                var outputFieldPaths = definition.IsStructuredXmlIO
                    ? FlattenXmlFieldPaths(definition.OutputSchemaRoot)
                    : Array.Empty<string>();

                var option = new SessionFlowAgentOption
                {
                    AgentId = definition.AgentId,
                    DisplayName = definition.DisplayNameOrFallback,
                    IsStructuredXmlIO = definition.IsStructuredXmlIO,
                    InputPortType = definition.IsStructuredXmlIO ? SessionFlowPortType.XmlField : SessionFlowPortType.NaturalLanguage,
                    OutputPortType = definition.IsStructuredXmlIO ? SessionFlowPortType.XmlField : SessionFlowPortType.NaturalLanguage,
                    InputFieldPaths = inputFieldPaths,
                    OutputFieldPaths = outputFieldPaths
                };

                _availableAgents.Add(option);
                _agentOptionMap[option.AgentId] = option;
            }

            if (_availableAgents.Count == 0)
            {
                _availableAgents.Add(new SessionFlowAgentOption
                {
                    DisplayName = "暂无可用代理配置",
                    CanCreate = false
                });
            }

            OnPropertyChanged(nameof(HasAvailableAgents));
            OnPropertyChanged(nameof(AvailableAgentSummaryText));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ApplyGraph(SessionFlowGraphModel graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            using (SuspendPersistence())
            {
                _nodes.Clear();
                _connections.Clear();

                CanvasWidth = graph.CanvasWidth;
                CanvasHeight = graph.CanvasHeight;

                foreach (var node in graph.Nodes.Select(node => node.DeepClone()))
                {
                    NormalizeNode(node);
                    _nodes.Add(node);
                }

                EnsureMandatoryEndpointNodes();
                EnsureAgentNodePorts();

                foreach (var connection in graph.Connections.Select(connection => connection.DeepClone()))
                {
                    _connections.Add(connection);
                }

                SanitizeConnections();
                RefreshAllConnectionPaths();
                UpdateSpawnCursor();
                SelectNode(null);
                ClearPendingConnection();
            }
        }

        private void AddAgentNode(SessionFlowAgentOption? option)
        {
            if (option?.CanCreate != true)
            {
                StatusMessage = "当前没有可用代理配置。";
                return;
            }

            if (_agentOptionMap.TryGetValue(option.AgentId, out var latestOption))
            {
                option = latestOption;
            }

            var (x, y) = GetNextSpawnPosition();
            var node = new SessionFlowNodeModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = SessionFlowNodeKind.Agent,
                Title = option.DisplayName,
                AgentId = option.AgentId,
                AgentDisplayName = option.DisplayName,
                Width = 250,
                X = x,
                Y = y
            };

            ApplyAgentPorts(node, option);

            Nodes.Add(node);
            SelectNode(node);
            PersistGraph($"已新增代理节点：{node.Title}。");
        }

        private void AddLogicNode(SessionFlowNodeKind kind)
        {
            var (x, y) = GetNextSpawnPosition();
            var node = new SessionFlowNodeModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Title = kind switch
                {
                    SessionFlowNodeKind.LogicAnd => "与",
                    SessionFlowNodeKind.LogicOr => "或",
                    SessionFlowNodeKind.LogicXor => "异或",
                    SessionFlowNodeKind.LogicNot => "非",
                    _ => "逻辑"
                },
                Width = 220,
                X = x,
                Y = y
            };

            if (kind == SessionFlowNodeKind.LogicNot)
            {
                node.InputPorts.Add(CreateBooleanXmlInput("in-a", "输入"));
            }
            else
            {
                node.InputPorts.Add(CreateBooleanXmlInput("in-a", "输入A"));
                node.InputPorts.Add(CreateBooleanXmlInput("in-b", "输入B"));
            }

            node.OutputPorts.Add(CreatePort("out-result", "结果", SessionFlowPortDirection.Output, SessionFlowPortType.XmlField, isTransparentOutput: true));

            Nodes.Add(node);
            SelectNode(node);
            PersistGraph($"已新增逻辑节点：{node.Title}。");
        }

        private void AddLogicExecutionNode()
        {
            var (x, y) = GetNextSpawnPosition();
            var node = new SessionFlowNodeModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = SessionFlowNodeKind.LogicExecution,
                Title = "逻辑执行",
                Width = 260,
                X = x,
                Y = y
            };

            node.InputPorts.Add(CreateBooleanXmlInput("condition", "条件(Boolean)"));

            Nodes.Add(node);
            SelectNode(node);
            PersistGraph("已新增逻辑执行节点。");
        }

        private void AddNextLogicExecutionNode()
        {
            var (x, y) = GetNextSpawnPosition();
            var node = new SessionFlowNodeModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = SessionFlowNodeKind.NextLogicExecution,
                Title = "仅下一个逻辑执行",
                Width = 280,
                X = x,
                Y = y
            };

            node.InputPorts.Add(CreateBooleanXmlInput("condition", "条件(Boolean)"));
            EnsureSmartFlexibleInput(node);

            Nodes.Add(node);
            SelectNode(node);
            PersistGraph("已新增仅下一个逻辑执行节点。");
        }

        private void AddExecutionTransparentInput(SessionFlowPortType portType)
        {
            if (SelectedNode?.IsLogicExecutionNode != true)
            {
                return;
            }

            var pairIndex = SelectedNode.InputPorts.Count(port => !port.IsBooleanCondition) + 1;
            var pairKey = $"pair-{Guid.NewGuid():N}";
            var suffix = portType == SessionFlowPortType.XmlField ? "XML" : "自然语言";

            SelectedNode.InputPorts.Add(new SessionFlowPortModel
            {
                Id = $"input-{Guid.NewGuid():N}",
                Name = $"{suffix}输入 {pairIndex}",
                Direction = SessionFlowPortDirection.Input,
                PortType = portType,
                PairKey = pairKey
            });

            SelectedNode.OutputPorts.Add(new SessionFlowPortModel
            {
                Id = $"output-{Guid.NewGuid():N}",
                Name = $"{suffix}输出 {pairIndex}",
                Direction = SessionFlowPortDirection.Output,
                PortType = portType,
                PairKey = pairKey,
                IsTransparentOutput = true
            });

            RefreshConnectionPathsForNode(SelectedNode);
            PersistGraph("逻辑执行节点端口已更新。");
        }

        private void DeleteSelectedNode()
        {
            if (SelectedNode == null || !SelectedNode.CanDelete)
            {
                return;
            }

            var nodeToDelete = SelectedNode;
            var relatedConnections = _connections
                .Where(connection => connection.SourceNodeId == nodeToDelete.Id || connection.TargetNodeId == nodeToDelete.Id)
                .ToList();

            foreach (var connection in relatedConnections)
            {
                _connections.Remove(connection);
            }

            _nodes.Remove(nodeToDelete);
            SelectNode(null);
            PersistGraph("节点已删除。");
        }

        private void ClearSelection()
        {
            SelectNode(null);
            ClearPendingConnection();
        }

        private void OpenConfigurationDirectory()
        {
            Directory.CreateDirectory(ConfigurationDirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigurationDirectoryPath,
                UseShellExecute = true
            });
        }

        private void OnNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var node in e.NewItems.OfType<SessionFlowNodeModel>())
                {
                    AttachNode(node);
                }
            }

            if (e.OldItems != null)
            {
                foreach (var node in e.OldItems.OfType<SessionFlowNodeModel>())
                {
                    DetachNode(node);
                }
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachNode(SessionFlowNodeModel node)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
            node.PropertyChanged += OnNodePropertyChanged;

            node.InputPorts.CollectionChanged -= OnNodePortsCollectionChanged;
            node.InputPorts.CollectionChanged += OnNodePortsCollectionChanged;
            foreach (var port in node.InputPorts)
            {
                AttachPort(port);
            }

            node.OutputPorts.CollectionChanged -= OnNodePortsCollectionChanged;
            node.OutputPorts.CollectionChanged += OnNodePortsCollectionChanged;
            foreach (var port in node.OutputPorts)
            {
                AttachPort(port);
            }
        }

        private void DetachNode(SessionFlowNodeModel node)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
            node.InputPorts.CollectionChanged -= OnNodePortsCollectionChanged;
            node.OutputPorts.CollectionChanged -= OnNodePortsCollectionChanged;

            foreach (var port in node.InputPorts)
            {
                DetachPort(port);
            }

            foreach (var port in node.OutputPorts)
            {
                DetachPort(port);
            }
        }

        private void OnNodePortsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var port in e.NewItems.OfType<SessionFlowPortModel>())
                {
                    AttachPort(port);
                }
            }

            if (e.OldItems != null)
            {
                foreach (var port in e.OldItems.OfType<SessionFlowPortModel>())
                {
                    DetachPort(port);
                }
            }

            if (_suspendPersistenceCounter > 0)
            {
                return;
            }

            SanitizeConnections();
            RefreshAllConnectionPaths();
        }

        private void AttachPort(SessionFlowPortModel port)
        {
            port.PropertyChanged -= OnPortPropertyChanged;
            port.PropertyChanged += OnPortPropertyChanged;
        }

        private void DetachPort(SessionFlowPortModel port)
        {
            port.PropertyChanged -= OnPortPropertyChanged;
        }

        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not SessionFlowNodeModel node)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(SessionFlowNodeModel.IsSelected), StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(SessionFlowNodeModel.X), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(SessionFlowNodeModel.Y), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(SessionFlowNodeModel.Width), StringComparison.Ordinal))
            {
                RefreshConnectionPathsForNode(node);
                return;
            }

            if (_suspendPersistenceCounter > 0)
            {
                return;
            }

            PersistGraph("会话流已保存。");
        }

        private void OnPortPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suspendPersistenceCounter > 0)
            {
                return;
            }

            if (sender is not SessionFlowPortModel)
            {
                return;
            }

            SanitizeConnections();
            RefreshAllConnectionPaths();
            PersistGraph("端口配置已保存。");
        }

        private void EnsureMandatoryEndpointNodes()
        {
            var userInputNode = _nodes.FirstOrDefault(node => node.Kind == SessionFlowNodeKind.UserInput);
            if (userInputNode == null)
            {
                userInputNode = new SessionFlowNodeModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = SessionFlowNodeKind.UserInput,
                    Title = "用户输入",
                    Width = 220,
                    X = 120,
                    Y = 220,
                    IsFixed = true
                };
                _nodes.Insert(0, userInputNode);
            }

            userInputNode.IsFixed = true;
            userInputNode.Title = "用户输入";
            ApplyPortLayout(
                userInputNode,
                Array.Empty<PortTemplate>(),
                new[]
                {
                    new PortTemplate("output-user-text", "自然语言输出", SessionFlowPortType.NaturalLanguage)
                });

            var returnNode = _nodes.FirstOrDefault(node => node.Kind == SessionFlowNodeKind.Return);
            if (returnNode == null)
            {
                returnNode = new SessionFlowNodeModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = SessionFlowNodeKind.Return,
                    Title = "返回",
                    Width = 220,
                    X = userInputNode.X + userInputNode.Width + DefaultEndpointNodeGap,
                    Y = 260,
                    IsFixed = true
                };
                _nodes.Add(returnNode);
            }

            returnNode.IsFixed = true;
            returnNode.Title = "返回";
            ApplyPortLayout(
                returnNode,
                new[]
                {
                    new PortTemplate("input-return-text", "自然语言输入", SessionFlowPortType.NaturalLanguage),
                    new PortTemplate("input-return-xml", "XML字段输入", SessionFlowPortType.XmlField)
                },
                Array.Empty<PortTemplate>());
        }

        private void EnsureAgentNodePorts()
        {
            foreach (var node in _nodes.Where(item => item.IsAgentNode))
            {
                if (!_agentOptionMap.TryGetValue(node.AgentId, out var option))
                {
                    if (node.InputPorts.Count == 0 && node.OutputPorts.Count == 0)
                    {
                        ApplyPortLayout(
                            node,
                            new[]
                            {
                                new PortTemplate("agent-input", "自然语言输入", SessionFlowPortType.NaturalLanguage)
                            },
                            new[]
                            {
                                new PortTemplate("agent-output", "自然语言输出", SessionFlowPortType.NaturalLanguage)
                            });
                    }

                    continue;
                }

                node.AgentDisplayName = option.DisplayName;
                node.Title = option.DisplayName;
                ApplyAgentPorts(node, option);
            }
        }

        private static void ApplyAgentPorts(SessionFlowNodeModel node, SessionFlowAgentOption option)
        {
            if (option.IsStructuredXmlIO)
            {
                ApplyPortLayout(
                    node,
                    CreateStructuredXmlPortTemplates(option.InputFieldPaths, "agent-input-xml", "XML输入"),
                    CreateStructuredXmlPortTemplates(option.OutputFieldPaths, "agent-output-xml", "XML输出"));
                return;
            }

            ApplyPortLayout(
                node,
                new[]
                {
                    new PortTemplate("agent-input", option.InputPortName, option.InputPortType)
                },
                new[]
                {
                    new PortTemplate("agent-output", option.OutputPortName, option.OutputPortType)
                });
        }

        private static IReadOnlyList<PortTemplate> CreateStructuredXmlPortTemplates(
            IReadOnlyList<string> fieldPaths,
            string idPrefix,
            string fallbackName)
        {
            var templates = fieldPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new PortTemplate(CreateStableXmlPortId(idPrefix, path), path, SessionFlowPortType.XmlField))
                .ToList();

            if (templates.Count == 0)
            {
                templates.Add(new PortTemplate(idPrefix, fallbackName, SessionFlowPortType.XmlField));
            }

            return templates;
        }

        private static string[] FlattenXmlFieldPaths(XmlElementNodeDefinition rootNode)
        {
            var paths = new List<string>();
            foreach (var child in rootNode.Children)
            {
                AppendXmlFieldPaths(child, parentPath: null, paths);
            }

            return paths.ToArray();
        }

        private static void AppendXmlFieldPaths(
            XmlElementNodeDefinition node,
            string? parentPath,
            ICollection<string> paths)
        {
            var nodeName = (node.Name ?? string.Empty).Trim();
            if (nodeName.Length == 0)
            {
                return;
            }

            var currentPath = string.IsNullOrWhiteSpace(parentPath)
                ? nodeName
                : $"{parentPath}/{nodeName}";

            paths.Add(currentPath);

            foreach (var child in node.Children)
            {
                AppendXmlFieldPaths(child, currentPath, paths);
            }
        }

        private static void ApplyPortLayout(
            SessionFlowNodeModel node,
            IReadOnlyList<PortTemplate> inputTemplates,
            IReadOnlyList<PortTemplate> outputTemplates)
        {
            ReplacePorts(node.InputPorts, inputTemplates, SessionFlowPortDirection.Input);
            ReplacePorts(node.OutputPorts, outputTemplates, SessionFlowPortDirection.Output);
        }

        private static void ReplacePorts(
            ObservableCollection<SessionFlowPortModel> targetPorts,
            IReadOnlyList<PortTemplate> templates,
            SessionFlowPortDirection direction)
        {
            var existingPorts = targetPorts.ToList();
            var usedPorts = new HashSet<SessionFlowPortModel>();
            targetPorts.Clear();

            foreach (var template in templates)
            {
                var port = existingPorts.FirstOrDefault(candidate =>
                               !usedPorts.Contains(candidate) &&
                               string.Equals(candidate.Id, template.StableId, StringComparison.Ordinal))
                           ?? existingPorts.FirstOrDefault(candidate =>
                               !usedPorts.Contains(candidate) &&
                               candidate.Direction == direction &&
                               candidate.PortType == template.PortType &&
                               candidate.IsBooleanCondition == template.IsBooleanCondition &&
                               candidate.IsTransparentOutput == template.IsTransparentOutput &&
                               !candidate.IsFlexiblePlaceholder);

                if (port == null)
                {
                    port = CreateStablePort(
                        template.StableId,
                        template.Name,
                        direction,
                        template.PortType,
                        isBooleanCondition: template.IsBooleanCondition,
                        isTransparentOutput: template.IsTransparentOutput);
                }

                port.Id = string.IsNullOrWhiteSpace(port.Id) ? template.StableId : port.Id;
                port.Name = template.Name;
                port.Direction = direction;
                port.PortType = template.PortType;
                port.IsFlexiblePlaceholder = false;
                port.IsBooleanCondition = template.IsBooleanCondition;
                port.IsTransparentOutput = template.IsTransparentOutput;
                port.PairKey = string.Empty;

                targetPorts.Add(port);
                usedPorts.Add(port);
            }
        }

        private void NormalizeNode(SessionFlowNodeModel node)
        {
            node.X = double.IsFinite(node.X) ? node.X : 0;
            node.Y = double.IsFinite(node.Y) ? node.Y : 0;
            node.Width = !double.IsFinite(node.Width) || node.Width < 200 ? 240 : node.Width;

            foreach (var port in node.InputPorts)
            {
                port.Direction = SessionFlowPortDirection.Input;
            }

            foreach (var port in node.OutputPorts)
            {
                port.Direction = SessionFlowPortDirection.Output;
            }

            if (node.IsNextLogicExecutionNode)
            {
                EnsureSmartFlexibleInput(node);
            }
        }

        private void EnsureSmartFlexibleInput(SessionFlowNodeModel node)
        {
            if (!node.IsNextLogicExecutionNode)
            {
                return;
            }

            if (node.InputPorts.Any(port => port.IsFlexiblePlaceholder))
            {
                return;
            }

            node.InputPorts.Add(new SessionFlowPortModel
            {
                Id = $"smart-input-{Guid.NewGuid():N}",
                Name = "智能输入",
                Direction = SessionFlowPortDirection.Input,
                IsFlexiblePlaceholder = true,
                PortType = SessionFlowPortType.NaturalLanguage
            });
        }

        private bool TryConnectPorts(
            SessionFlowNodeModel sourceNode,
            SessionFlowPortModel sourcePort,
            SessionFlowNodeModel targetNode,
            SessionFlowPortModel targetPort)
        {
            if (sourcePort.Direction != SessionFlowPortDirection.Output)
            {
                StatusMessage = "连接起点必须是输出端口。";
                return false;
            }

            if (targetPort.Direction != SessionFlowPortDirection.Input)
            {
                StatusMessage = "连接目标必须是输入端口。";
                return false;
            }

            if (targetPort.IsFlexiblePlaceholder)
            {
                MaterializeSmartInput(targetNode, targetPort, sourcePort.PortType);
            }

            if (sourcePort.PortType != targetPort.PortType)
            {
                StatusMessage = $"端口类型不匹配：{sourcePort.PortTypeDisplayText} 无法连接到 {targetPort.PortTypeDisplayText}。";
                return false;
            }

            var targetKey = $"{targetNode.Id}:{targetPort.Id}";
            var duplicatedIncoming = _connections.Where(connection =>
                    string.Equals($"{connection.TargetNodeId}:{connection.TargetPortId}", targetKey, StringComparison.Ordinal))
                .ToList();
            foreach (var incoming in duplicatedIncoming)
            {
                _connections.Remove(incoming);
            }

            var exists = _connections.Any(connection =>
                connection.SourceNodeId == sourceNode.Id &&
                connection.SourcePortId == sourcePort.Id &&
                connection.TargetNodeId == targetNode.Id &&
                connection.TargetPortId == targetPort.Id);
            if (exists)
            {
                StatusMessage = "该连接已存在。";
                return false;
            }

            var connectionModel = new SessionFlowConnectionModel
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceNodeId = sourceNode.Id,
                SourcePortId = sourcePort.Id,
                TargetNodeId = targetNode.Id,
                TargetPortId = targetPort.Id
            };

            _connections.Add(connectionModel);
            RefreshConnectionPath(connectionModel);
            StatusMessage = $"已连接：{sourceNode.Title} -> {targetNode.Title}";
            return true;
        }

        private void MaterializeSmartInput(SessionFlowNodeModel node, SessionFlowPortModel smartPort, SessionFlowPortType incomingPortType)
        {
            if (!node.IsNextLogicExecutionNode || !smartPort.IsFlexiblePlaceholder)
            {
                return;
            }

            var index = node.InputPorts.Count(port => !port.IsBooleanCondition && !port.IsFlexiblePlaceholder) + 1;
            var pairKey = $"smart-pair-{Guid.NewGuid():N}";
            var labelPrefix = incomingPortType == SessionFlowPortType.XmlField ? "XML" : "自然语言";

            smartPort.IsFlexiblePlaceholder = false;
            smartPort.PortType = incomingPortType;
            smartPort.Name = $"{labelPrefix}输入 {index}";
            smartPort.PairKey = pairKey;

            node.OutputPorts.Add(new SessionFlowPortModel
            {
                Id = $"smart-output-{Guid.NewGuid():N}",
                Name = $"{labelPrefix}输出 {index}",
                Direction = SessionFlowPortDirection.Output,
                PortType = incomingPortType,
                PairKey = pairKey,
                IsTransparentOutput = true
            });

            EnsureSmartFlexibleInput(node);
        }

        private void SanitizeConnections()
        {
            var seenTargetInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removableConnections = new List<SessionFlowConnectionModel>();

            foreach (var connection in _connections)
            {
                if (!TryResolveConnectionEndpoints(connection, out var sourcePort, out var targetPort))
                {
                    removableConnections.Add(connection);
                    continue;
                }

                if (sourcePort.Direction != SessionFlowPortDirection.Output ||
                    targetPort.Direction != SessionFlowPortDirection.Input)
                {
                    removableConnections.Add(connection);
                    continue;
                }

                if (sourcePort.PortType != targetPort.PortType)
                {
                    removableConnections.Add(connection);
                    continue;
                }

                var sourceNode = _nodes.FirstOrDefault(node => string.Equals(node.Id, connection.SourceNodeId, StringComparison.OrdinalIgnoreCase));
                var targetNode = _nodes.FirstOrDefault(node => string.Equals(node.Id, connection.TargetNodeId, StringComparison.OrdinalIgnoreCase));
                var targetKey = $"{connection.TargetNodeId}:{connection.TargetPortId}";
                if (!seenTargetInputs.Add(targetKey))
                {
                    removableConnections.Add(connection);
                }
            }

            foreach (var connection in removableConnections)
            {
                _connections.Remove(connection);
            }
        }

        private void RefreshAllConnectionPaths()
        {
            foreach (var connection in _connections)
            {
                RefreshConnectionPath(connection);
            }
        }

        private void RefreshConnectionPathsForNode(SessionFlowNodeModel node)
        {
            foreach (var connection in _connections.Where(connection =>
                         connection.SourceNodeId == node.Id || connection.TargetNodeId == node.Id))
            {
                RefreshConnectionPath(connection);
            }
        }

        private void RefreshConnectionPath(SessionFlowConnectionModel connection)
        {
            if (!TryResolveConnectionEndpoints(connection, out var sourcePort, out var targetPort))
            {
                ResetConnectionGeometry(connection);
                return;
            }

            var sourceNode = _nodes.First(node => node.Id == connection.SourceNodeId);
            var targetNode = _nodes.First(node => node.Id == connection.TargetNodeId);
            var sourcePoint = GetPortAnchorPoint(sourceNode, sourcePort);
            var targetPoint = GetPortAnchorPoint(targetNode, targetPort);
            connection.SourceX = sourcePoint.X;
            connection.SourceY = sourcePoint.Y;
            connection.TargetX = targetPoint.X;
            connection.TargetY = targetPoint.Y;
            connection.PortType = sourcePort.PortType;
            connection.PathData = CreateConnectionPathData(sourcePoint, targetPoint);
        }

        private static void ResetConnectionGeometry(SessionFlowConnectionModel connection)
        {
            connection.PathData = string.Empty;
            connection.SourceX = 0;
            connection.SourceY = 0;
            connection.TargetX = 0;
            connection.TargetY = 0;
        }

        private static string CreateConnectionPathData(Point sourcePoint, Point targetPoint)
        {
            const double lead = 28;
            const double forwardThreshold = 96;
            const double detourOffset = 72;

            var points = new List<Point>(8);
            AppendConnectionPoint(points, sourcePoint);
            AppendConnectionPoint(points, new Point(sourcePoint.X + lead, sourcePoint.Y));

            if (targetPoint.X - sourcePoint.X >= forwardThreshold)
            {
                var midX = sourcePoint.X + Math.Max((targetPoint.X - sourcePoint.X) * 0.5, lead);
                AppendConnectionPoint(points, new Point(midX, sourcePoint.Y));
                AppendConnectionPoint(points, new Point(midX, targetPoint.Y));
            }
            else
            {
                var detourX = Math.Max(sourcePoint.X, targetPoint.X) + detourOffset;
                var midY = sourcePoint.Y + ((targetPoint.Y - sourcePoint.Y) * 0.5);
                AppendConnectionPoint(points, new Point(detourX, sourcePoint.Y));
                AppendConnectionPoint(points, new Point(detourX, midY));
                AppendConnectionPoint(points, new Point(targetPoint.X - lead, midY));
            }

            AppendConnectionPoint(points, new Point(targetPoint.X - lead, targetPoint.Y));
            AppendConnectionPoint(points, targetPoint);

            if (points.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append("M ");
            AppendPointText(builder, points[0]);

            for (var index = 1; index < points.Count; index++)
            {
                builder.Append(" L ");
                AppendPointText(builder, points[index]);
            }

            return builder.ToString();
        }

        private static void AppendConnectionPoint(ICollection<Point> points, Point point)
        {
            if (points.Count > 0 && points.Last() is var lastPoint &&
                Math.Abs(lastPoint.X - point.X) < 0.01 &&
                Math.Abs(lastPoint.Y - point.Y) < 0.01)
            {
                return;
            }

            points.Add(point);
        }

        private static void AppendPointText(StringBuilder builder, Point point)
        {
            builder.Append(point.X.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Y.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private bool TryResolveConnectionEndpoints(
            SessionFlowConnectionModel connection,
            out SessionFlowPortModel sourcePort,
            out SessionFlowPortModel targetPort)
        {
            sourcePort = null!;
            targetPort = null!;

            var sourceNode = _nodes.FirstOrDefault(node => node.Id == connection.SourceNodeId);
            var targetNode = _nodes.FirstOrDefault(node => node.Id == connection.TargetNodeId);
            if (sourceNode == null || targetNode == null)
            {
                return false;
            }

            sourcePort = sourceNode.OutputPorts.FirstOrDefault(port => port.Id == connection.SourcePortId)
                ?? sourceNode.InputPorts.FirstOrDefault(port => port.Id == connection.SourcePortId)!;
            targetPort = targetNode.InputPorts.FirstOrDefault(port => port.Id == connection.TargetPortId)
                ?? targetNode.OutputPorts.FirstOrDefault(port => port.Id == connection.TargetPortId)!;

            return sourcePort != null && targetPort != null;
        }

        private Point GetPortAnchorPoint(SessionFlowNodeModel node, SessionFlowPortModel port)
        {
            var ports = port.Direction == SessionFlowPortDirection.Input ? node.InputPorts : node.OutputPorts;
            var index = ports.IndexOf(port);
            if (index < 0)
            {
                index = 0;
            }

            var x = port.Direction == SessionFlowPortDirection.Input ? node.X : node.X + node.Width;
            var y = node.Y + NodeTopPadding + (index * PortRowHeight) + (PortRowHeight * 0.5);
            return new Point(x, y);
        }

        private void ApplySelectionState(SessionFlowNodeModel? selectedNode)
        {
            foreach (var node in _nodes)
            {
                node.IsSelected = ReferenceEquals(node, selectedNode);
            }
        }

        private void UpdateInspectorState()
        {
            if (SelectedNode == null)
            {
                InspectorMargin = new Thickness(16, 16, 0, 0);
                return;
            }

            var targetLeft = Math.Clamp(SelectedNode.X + SelectedNode.Width + 26, 16, Math.Max(16, CanvasWidth - 360));
            var targetTop = Math.Clamp(SelectedNode.Y, 16, Math.Max(16, CanvasHeight - 320));
            InspectorMargin = new Thickness(targetLeft, targetTop, 0, 0);
        }

        private void UpdateSpawnCursor()
        {
            if (_nodes.Count == 0)
            {
                _spawnX = 420;
                _spawnY = 180;
                return;
            }

            var rightMostNode = _nodes.OrderByDescending(node => node.X).First();
            _spawnX = rightMostNode.X + 320;
            _spawnY = rightMostNode.Y;

            if (_spawnX > CanvasWidth - 380)
            {
                _spawnX = 420;
                _spawnY += 220;
            }
        }

        private (double x, double y) GetNextSpawnPosition()
        {
            var x = _spawnX;
            var y = _spawnY;

            _spawnX += 320;
            if (_spawnX > CanvasWidth - 380)
            {
                _spawnX = 420;
                _spawnY += 220;
            }

            if (_spawnY > CanvasHeight - 260)
            {
                _spawnY = 180;
            }

            return (x, y);
        }

                private void PersistGraph(string successMessage)
        {
            if (_suspendPersistenceCounter > 0)
            {
                return;
            }

            if (_currentNodeGraph == null)
            {
                StatusMessage = "当前没有打开的节点图。";
                return;
            }

            try
            {
                var graph = new SessionFlowGraphModel
                {
                    CanvasWidth = CanvasWidth,
                    CanvasHeight = CanvasHeight
                };

                foreach (var node in _nodes)
                {
                    graph.Nodes.Add(node.DeepClone());
                }

                foreach (var connection in _connections)
                {
                    graph.Connections.Add(connection.DeepClone());
                }

                var savedDocument = _sessionFlowRepository.Save(CreateCurrentDocument(graph));
                UpdateCurrentNodeGraph(savedDocument);
                StatusMessage = successMessage;
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存会话流失败：{ex.Message}";
            }
        }

        private SessionFlowPortModel CreatePort(
            string idSeed,
            string name,
            SessionFlowPortDirection direction,
            SessionFlowPortType portType,
            bool isBooleanCondition = false,
            bool isTransparentOutput = false)
        {
            return new SessionFlowPortModel
            {
                Id = $"{idSeed}-{Guid.NewGuid():N}",
                Name = name,
                Direction = direction,
                PortType = portType,
                IsBooleanCondition = isBooleanCondition,
                IsTransparentOutput = isTransparentOutput
            };
        }

        private static SessionFlowPortModel CreateStablePort(
            string stableId,
            string name,
            SessionFlowPortDirection direction,
            SessionFlowPortType portType,
            bool isBooleanCondition = false,
            bool isTransparentOutput = false)
        {
            return new SessionFlowPortModel
            {
                Id = stableId,
                Name = name,
                Direction = direction,
                PortType = portType,
                IsBooleanCondition = isBooleanCondition,
                IsTransparentOutput = isTransparentOutput
            };
        }

        private static string CreateStableXmlPortId(string prefix, string path)
        {
            return $"{prefix}-{Convert.ToHexString(Encoding.UTF8.GetBytes(path))}";
        }

        private SessionFlowPortModel CreateBooleanXmlInput(string idSeed, string name)
        {
            return CreatePort(idSeed, name, SessionFlowPortDirection.Input, SessionFlowPortType.XmlField, isBooleanCondition: true);
        }

        private void ClearPendingConnection()
        {
            _pendingSourceNode = null;
            _pendingSourcePort = null;
            OnPropertyChanged(nameof(PendingConnectionText));
            CommandManager.InvalidateRequerySuggested();
        }

        private IDisposable SuspendPersistence()
        {
            _suspendPersistenceCounter++;
            return new DelegateDisposable(() => _suspendPersistenceCounter--);
        }
    }
}

