using System.Collections.ObjectModel;
using System.Windows.Input;
using Skyweaver.Controls.AgentConfigurationControl.ViewModels;
using Skyweaver.Controls.AgentConfigurationControl.Views;
using Skyweaver.Commands;
using Skyweaver.Controls.AgentWizardControl.ViewModels;
using Skyweaver.Controls.AgentWizardControl.Views;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.ViewModels;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Views;
using Skyweaver.Controls.FileManagerControl.ViewModels;
using Skyweaver.Controls.FileManagerControl.Views;
using Skyweaver.Controls.LanguageModelConfigurationControl.ViewModels;
using Skyweaver.Controls.LanguageModelConfigurationControl.Views;
using Skyweaver.Controls.LateralFileSystemTreeControl.ViewModels;
using Skyweaver.Controls.LateralFileSystemTreeControl.Views;
using Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels;
using Skyweaver.Controls.SkyweaverPreferencesControl.Views;
using Skyweaver.Controls.TextEditorControl.ViewModels;
using Skyweaver.Controls.TextEditorControl.Views;
using Skyweaver.Controls.ToolConfigurationControl.ViewModels;
using Skyweaver.Controls.ToolConfigurationControl.Views;
using Skyweaver.Controls.WorkflowEditorControl.ViewModels;
using Skyweaver.Controls.WorkflowEditorControl.Views;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Panels.DocumentWorkspace.Models;
using Skyweaver.Panels.DocumentWorkspace.ViewModels;
using Skyweaver.Panels.MultiFunctionArea.Models;

namespace Skyweaver.Panels.MultiFunctionArea.ViewModels
{
    public sealed class MultiFunctionAreaPanelViewModel : ObservableObject
    {
        private readonly Dictionary<string, MultiFunctionTabDefinition> _definitions;
        private readonly DocumentWorkspacePanelViewModel _workspaceHost = new();
        private WorkspaceDocument? _activeTab;

        public ObservableCollection<WorkspaceDocument> OpenedTabs => _workspaceHost.OpenedDocuments;

        public WorkspaceDocument? ActiveTab
        {
            get => _activeTab;
            set
            {
                if (SetProperty(ref _activeTab, value))
                {
                    _workspaceHost.ActiveDocument = value;
                }
            }
        }

        public IReadOnlyList<MultiFunctionTabDefinition> AvailableTabDefinitions { get; }

        public ICommand CloseTabCommand { get; }

        public ICommand CreateTabCommand { get; }

        public MultiFunctionAreaPanelViewModel()
        {
            CloseTabCommand = new RelayCommand<WorkspaceDocument>(CloseTab, tab => tab != null);
            CreateTabCommand = new RelayCommand<string>(CreateTabByType, CanCreateTabByType);

            _workspaceHost.DocumentClosed += HandleDocumentClosed;

            AvailableTabDefinitions = CreateDefinitions();
            _definitions = AvailableTabDefinitions.ToDictionary(definition => definition.TypeKey, definition => definition);
        }

        public WorkspaceDocument CreateTab(WorkspaceTabOptions options)
        {
            var tab = _workspaceHost.CreateAndOpenDocument(options);
            RefreshTypeNumbering(options.TabTypeKey);
            ActiveTab = tab;
            return tab;
        }

        public bool CanCreateTab(string typeKey)
        {
            return CanCreateTabByType(typeKey);
        }

        public static class TabTypes
        {
            public const string SkyweaverPreferences = "skyweaver-preferences";
            public const string WorkflowEditor = "session-flow-editor";
            public const string FileManager = "file-manager";
            public const string LateralFileSystemTree = "lateral-file-system-tree";
            public const string LanguageModelConfiguration = "language-model-configuration";
            public const string EmbeddingModelConfiguration = "embedding-model-configuration";
            public const string ToolConfiguration = "tool-configuration";
            public const string AgentConfiguration = "agent-configuration";
            public const string TextEditor = "text-editor";
            public const string AgentWizard = "agent-wizard";
        }

