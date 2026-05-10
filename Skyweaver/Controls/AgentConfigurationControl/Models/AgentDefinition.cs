using System.Collections.ObjectModel;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.AgentConfigurationControl.Models
{
    public sealed class AgentDefinition : ObservableObject
    {
        public const string DefaultAvatarPath = "pack://application:,,,/Resources/GuideBot.png";
        public const string InputRootName = "Input";
        public const string OutputRootName = "Output";

        private string _avatarPath = DefaultAvatarPath;
        private string _displayName = string.Empty;
        private string _agentId = string.Empty;
        private string _systemPrompt = string.Empty;
        private bool _isStructuredXmlIO;
        private string _inputDescription = string.Empty;
        private string _outputDescription = string.Empty;
        private AgentRuntimeRole _runtimeRole = AgentRuntimeRole.MainOnly;
        private string _subAgentIntroduction = string.Empty;
        private AgentLanguageModelSelectionMode _languageModelSelectionMode = AgentLanguageModelSelectionMode.SpecificLanguageModel;
        private string _selectedLanguageModelKey = string.Empty;
        private string _selectedCapabilityLayerKey = string.Empty;

        public AgentDefinition()
        {
            InputSchemaRoot = new XmlElementNodeDefinition(InputRootName, isRoot: true);
            OutputSchemaRoot = new XmlElementNodeDefinition(OutputRootName, isRoot: true);
        }

        public string AvatarPath
        {
            get => _avatarPath;
            set
            {
                if (SetProperty(ref _avatarPath, string.IsNullOrWhiteSpace(value) ? DefaultAvatarPath : value.Trim()))
                {
                    OnPropertyChanged(nameof(AvatarPreviewPath));
                }
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetProperty(ref _displayName, value?.Trim() ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayNameOrFallback));
                }
            }
        }

        public string AgentId
        {
            get => _agentId;
            set
            {
                if (SetProperty(ref _agentId, value?.Trim() ?? string.Empty))
                {
                    OnPropertyChanged(nameof(AgentIdOrFallback));
                }
            }
        }

        public string SystemPrompt
        {
            get => _systemPrompt;
            set => SetProperty(ref _systemPrompt, value ?? string.Empty);
        }

        public bool IsStructuredXmlIO
        {
            get => _isStructuredXmlIO;
            set
            {
                if (SetProperty(ref _isStructuredXmlIO, value))
                {
                    OnPropertyChanged(nameof(StructuredModeText));
                }
            }
        }

        public string InputDescription
        {
            get => _inputDescription;
            set => SetProperty(ref _inputDescription, value ?? string.Empty);
        }

        public string OutputDescription
        {
            get => _outputDescription;
            set => SetProperty(ref _outputDescription, value ?? string.Empty);
        }

        public AgentRuntimeRole RuntimeRole
        {
            get => _runtimeRole;
            set
            {
                if (SetProperty(ref _runtimeRole, value))
                {
                    OnPropertyChanged(nameof(CanRunAsMainAgent));
                    OnPropertyChanged(nameof(CanRunAsSubAgent));
                    OnPropertyChanged(nameof(RuntimeRoleText));
                }
            }
        }

        public string SubAgentIntroduction
        {
            get => _subAgentIntroduction;
            set => SetProperty(ref _subAgentIntroduction, value ?? string.Empty);
        }

        public AgentLanguageModelSelectionMode LanguageModelSelectionMode
        {
            get => _languageModelSelectionMode;
            set => SetProperty(ref _languageModelSelectionMode, value);
        }

        public string SelectedLanguageModelKey
        {
            get => _selectedLanguageModelKey;
            set => SetProperty(ref _selectedLanguageModelKey, value?.Trim() ?? string.Empty);
        }

        public string SelectedCapabilityLayerKey
        {
            get => _selectedCapabilityLayerKey;
            set => SetProperty(ref _selectedCapabilityLayerKey, value?.Trim() ?? string.Empty);
        }

        public ObservableCollection<AgentToolPermissionDefinition> ToolPermissions { get; } = new();

        public ObservableCollection<AgentToolKitSelectionDefinition> DefaultToolKits { get; } = new();

        public XmlElementNodeDefinition InputSchemaRoot { get; }

        public XmlElementNodeDefinition OutputSchemaRoot { get; }

        public string AvatarPreviewPath => string.IsNullOrWhiteSpace(AvatarPath) ? DefaultAvatarPath : AvatarPath;

        public string DisplayNameOrFallback => string.IsNullOrWhiteSpace(DisplayName) ? "（未命名代理）" : DisplayName;

        public string AgentIdOrFallback => string.IsNullOrWhiteSpace(AgentId) ? "（缺少 ID）" : AgentId;

        public string StructuredModeText => IsStructuredXmlIO ? "结构化 XML" : "自然语言";

        public bool CanRunAsMainAgent => RuntimeRole is AgentRuntimeRole.MainOnly or AgentRuntimeRole.MainAndSubAgent;

        public bool CanRunAsSubAgent => RuntimeRole is AgentRuntimeRole.SubAgentOnly or AgentRuntimeRole.MainAndSubAgent;

        public string RuntimeRoleText => RuntimeRole switch
        {
            AgentRuntimeRole.SubAgentOnly => "仅子代理",
            AgentRuntimeRole.MainAndSubAgent => "主/子代理",
            _ => "仅主代理"
        };

        public AgentDefinition DeepClone()
        {
            var clone = new AgentDefinition
            {
                AvatarPath = AvatarPath,
                DisplayName = DisplayName,
                AgentId = AgentId,
                SystemPrompt = SystemPrompt,
                IsStructuredXmlIO = IsStructuredXmlIO,
                InputDescription = InputDescription,
                OutputDescription = OutputDescription,
                RuntimeRole = RuntimeRole,
                SubAgentIntroduction = SubAgentIntroduction,
                LanguageModelSelectionMode = LanguageModelSelectionMode,
                SelectedLanguageModelKey = SelectedLanguageModelKey,
                SelectedCapabilityLayerKey = SelectedCapabilityLayerKey
            };

            foreach (var toolKit in DefaultToolKits)
            {
                clone.DefaultToolKits.Add(new AgentToolKitSelectionDefinition
                {
                    ToolKitKey = toolKit.ToolKitKey
                });
            }

            foreach (var toolPermission in ToolPermissions)
            {
                clone.ToolPermissions.Add(new AgentToolPermissionDefinition
                {
                    ToolName = toolPermission.ToolName,
                    Permission = toolPermission.Permission,
                    ToolDescription = toolPermission.ToolDescription,
                    IsMissing = toolPermission.IsMissing,
                    IsGloballyEnabled = toolPermission.IsGloballyEnabled
                });
            }

            foreach (var child in InputSchemaRoot.Children)
            {
                clone.InputSchemaRoot.AddChild(child.DeepClone());
            }

            foreach (var child in OutputSchemaRoot.Children)
            {
                clone.OutputSchemaRoot.AddChild(child.DeepClone());
            }

            return clone;
        }
    }
}