        private IReadOnlyList<MultiFunctionTabDefinition> CreateDefinitions()
        {
            return new List<MultiFunctionTabDefinition>
            {
                new()
                {
                    TypeKey = TabTypes.SkyweaverPreferences,
                    Title = "Skyweaver首选项",
                    Description = "管理 Skyweaver 的全局偏好设置、外观和默认行为。",
                    IconPath = "pack://application:,,,/Resources/Setup.png",
                    MaxCount = 1,
                    ContentFactory = _ => CreateSkyweaverPreferencesView()
                },
                new()
                {
                    TypeKey = TabTypes.WorkflowEditor,
                    Title = "会话流编辑器",
                    Description = "在全视图区域中编辑会话流、节点连接与流程状态。",
                    IconPath = "pack://application:,,,/Resources/Nodegraph.png",
                    MaxCount = 1,
                    ContentFactory = CreateWorkflowEditorView
                },
                new()
                {
                    TypeKey = TabTypes.FileManager,
                    Title = "文件管理器",
                    Description = "浏览、筛选和操作当前工程或素材目录中的文件。",
                    IconPath = "pack://application:,,,/Resources/WorkFolder.png",
                    MaxCount = 3,
                    ContentFactory = CreateFileManagerView
                },
                new()
                {
                    TypeKey = TabTypes.LateralFileSystemTree,
                    Title = "侧向文件系统树",
                    Description = "显示紧凑的目录树，适合作为导航或辅助浏览面板。",
                    IconPath = "pack://application:,,,/Resources/ResourcesLibrary.png",
                    MaxCount = 2,
                    ContentFactory = CreateLateralFileSystemTreeView
                },
                new()
                {
                    TypeKey = TabTypes.LanguageModelConfiguration,
                    Title = "语言模型配置",
                    Description = "配置模型来源、默认参数、上下文策略与调用预设。",
                    IconPath = "pack://application:,,,/Resources/Setup.png",
                    MaxCount = 1,
                    ContentFactory = _ => CreateLanguageModelConfigurationView()
                },
                new()
                {
                    TypeKey = TabTypes.EmbeddingModelConfiguration,
                    Title = "嵌入模型配置",
                    Description = "配置向量嵌入模型来源、维度、调用参数与连通性测试。",
                    IconPath = "pack://application:,,,/Resources/Setup.png",
                    MaxCount = 1,
                    ContentFactory = _ => CreateEmbeddingModelConfigurationView()
                },
                new()
                {
                    TypeKey = TabTypes.ToolConfiguration,
                    Title = "工具配置",
                    Description = "配置工具清单、启用状态、参数模板与运行策略。",
                    IconPath = "pack://application:,,,/Resources/WorkFolder.png",
                    MaxCount = 1,
                    ContentFactory = _ => CreateToolConfigurationView()
                },
                new()
                {
                    TypeKey = TabTypes.AgentConfiguration,
                    Title = "代理配置",
                    Description = "配置代理身份、职责、模型绑定与工具权限。",
                    IconPath = "pack://application:,,,/Resources/GuideBot.png",
                    MaxCount = 1,
                    ContentFactory = _ => CreateAgentConfigurationView()
                },
                new()
                {
                    TypeKey = TabTypes.TextEditor,
                    Title = "文本编辑器",
                    Description = "用于脚本、提示词或配置文件编辑的文本工作页。",
                    IconPath = "pack://application:,,,/Resources/Script.png",
                    MaxCount = 5,
                    ContentFactory = CreateTextEditorView
                },
                new()
                {
                    TypeKey = TabTypes.AgentWizard,
                    Title = "创建代理向导",
                    Description = "通过分步向导快速配置新的代理、职责和行为模板。",
                    IconPath = "pack://application:,,,/Resources/GuideBot.png",
                    MaxCount = 1,
                    ContentFactory = _ => CreateAgentWizardView()
                }
            };
        }

        private bool CanCreateTabByType(string? typeKey)
        {
            if (string.IsNullOrWhiteSpace(typeKey) || !_definitions.TryGetValue(typeKey, out var definition))
            {
                return false;
            }

            return OpenedTabs.Count(tab => tab.TabTypeKey == typeKey) < definition.MaxCount;
        }

        public void OpenOrActivateTab(string typeKey)
        {
            CreateTabByType(typeKey);
        }

        private void CreateTabByType(string? typeKey)
        {
            if (string.IsNullOrWhiteSpace(typeKey) || !_definitions.TryGetValue(typeKey, out var definition))
            {
                return;
            }

            if (!CanCreateTabByType(typeKey))
            {
                var existing = OpenedTabs.LastOrDefault(tab => tab.TabTypeKey == typeKey);
                if (existing != null)
                {
                    ActiveTab = existing;
                }

                return;
            }

            var instanceNumber = OpenedTabs.Count(tab => tab.TabTypeKey == typeKey) + 1;
            CreateTab(new WorkspaceTabOptions
            {
                DocumentKey = $"multi-function:{typeKey}:{Guid.NewGuid():N}",
                Title = definition.Title,
                Subtitle = definition.Description,
                IconPath = definition.IconPath,
                ContentViewModel = definition.ContentFactory(instanceNumber),
                PlaceholderText = $"Tab '{definition.Title}' has no content yet.",
                TabTypeKey = typeKey,
                InstanceNumber = instanceNumber
            });
        }

        private void CloseTab(WorkspaceDocument? tab)
        {
            if (tab == null)
            {
                return;
            }

            CloseTabCommandInternal(tab);
            ActiveTab = _workspaceHost.ActiveDocument;
        }

        private void CloseTabCommandInternal(WorkspaceDocument tab)
        {
            if (_workspaceHost.CloseDocumentCommand.CanExecute(tab))
            {
                _workspaceHost.CloseDocumentCommand.Execute(tab);
            }
        }

        private void RefreshTypeNumbering(string? typeKey)
        {
            if (string.IsNullOrWhiteSpace(typeKey))
            {
                return;
            }

            var sameTypeTabs = OpenedTabs.Where(tab => tab.TabTypeKey == typeKey).ToList();
            var showNumbers = sameTypeTabs.Count > 1;
            for (var index = 0; index < sameTypeTabs.Count; index++)
            {
                sameTypeTabs[index].InstanceNumber = index + 1;
                sameTypeTabs[index].RefreshDisplayTitle(showNumbers);
            }
        }

        private void HandleDocumentClosed(WorkspaceDocument document)
        {
            RefreshTypeNumbering(document.TabTypeKey);
        }

        private SkyweaverPreferencesControl CreateSkyweaverPreferencesView()
        {
            return new SkyweaverPreferencesControl
            {
                DataContext = new SkyweaverPreferencesControlViewModel(OpenOrActivateTab)
            };
        }

        private static WorkflowEditorControl CreateWorkflowEditorView(int instanceNumber)
        {
            return new WorkflowEditorControl
            {
                DataContext = new WorkflowEditorControlViewModel(instanceNumber)
            };
        }

        private static FileManagerControl CreateFileManagerView(int instanceNumber)
        {
            return new FileManagerControl
            {
                DataContext = new FileManagerControlViewModel(instanceNumber)
            };
        }

        private static LateralFileSystemTreeControl CreateLateralFileSystemTreeView(int instanceNumber)
        {
            Skyweaver.Services.LateralFileSystem.LateralFileSystemDebugConsole.Write("UI", $"CreateLateralFileSystemTreeView start; instanceNumber={instanceNumber}.");
            return new LateralFileSystemTreeControl
            {
                DataContext = new LateralFileSystemTreeControlViewModel(instanceNumber)
            };
        }

        private static LanguageModelConfigurationControl CreateLanguageModelConfigurationView()
        {
            return new LanguageModelConfigurationControl
            {
                DataContext = new LanguageModelConfigurationControlViewModel()
            };
        }

        private static EmbeddingModelConfigurationControl CreateEmbeddingModelConfigurationView()
        {
            return new EmbeddingModelConfigurationControl
            {
                DataContext = new EmbeddingModelConfigurationControlViewModel()
            };
        }

        private static ToolConfigurationControl CreateToolConfigurationView()
        {
            return new ToolConfigurationControl
            {
                DataContext = new ToolConfigurationControlViewModel()
            };
        }

        private static AgentConfigurationControl CreateAgentConfigurationView()
        {
            return new AgentConfigurationControl
            {
                DataContext = new AgentConfigurationControlViewModel()
            };
        }

        private static TextEditorControl CreateTextEditorView(int instanceNumber)
        {
            return new TextEditorControl
            {
                DataContext = new TextEditorControlViewModel(instanceNumber)
            };
        }

        private static AgentWizardControl CreateAgentWizardView()
        {
            return new AgentWizardControl
            {
                DataContext = new AgentWizardControlViewModel()
            };
        }
    }
}

