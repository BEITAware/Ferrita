using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;

namespace InstallationWizard
{
    public enum AgentLanguageModelSelectionMode
    {
        SpecificLanguageModel = 0,
        CapabilityLayer = 1
    }

    public class AgentConfigOption : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AgentId { get; set; } = string.Empty;
        
        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        public string SystemPrompt { get; set; } = string.Empty;

        private string _description = string.Empty;
        public string Description
        {
            get => _description;
            set { if (_description != value) { _description = value; OnPropertyChanged(); } }
        }
        public string AvatarPath { get; set; } = "pack://application:,,,/Resources/GuideBot.png";
        public XElement? RawElement { get; set; }

        private AgentLanguageModelSelectionMode _languageModelSelectionMode = AgentLanguageModelSelectionMode.SpecificLanguageModel;
        public AgentLanguageModelSelectionMode LanguageModelSelectionMode
        {
            get => _languageModelSelectionMode;
            set
            {
                if (_languageModelSelectionMode != value)
                {
                    _languageModelSelectionMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSpecificLanguageModel));
                    OnPropertyChanged(nameof(IsCapabilityLayer));
                }
            }
        }

        private string _selectedLanguageModelKey = "default-openai";
        public string SelectedLanguageModelKey
        {
            get => _selectedLanguageModelKey;
            set
            {
                if (_selectedLanguageModelKey != value)
                {
                    _selectedLanguageModelKey = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _selectedCapabilityLayerKey = "builtin.utility-ii-smart";
        public string SelectedCapabilityLayerKey
        {
            get => _selectedCapabilityLayerKey;
            set
            {
                if (_selectedCapabilityLayerKey != value)
                {
                    _selectedCapabilityLayerKey = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSpecificLanguageModel
        {
            get => _languageModelSelectionMode == AgentLanguageModelSelectionMode.SpecificLanguageModel;
            set
            {
                if (value)
                {
                    LanguageModelSelectionMode = AgentLanguageModelSelectionMode.SpecificLanguageModel;
                }
            }
        }

        public bool IsCapabilityLayer
        {
            get => _languageModelSelectionMode == AgentLanguageModelSelectionMode.CapabilityLayer;
            set
            {
                if (value)
                {
                    LanguageModelSelectionMode = AgentLanguageModelSelectionMode.CapabilityLayer;
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public void RaisePropertyChanged(string propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }

    public class SessionFlowConfigOption : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AgentId { get; set; } = string.Empty;
        
        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        private string _description = string.Empty;
        public string Description
        {
            get => _description;
            set { if (_description != value) { _description = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public void RaisePropertyChanged(string propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }

    public class WizardLanguageModelDefinition : INotifyPropertyChanged
    {
        private string _key = Guid.NewGuid().ToString("N");
        private string _displayName = string.Empty;
        private string _interfaceType = "OpenAI Chat Completions API";
        private int _contextWindowTokens = 200000;
        private bool _enableImageInput = true;
        private bool _enableAudioInput = true;
        private bool _enableVideoInput = true;
        private bool _enableDocumentInput = true;
        private WizardGoogleSettings _googleSettings = new WizardGoogleSettings();
        private WizardOpenAiSettings _openaiSettings = new WizardOpenAiSettings();

        public string Key
        {
            get => _key;
            set { _key = value; OnPropertyChanged(); }
        }

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public string InterfaceType
        {
            get => _interfaceType;
            set 
            { 
                if (_interfaceType != value)
                {
                    _interfaceType = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(InterfaceSettings));
                }
            }
        }

        public int ContextWindowTokens
        {
            get => _contextWindowTokens;
            set { _contextWindowTokens = value; OnPropertyChanged(); }
        }

        public bool EnableImageInput
        {
            get => _enableImageInput;
            set { _enableImageInput = value; OnPropertyChanged(); }
        }

        public bool EnableAudioInput
        {
            get => _enableAudioInput;
            set { _enableAudioInput = value; OnPropertyChanged(); }
        }

        public bool EnableVideoInput
        {
            get => _enableVideoInput;
            set { _enableVideoInput = value; OnPropertyChanged(); }
        }

        public bool EnableDocumentInput
        {
            get => _enableDocumentInput;
            set { _enableDocumentInput = value; OnPropertyChanged(); }
        }

        public WizardGoogleSettings GoogleSettings
        {
            get => _googleSettings;
            set { _googleSettings = value; OnPropertyChanged(); }
        }

        public WizardOpenAiSettings OpenAiSettings
        {
            get => _openaiSettings;
            set { _openaiSettings = value; OnPropertyChanged(); }
        }

        public object InterfaceSettings => string.Equals(InterfaceType, "Google", StringComparison.OrdinalIgnoreCase) ? (object)GoogleSettings : (object)OpenAiSettings;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class WizardGoogleSettings : INotifyPropertyChanged
    {
        private string _modelId = string.Empty;
        private string _apiKey = string.Empty;
        private string _baseUrl = string.Empty;
        private bool _useTemperature = false;
        private decimal _temperature = 1.0m;
        private bool _useTopP = false;
        private decimal _topP = 0.95m;
        private bool _useMaxOutputTokens = false;
        private int _maxOutputTokens = 2048;
        private bool _useThinkingLevel = false;
        private string _thinkingLevel = "High";
        private bool _useThinkingBudget = false;
        private int _thinkingBudget = -1;
        private bool _includeThoughts = true;

        public string ModelId { get => _modelId; set { _modelId = value; OnPropertyChanged(); } }
        public string ApiKey { get => _apiKey; set { _apiKey = value; OnPropertyChanged(); } }
        public string BaseUrl { get => _baseUrl; set { _baseUrl = value; OnPropertyChanged(); } }
        public bool UseTemperature { get => _useTemperature; set { _useTemperature = value; OnPropertyChanged(); } }
        public decimal Temperature { get => _temperature; set { _temperature = value; OnPropertyChanged(); } }
        public bool UseTopP { get => _useTopP; set { _useTopP = value; OnPropertyChanged(); } }
        public decimal TopP { get => _topP; set { _topP = value; OnPropertyChanged(); } }
        public bool UseMaxOutputTokens { get => _useMaxOutputTokens; set { _useMaxOutputTokens = value; OnPropertyChanged(); } }
        public int MaxOutputTokens { get => _maxOutputTokens; set { _maxOutputTokens = value; OnPropertyChanged(); } }
        public bool UseThinkingLevel { get => _useThinkingLevel; set { _useThinkingLevel = value; OnPropertyChanged(); } }
        public string ThinkingLevel { get => _thinkingLevel; set { _thinkingLevel = value; OnPropertyChanged(); } }
        public bool UseThinkingBudget { get => _useThinkingBudget; set { _useThinkingBudget = value; OnPropertyChanged(); } }
        public int ThinkingBudget { get => _thinkingBudget; set { _thinkingBudget = value; OnPropertyChanged(); } }
        public bool IncludeThoughts { get => _includeThoughts; set { _includeThoughts = value; OnPropertyChanged(); } }

        public string[] SupportedThinkingLevels => new[] { "Low", "Medium", "High" };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class WizardOpenAiSettings : INotifyPropertyChanged
    {
        private string _modelId = string.Empty;
        private string _apiKey = string.Empty;
        private string _baseUrl = string.Empty;
        private bool _useTemperature = false;
        private decimal _temperature = 1.0m;
        private bool _useTopP = false;
        private decimal _topP = 1.0m;
        private bool _useMaxOutputTokens = false;
        private int _maxOutputTokens = 2048;
        private bool _usePresencePenalty = false;
        private decimal _presencePenalty = 0m;
        private bool _useFrequencyPenalty = false;
        private decimal _frequencyPenalty = 0m;
        private bool _useSeed = false;
        private long _seed = 0L;
        private bool _useReasoningEffort = false;
        private string _reasoningEffort = "Medium";
        private bool _useReasoningOutput = true;
        private string _reasoningOutput = "Full";

        public string ModelId { get => _modelId; set { _modelId = value; OnPropertyChanged(); } }
        public string ApiKey { get => _apiKey; set { _apiKey = value; OnPropertyChanged(); } }
        public string BaseUrl { get => _baseUrl; set { _baseUrl = value; OnPropertyChanged(); } }
        public bool UseTemperature { get => _useTemperature; set { _useTemperature = value; OnPropertyChanged(); } }
        public decimal Temperature { get => _temperature; set { _temperature = value; OnPropertyChanged(); } }
        public bool UseTopP { get => _useTopP; set { _useTopP = value; OnPropertyChanged(); } }
        public decimal TopP { get => _topP; set { _topP = value; OnPropertyChanged(); } }
        public bool UseMaxOutputTokens { get => _useMaxOutputTokens; set { _useMaxOutputTokens = value; OnPropertyChanged(); } }
        public int MaxOutputTokens { get => _maxOutputTokens; set { _maxOutputTokens = value; OnPropertyChanged(); } }
        public bool UsePresencePenalty { get => _usePresencePenalty; set { _usePresencePenalty = value; OnPropertyChanged(); } }
        public decimal PresencePenalty { get => _presencePenalty; set { _presencePenalty = value; OnPropertyChanged(); } }
        public bool UseFrequencyPenalty { get => _useFrequencyPenalty; set { _useFrequencyPenalty = value; OnPropertyChanged(); } }
        public decimal FrequencyPenalty { get => _frequencyPenalty; set { _frequencyPenalty = value; OnPropertyChanged(); } }
        public bool UseSeed { get => _useSeed; set { _useSeed = value; OnPropertyChanged(); } }
        public long Seed { get => _seed; set { _seed = value; OnPropertyChanged(); } }
        public bool UseReasoningEffort { get => _useReasoningEffort; set { _useReasoningEffort = value; OnPropertyChanged(); } }
        public string ReasoningEffort { get => _reasoningEffort; set { _reasoningEffort = value; OnPropertyChanged(); } }
        public bool UseReasoningOutput { get => _useReasoningOutput; set { _useReasoningOutput = value; OnPropertyChanged(); } }
        public string ReasoningOutput { get => _reasoningOutput; set { _reasoningOutput = value; OnPropertyChanged(); } }

        public string[] SupportedReasoningEfforts => new[] { "Low", "Medium", "High" };
        public string[] SupportedReasoningOutputs => new[] { "None", "ReasoningOnly", "Full" };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class WizardCapabilityLayerDefinition : INotifyPropertyChanged
    {
        private string _key = string.Empty;
        private string _name = string.Empty;
        private ObservableCollection<WizardCapabilityLayerEntry> _languageModels = new ObservableCollection<WizardCapabilityLayerEntry>();

        public string Key { get => _key; set { _key = value; OnPropertyChanged(); } }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public ObservableCollection<WizardCapabilityLayerEntry> LanguageModels { get => _languageModels; set { _languageModels = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public void RaisePropertyChanged(string propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }

    public class WizardCapabilityLayerEntry : INotifyPropertyChanged
    {
        private string _languageModelKey = string.Empty;

        public string LanguageModelKey 
        { 
            get => _languageModelKey; 
            set { _languageModelKey = value; OnPropertyChanged(); } 
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private int _currentStep = 1;
        private int _progressVal = 0;
        private bool _installationStarted;
        private string? _installedExecutablePath;

        private ObservableCollection<WizardLanguageModelDefinition> _wizardLanguageModels = new ObservableCollection<WizardLanguageModelDefinition>();
        public ObservableCollection<WizardLanguageModelDefinition> WizardLanguageModels
        {
            get => _wizardLanguageModels;
            set
            {
                if (_wizardLanguageModels != value)
                {
                    _wizardLanguageModels = value;
                    OnPropertyChanged();
                }
            }
        }

        private WizardLanguageModelDefinition? _selectedWizardLanguageModel;
        public WizardLanguageModelDefinition? SelectedWizardLanguageModel
        {
            get => _selectedWizardLanguageModel;
            set
            {
                if (_selectedWizardLanguageModel != value)
                {
                    _selectedWizardLanguageModel = value;
                    OnPropertyChanged();
                }
            }
        }

        private ObservableCollection<WizardCapabilityLayerDefinition> _capabilityLayers = new ObservableCollection<WizardCapabilityLayerDefinition>();
        public ObservableCollection<WizardCapabilityLayerDefinition> CapabilityLayers
        {
            get => _capabilityLayers;
            set
            {
                if (_capabilityLayers != value)
                {
                    _capabilityLayers = value;
                    OnPropertyChanged();
                }
            }
        }

        private ObservableCollection<AgentConfigOption> _agentOptions = new ObservableCollection<AgentConfigOption>();
        public ObservableCollection<AgentConfigOption> AgentOptions
        {
            get => _agentOptions;
            set
            {
                if (_agentOptions != value)
                {
                    _agentOptions = value;
                    OnPropertyChanged();
                }
            }
        }

        private ObservableCollection<SessionFlowConfigOption> _flowOptions = new ObservableCollection<SessionFlowConfigOption>();
        public ObservableCollection<SessionFlowConfigOption> FlowOptions
        {
            get => _flowOptions;
            set
            {
                if (_flowOptions != value)
                {
                    _flowOptions = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isLfsEnabled = true;
        public bool IsLfsEnabled
        {
            get => _isLfsEnabled;
            set
            {
                if (_isLfsEnabled != value)
                {
                    _isLfsEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _lfsWorkingDir = string.Empty;
        public string LfsWorkingDir
        {
            get => _lfsWorkingDir;
            set
            {
                if (_lfsWorkingDir != value)
                {
                    _lfsWorkingDir = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private const string DefaultAgentsXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<AgentConfigurations SchemaVersion=""3"">
  <Agent AgentId=""NiceChatter"">
    <DisplayName>NiceChatter</DisplayName>
    <AvatarPath>pack://application:,,,/Resources/GuideBot.png</AvatarPath>
    <SystemPrompt>你是一个友善的助理，帮助用户完成聊天与完成各项任务。</SystemPrompt>
    <IsStructuredXmlIO>false</IsStructuredXmlIO>
    <InputDescription>用户发送的聊天消息</InputDescription>
    <OutputDescription>无，无需调用Passdown工具</OutputDescription>
    <RuntimeRole>MainOnly</RuntimeRole>
    <SubAgentIntroduction></SubAgentIntroduction>
    <LanguageModelSelectionMode>SpecificLanguageModel</LanguageModelSelectionMode>
    <SelectedLanguageModelKey></SelectedLanguageModelKey>
    <SelectedCapabilityLayerKey></SelectedCapabilityLayerKey>
    <IsPersonaEnabled>false</IsPersonaEnabled>
    <SelectedPersonaId>aero</SelectedPersonaId>
    <DefaultToolKits>
      <ToolKit Key=""multimodal"" />
    </DefaultToolKits>
    <ToolPermissions>
      <Tool Name=""CheckPlanItem"" Permission=""RequireConfirmation"" />
      <Tool Name=""CreateLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""Curl"" Permission=""RequireConfirmation"" />
      <Tool Name=""EditFile_Advanced"" Permission=""RequireConfirmation"" />
      <Tool Name=""EditFile_Rewrite"" Permission=""RequireConfirmation"" />
      <Tool Name=""EditFile_SearchReplace"" Permission=""RequireConfirmation"" />
      <Tool Name=""EditPlan"" Permission=""RequireConfirmation"" />
      <Tool Name=""ExecutePowershellCommand"" Permission=""RequireConfirmation"" />
      <Tool Name=""FileSystemIO_CreateFile"" Permission=""RequireConfirmation"" />
      <Tool Name=""FileSystemIO_DeleteDirectory"" Permission=""RequireConfirmation"" />
      <Tool Name=""FileSystemIO_DeleteFile"" Permission=""RequireConfirmation"" />
      <Tool Name=""FileSystemIO_NewDirectory"" Permission=""RequireConfirmation"" />
      <Tool Name=""FileSystemIO_RenameFile"" Permission=""RequireConfirmation"" />
      <Tool Name=""GlobSearch"" Permission=""RequireConfirmation"" />
      <Tool Name=""GrepSearch"" Permission=""RequireConfirmation"" />
      <Tool Name=""InheritLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""InitializeAerialCityRAG"" Permission=""RequireConfirmation"" />
      <Tool Name=""InitializeLiveXAML"" Permission=""RequireConfirmation"" />
      <Tool Name=""InitializePlan"" Permission=""RequireConfirmation"" />
      <Tool Name=""KeywordSearch"" Permission=""RequireConfirmation"" />
      <Tool Name=""LoadToolKits"" Permission=""RequireConfirmation"" />
      <Tool Name=""MergeLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""OverwriteLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadDirectoryRecursive"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadImages"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadTextFile"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadThumbnails"" Permission=""RequireConfirmation"" />
      <Tool Name=""SemanticSearch"" Permission=""RequireConfirmation"" />
      <Tool Name=""SharpSpray"" Permission=""RequireConfirmation"" />
      <Tool Name=""ShowLiveXAML"" Permission=""RequireConfirmation"" />
      <Tool Name=""FerritaContextSnapshot"" Permission=""RequireConfirmation"" />
      <Tool Name=""SpawnSubAgent"" Permission=""RequireConfirmation"" />
      <Tool Name=""SWSniff"" Permission=""RequireConfirmation"" />
      <Tool Name=""UpdateAerialCityDB"" Permission=""RequireConfirmation"" />
      <Tool Name=""WebBrowse"" Permission=""RequireConfirmation"" />
      <Tool Name=""WebSearch"" Permission=""RequireConfirmation"" />
      <Tool Name=""Wget"" Permission=""RequireConfirmation"" />
      <Tool Name=""WorkspaceNoteTemplate"" Permission=""RequireConfirmation"" />
    </ToolPermissions>
    <InputSchema />
    <OutputSchema />
  </Agent>
  <Agent AgentId=""Coder"">
    <DisplayName>Coder</DisplayName>
    <AvatarPath>pack://application:,,,/Resources/GuideBot.png</AvatarPath>
    <SystemPrompt>系统指令
你是 Ferrita，一个人工智能代理。你和用户共享一个工作区，你的职责是与他们协作，直到他们的目标得到真正的解决。
通用指南：
你将资深工程师的判断力带入工作，但这种判断是建立在敏锐的观察之上，而非过早下定论。你要先阅读代码库，克制简单盲目的假设，让既有系统的架构引导你如何行动。
你更倾向于使用代码库中现有的模式、框架 and 本地辅助 API，而不是自己发明一套全新的抽象风格。
只有当抽象能够消除真正的复杂性、减少有意义的重复，或明显符合既有的本地模式时，你才会引入它。
你应让测试覆盖率随着风险和影响范围的大小而调整：对于局部的微调，保持测试的聚焦；当实现涉及共享行为、跨模块契约或面向用户的业务流时，则需要扩大测试覆盖范围。
你尽可能多调用工具，少直接输出自然语言。只有和用户交互或交付成果时使用自然语言。</SystemPrompt>
    <IsStructuredXmlIO>false</IsStructuredXmlIO>
    <InputDescription>用户发送的聊天消息</InputDescription>
    <OutputDescription>无，无需调用Passdown工具</OutputDescription>
    <RuntimeRole>MainOnly</RuntimeRole>
    <SubAgentIntroduction></SubAgentIntroduction>
    <LanguageModelSelectionMode>SpecificLanguageModel</LanguageModelSelectionMode>
    <SelectedLanguageModelKey></SelectedLanguageModelKey>
    <SelectedCapabilityLayerKey></SelectedCapabilityLayerKey>
    <IsPersonaEnabled>false</IsPersonaEnabled>
    <SelectedPersonaId>aero</SelectedPersonaId>
    <DefaultToolKits>
      <ToolKit Key=""Investigate"" />
      <ToolKit Key=""Web"" />
    </DefaultToolKits>
    <ToolPermissions>
      <Tool Name=""CheckPlanItem"" Permission=""Allow"" />
      <Tool Name=""CreateLateralFS"" Permission=""Allow"" />
      <Tool Name=""Curl"" Permission=""Allow"" />
      <Tool Name=""EditFile_Advanced"" Permission=""Allow"" />
      <Tool Name=""EditFile_Rewrite"" Permission=""Allow"" />
      <Tool Name=""EditFile_SearchReplace"" Permission=""Allow"" />
      <Tool Name=""EditPlan"" Permission=""Allow"" />
      <Tool Name=""ExecutePowershellCommand"" Permission=""Disabled"" />
      <Tool Name=""FileSystemIO_CreateFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_NewDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_RenameFile"" Permission=""Allow"" />
      <Tool Name=""GlobSearch"" Permission=""Allow"" />
      <Tool Name=""GrepSearch"" Permission=""Allow"" />
      <Tool Name=""InheritLateralFS"" Permission=""Allow"" />
      <Tool Name=""InitializeAerialCityRAG"" Permission=""Allow"" />
      <Tool Name=""InitializeLiveXAML"" Permission=""Allow"" />
      <Tool Name=""InitializePlan"" Permission=""Allow"" />
      <Tool Name=""KeywordSearch"" Permission=""Allow"" />
      <Tool Name=""LoadToolKits"" Permission=""Allow"" />
      <Tool Name=""MergeLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""OverwriteLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadDirectoryRecursive"" Permission=""Allow"" />
      <Tool Name=""ReadImages"" Permission=""Allow"" />
      <Tool Name=""ReadTextFile"" Permission=""Allow"" />
      <Tool Name=""ReadThumbnails"" Permission=""Allow"" />
      <Tool Name=""SemanticSearch"" Permission=""Allow"" />
      <Tool Name=""SharpSpray"" Permission=""Allow"" />
      <Tool Name=""ShowLiveXAML"" Permission=""Allow"" />
      <Tool Name=""FerritaContextSnapshot"" Permission=""Allow"" />
      <Tool Name=""SpawnSubAgent"" Permission=""Allow"" />
      <Tool Name=""SWSniff"" Permission=""Allow"" />
      <Tool Name=""UpdateAerialCityDB"" Permission=""Allow"" />
      <Tool Name=""WebBrowse"" Permission=""Allow"" />
      <Tool Name=""WebSearch"" Permission=""Allow"" />
      <Tool Name=""Wget"" Permission=""Allow"" />
      <Tool Name=""WorkspaceNoteTemplate"" Permission=""Allow"" />
    </ToolPermissions>
    <InputSchema />
    <OutputSchema />
  </Agent>
  <Agent AgentId=""CoderWithPowershell"">
    <DisplayName>Coder With Powershell</DisplayName>
    <AvatarPath>pack://application:,,,/Resources/GuideBot.png</AvatarPath>
    <SystemPrompt>系统指令
你是 Ferrita，一个人工智能代理。你和用户共享一个工作区，你的职责是与他们协作，直到他们的目标得到真正的解决。
通用指南：
你将资深工程师的判断力带入工作，但这种判断是建立在敏锐的观察之上，而非过早下定论。你要先阅读代码库，克制简单盲目的假设，让既有系统的架构引导你如何行动。
你更倾向于使用代码库中现有的模式、框架和本地辅助 API，而不是自己发明一套全新的抽象风格。
只有当抽象能够消除真正的复杂性、减少有意义的重复，或明显符合既有的本地模式时，你才会引入它。
你应让测试覆盖率随着风险 and 影响范围的大小而调整：对于局部的微调，保持测试的聚焦；当实现涉及共享行为、跨模块契约或面向用户的业务流时，则需要扩大测试覆盖范围。
你尽可能多调用工具，少直接输出自然语言。只有和用户交互或交付成果时使用自然语言。
你可以执行Powershell命令，但是，你必须优先使用应用程序内已有的内建工具。只有确保这些工具无法满足任务要求，才考虑使用Powershell。</SystemPrompt>
    <IsStructuredXmlIO>false</IsStructuredXmlIO>
    <InputDescription>用户发送的聊天消息</InputDescription>
    <OutputDescription>无，无需调用Passdown工具</OutputDescription>
    <RuntimeRole>MainOnly</RuntimeRole>
    <SubAgentIntroduction></SubAgentIntroduction>
    <LanguageModelSelectionMode>SpecificLanguageModel</LanguageModelSelectionMode>
    <SelectedLanguageModelKey></SelectedLanguageModelKey>
    <SelectedCapabilityLayerKey></SelectedCapabilityLayerKey>
    <IsPersonaEnabled>false</IsPersonaEnabled>
    <SelectedPersonaId>aero</SelectedPersonaId>
    <DefaultToolKits>
      <ToolKit Key=""Investigate"" />
      <ToolKit Key=""Web"" />
    </DefaultToolKits>
    <ToolPermissions>
      <Tool Name=""CheckPlanItem"" Permission=""Allow"" />
      <Tool Name=""CreateLateralFS"" Permission=""Allow"" />
      <Tool Name=""Curl"" Permission=""Allow"" />
      <Tool Name=""EditFile_Advanced"" Permission=""Allow"" />
      <Tool Name=""EditFile_Rewrite"" Permission=""Allow"" />
      <Tool Name=""EditFile_SearchReplace"" Permission=""Allow"" />
      <Tool Name=""EditPlan"" Permission=""Allow"" />
      <Tool Name=""ExecutePowershellCommand"" Permission=""RequireConfirmation"" />
      <Tool Name=""FileSystemIO_CreateFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_NewDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_RenameFile"" Permission=""Allow"" />
      <Tool Name=""GlobSearch"" Permission=""Allow"" />
      <Tool Name=""GrepSearch"" Permission=""Allow"" />
      <Tool Name=""InheritLateralFS"" Permission=""Allow"" />
      <Tool Name=""InitializeAerialCityRAG"" Permission=""Allow"" />
      <Tool Name=""InitializeLiveXAML"" Permission=""Allow"" />
      <Tool Name=""InitializePlan"" Permission=""Allow"" />
      <Tool Name=""KeywordSearch"" Permission=""Allow"" />
      <Tool Name=""LoadToolKits"" Permission=""Allow"" />
      <Tool Name=""MergeLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""OverwriteLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadDirectoryRecursive"" Permission=""Allow"" />
      <Tool Name=""ReadImages"" Permission=""Allow"" />
      <Tool Name=""ReadTextFile"" Permission=""Allow"" />
      <Tool Name=""ReadThumbnails"" Permission=""Allow"" />
      <Tool Name=""SemanticSearch"" Permission=""Allow"" />
      <Tool Name=""SharpSpray"" Permission=""Allow"" />
      <Tool Name=""ShowLiveXAML"" Permission=""Allow"" />
      <Tool Name=""FerritaContextSnapshot"" Permission=""Allow"" />
      <Tool Name=""SpawnSubAgent"" Permission=""Allow"" />
      <Tool Name=""SWSniff"" Permission=""Allow"" />
      <Tool Name=""UpdateAerialCityDB"" Permission=""Allow"" />
      <Tool Name=""WebBrowse"" Permission=""Allow"" />
      <Tool Name=""WebSearch"" Permission=""Allow"" />
      <Tool Name=""Wget"" Permission=""Allow"" />
      <Tool Name=""WorkspaceNoteTemplate"" Permission=""Allow"" />
    </ToolPermissions>
    <InputSchema />
    <OutputSchema />
  </Agent>
  <Agent AgentId=""Puterperson"">
    <DisplayName>Puterperson</DisplayName>
    <AvatarPath>pack://application:,,,/Resources/GuideBot.png</AvatarPath>
    <SystemPrompt>你是用户计算机的化身。你帮助用户操作计算机并完成任务。</SystemPrompt>
    <IsStructuredXmlIO>false</IsStructuredXmlIO>
    <InputDescription>用户发送的聊天消息</InputDescription>
    <OutputDescription>无，无需调用Passdown工具</OutputDescription>
    <RuntimeRole>MainOnly</RuntimeRole>
    <SubAgentIntroduction></SubAgentIntroduction>
    <LanguageModelSelectionMode>SpecificLanguageModel</LanguageModelSelectionMode>
    <SelectedLanguageModelKey></SelectedLanguageModelKey>
    <SelectedCapabilityLayerKey></SelectedCapabilityLayerKey>
    <IsPersonaEnabled>true</IsPersonaEnabled>
    <SelectedPersonaId>aero</SelectedPersonaId>
    <DefaultToolKits>
      <ToolKit Key=""Investigate"" />
      <ToolKit Key=""Web"" />
      <ToolKit Key=""multimodal"" />
    </DefaultToolKits>
    <ToolPermissions>
      <Tool Name=""CheckPlanItem"" Permission=""Allow"" />
      <Tool Name=""CreateLateralFS"" Permission=""Allow"" />
      <Tool Name=""Curl"" Permission=""Allow"" />
      <Tool Name=""EditFile_Advanced"" Permission=""Allow"" />
      <Tool Name=""EditFile_Rewrite"" Permission=""Allow"" />
      <Tool Name=""EditFile_SearchReplace"" Permission=""Allow"" />
      <Tool Name=""EditPlan"" Permission=""Allow"" />
      <Tool Name=""ExecutePowershellCommand"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_CreateFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_NewDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_RenameFile"" Permission=""Allow"" />
      <Tool Name=""GlobSearch"" Permission=""Allow"" />
      <Tool Name=""GrepSearch"" Permission=""Allow"" />
      <Tool Name=""InheritLateralFS"" Permission=""Allow"" />
      <Tool Name=""InitializeAerialCityRAG"" Permission=""Allow"" />
      <Tool Name=""InitializeLiveXAML"" Permission=""Allow"" />
      <Tool Name=""InitializePlan"" Permission=""Allow"" />
      <Tool Name=""KeywordSearch"" Permission=""Allow"" />
      <Tool Name=""LoadToolKits"" Permission=""Allow"" />
      <Tool Name=""MergeLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""OverwriteLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadDirectoryRecursive"" Permission=""Allow"" />
      <Tool Name=""ReadImages"" Permission=""Allow"" />
      <Tool Name=""ReadTextFile"" Permission=""Allow"" />
      <Tool Name=""ReadThumbnails"" Permission=""Allow"" />
      <Tool Name=""SemanticSearch"" Permission=""Allow"" />
      <Tool Name=""SharpSpray"" Permission=""Allow"" />
      <Tool Name=""ShowLiveXAML"" Permission=""Allow"" />
      <Tool Name=""FerritaContextSnapshot"" Permission=""Allow"" />
      <Tool Name=""SpawnSubAgent"" Permission=""Allow"" />
      <Tool Name=""SWSniff"" Permission=""Allow"" />
      <Tool Name=""UpdateAerialCityDB"" Permission=""Allow"" />
      <Tool Name=""WebBrowse"" Permission=""Allow"" />
      <Tool Name=""WebSearch"" Permission=""Allow"" />
      <Tool Name=""Wget"" Permission=""Allow"" />
      <Tool Name=""WorkspaceNoteTemplate"" Permission=""Allow"" />
    </ToolPermissions>
    <InputSchema />
    <OutputSchema />
  </Agent>
  <Agent AgentId=""Investigator"">
    <DisplayName>Investigator</DisplayName>
    <AvatarPath>pack://application:,,,/Resources/GuideBot.png</AvatarPath>
    <SystemPrompt>你是代码库与文件系统探索者。你的任务是进行复杂的代码库探索。你将使用多项工具深入探查代码库，满足主代理的任务需求，并使用Passdown工具交付满足要求的结果。探索尽可能详尽。输出内容准确而丰富。不要编造不存在的内容或猜测的内容，只返回你的确通过工具探索过的内容。</SystemPrompt>
    <IsStructuredXmlIO>false</IsStructuredXmlIO>
    <InputDescription>任务需求</InputDescription>
    <OutputDescription>按照任务需求探索代码库所得的结果。尽可能详细。</OutputDescription>
    <RuntimeRole>SubAgentOnly</RuntimeRole>
    <SubAgentIntroduction>代码库与文件系统探索者。请在需要探索复杂目录时并行调用多个这种子代理来降低任务负担并加速交付。</SubAgentIntroduction>
    <LanguageModelSelectionMode>SpecificLanguageModel</LanguageModelSelectionMode>
    <SelectedLanguageModelKey></SelectedLanguageModelKey>
    <SelectedCapabilityLayerKey></SelectedCapabilityLayerKey>
    <IsPersonaEnabled>false</IsPersonaEnabled>
    <SelectedPersonaId>aero</SelectedPersonaId>
    <DefaultToolKits>
      <ToolKit Key=""Investigate"" />
    </DefaultToolKits>
    <ToolPermissions>
      <Tool Name=""CheckPlanItem"" Permission=""Disabled"" />
      <Tool Name=""CreateLateralFS"" Permission=""Disabled"" />
      <Tool Name=""Curl"" Permission=""Disabled"" />
      <Tool Name=""EditFile_Advanced"" Permission=""Disabled"" />
      <Tool Name=""EditFile_Rewrite"" Permission=""Disabled"" />
      <Tool Name=""EditFile_SearchReplace"" Permission=""Disabled"" />
      <Tool Name=""EditPlan"" Permission=""Disabled"" />
      <Tool Name=""ExecutePowershellCommand"" Permission=""Disabled"" />
      <Tool Name=""FileSystemIO_CreateFile"" Permission=""Disabled"" />
      <Tool Name=""FileSystemIO_DeleteDirectory"" Permission=""Disabled"" />
      <Tool Name=""FileSystemIO_DeleteFile"" Permission=""Disabled"" />
      <Tool Name=""FileSystemIO_NewDirectory"" Permission=""Disabled"" />
      <Tool Name=""FileSystemIO_RenameFile"" Permission=""Disabled"" />
      <Tool Name=""GlobSearch"" Permission=""Allow"" />
      <Tool Name=""GrepSearch"" Permission=""Allow"" />
      <Tool Name=""InheritLateralFS"" Permission=""Disabled"" />
      <Tool Name=""InitializeAerialCityRAG"" Permission=""RequireConfirmation"" />
      <Tool Name=""InitializeLiveXAML"" Permission=""Disabled"" />
      <Tool Name=""InitializePlan"" Permission=""Disabled"" />
      <Tool Name=""KeywordSearch"" Permission=""Allow"" />
      <Tool Name=""LoadToolKits"" Permission=""Disabled"" />
      <Tool Name=""MergeLateralFS"" Permission=""Disabled"" />
      <Tool Name=""OverwriteLateralFS"" Permission=""Disabled"" />
      <Tool Name=""ReadDirectoryRecursive"" Permission=""Allow"" />
      <Tool Name=""ReadImages"" Permission=""Allow"" />
      <Tool Name=""ReadTextFile"" Permission=""Allow"" />
      <Tool Name=""ReadThumbnails"" Permission=""Allow"" />
      <Tool Name=""SemanticSearch"" Permission=""Allow"" />
      <Tool Name=""SharpSpray"" Permission=""Disabled"" />
      <Tool Name=""ShowLiveXAML"" Permission=""Disabled"" />
      <Tool Name=""FerritaContextSnapshot"" Permission=""Disabled"" />
      <Tool Name=""SpawnSubAgent"" Permission=""Disabled"" />
      <Tool Name=""SWSniff"" Permission=""Disabled"" />
      <Tool Name=""UpdateAerialCityDB"" Permission=""RequireConfirmation"" />
      <Tool Name=""WebBrowse"" Permission=""Disabled"" />
      <Tool Name=""WebSearch"" Permission=""Disabled"" />
      <Tool Name=""Wget"" Permission=""Disabled"" />
      <Tool Name=""WorkspaceNoteTemplate"" Permission=""Disabled"" />
    </ToolPermissions>
    <InputSchema />
    <OutputSchema />
  </Agent>
  <Agent AgentId=""GPSubAgent"">
    <DisplayName>GPSubAgent</DisplayName>
    <AvatarPath>pack://application:,,,/Resources/GuideBot.png</AvatarPath>
    <SystemPrompt>系统指令
你是 Ferrita，一种人工智能代理。你和用户共享一个工作区，你的职责是与他们协作，直到他们的目标得到真正的解决。
通用指南：
你将资深工程师的判断力带入工作，但这种判断是建立在敏锐的观察之上，而非过早下定论。你要先阅读代码库，克制简单盲目的假设，让既有系统的架构引导你如何行动。
你更倾向于使用代码库中现有的模式、框架和本地辅助 API，而不是自己发明一套全新的抽象风格。
只有当抽象能够消除真正的复杂性、减少有意义的重复，或明显符合既有的本地模式时，你才会引入它。
你应让测试覆盖率随着风险和影响范围的大小而调整：对于局部的微调，保持测试的聚焦；当实现涉及共享行为、跨模块契约或面向用户的业务流时，则需要扩大测试覆盖范围。
你尽可能多调用工具，少直接输出自然语言。只有和用户交互或交付成果时使用自然语言。
作为通用子代理（General Purpose Subagent），你的职责是严格按照主智能体派发的任务要求进行工作，自主决定和调用最适合的工具（如文件读写、搜索、代码编辑或执行命令等）来解决问题。
在完成任务或需要回传结果时，你必须恰当且显式地调用 PassToMainAgent 工具，将你所得的最终 payload（如修改后的代码、分析报告或状态反馈）回传给主智能体。请记住，你无法使用 Passdown 工具，也无法直接通过自然语言回复主智能体。你的所有交付和回复必须通过调用 PassToMainAgent 工具完成。若有任务要求，必须严格百分之百遵循。你是通用子代理（General Purpose Subagent）。你代表主智能体在后台自主运行。你拥有全面的文件、代码库操作及命令执行等工具使用权限。你必须严格遵循主智能体下达的任务目标，当任务执行完毕或需要反馈时，恰当并显式地调用 PassToMainAgent 工具将最终结果回传给主智能体。请严格执行主智能体要求的每一个步骤，切勿遗漏。</SystemPrompt>
    <IsStructuredXmlIO>false</IsStructuredXmlIO>
    <InputDescription>任务需求</InputDescription>
    <OutputDescription>根据任务要求执行并交付的最终结果。必须使用 PassToMainAgent 工具回传。</OutputDescription>
    <RuntimeRole>SubAgentOnly</RuntimeRole>
    <SubAgentIntroduction>你是通用子代理（General Purpose Subagent）。你代表主智能体在后台自主运行。你拥有全面的文件、代码库操作及命令执行等工具使用权限。你必须严格遵循主智能体下达的任务目标，当任务执行完毕或需要反馈时，恰当并显式地调用 PassToMainAgent 工具将最终结果回传给主智能体。请严格执行主智能体要求的每一个步骤，切勿遗漏。</SubAgentIntroduction>
    <LanguageModelSelectionMode>SpecificLanguageModel</LanguageModelSelectionMode>
    <SelectedLanguageModelKey></SelectedLanguageModelKey>
    <SelectedCapabilityLayerKey></SelectedCapabilityLayerKey>
    <IsPersonaEnabled>false</IsPersonaEnabled>
    <SelectedPersonaId>aero</SelectedPersonaId>
    <DefaultToolKits>
      <ToolKit Key=""Investigate"" />
      <ToolKit Key=""Web"" />
    </DefaultToolKits>
    <ToolPermissions>
      <Tool Name=""CheckPlanItem"" Permission=""Allow"" />
      <Tool Name=""CreateLateralFS"" Permission=""Allow"" />
      <Tool Name=""Curl"" Permission=""Allow"" />
      <Tool Name=""EditFile_Advanced"" Permission=""Allow"" />
      <Tool Name=""EditFile_Rewrite"" Permission=""Allow"" />
      <Tool Name=""EditFile_SearchReplace"" Permission=""Allow"" />
      <Tool Name=""EditPlan"" Permission=""Allow"" />
      <Tool Name=""ExecutePowershellCommand"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_CreateFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_DeleteFile"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_NewDirectory"" Permission=""Allow"" />
      <Tool Name=""FileSystemIO_RenameFile"" Permission=""Allow"" />
      <Tool Name=""GlobSearch"" Permission=""Allow"" />
      <Tool Name=""GrepSearch"" Permission=""Allow"" />
      <Tool Name=""InheritLateralFS"" Permission=""Allow"" />
      <Tool Name=""InitializeAerialCityRAG"" Permission=""Allow"" />
      <Tool Name=""InitializeLiveXAML"" Permission=""Allow"" />
      <Tool Name=""InitializePlan"" Permission=""Allow"" />
      <Tool Name=""KeywordSearch"" Permission=""Allow"" />
      <Tool Name=""LoadToolKits"" Permission=""Allow"" />
      <Tool Name=""MergeLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""OverwriteLateralFS"" Permission=""RequireConfirmation"" />
      <Tool Name=""ReadDirectoryRecursive"" Permission=""Allow"" />
      <Tool Name=""ReadImages"" Permission=""Allow"" />
      <Tool Name=""ReadTextFile"" Permission=""Allow"" />
      <Tool Name=""ReadThumbnails"" Permission=""Allow"" />
      <Tool Name=""SemanticSearch"" Permission=""Allow"" />
      <Tool Name=""SharpSpray"" Permission=""Allow"" />
      <Tool Name=""ShowLiveXAML"" Permission=""Allow"" />
      <Tool Name=""FerritaContextSnapshot"" Permission=""Allow"" />
      <Tool Name=""SpawnSubAgent"" Permission=""Disabled"" />
      <Tool Name=""SWSniff"" Permission=""Allow"" />
      <Tool Name=""UpdateAerialCityDB"" Permission=""Allow"" />
      <Tool Name=""WebBrowse"" Permission=""Allow"" />
      <Tool Name=""WebSearch"" Permission=""Allow"" />
      <Tool Name=""Wget"" Permission=""Allow"" />
      <Tool Name=""WorkspaceNoteTemplate"" Permission=""Allow"" />
      <Tool Name=""PassToMainAgent"" Permission=""Allow"" />
    </ToolPermissions>
    <InputSchema />
    <OutputSchema />
  </Agent>
</AgentConfigurations>
";

        private void BtnAddLM_Click(object sender, RoutedEventArgs e)
        {
            var newLm = new WizardLanguageModelDefinition
            {
                DisplayName = string.Empty,
                InterfaceType = "OpenAI Chat Completions API",
                ContextWindowTokens = 200000
            };
            WizardLanguageModels.Add(newLm);
            SelectedWizardLanguageModel = newLm;
        }

        private void BtnDuplicateLM_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedWizardLanguageModel == null) return;
            var current = SelectedWizardLanguageModel;
            var dup = new WizardLanguageModelDefinition
            {
                DisplayName = current.DisplayName + " (副本)",
                InterfaceType = current.InterfaceType,
                ContextWindowTokens = current.ContextWindowTokens,
                EnableImageInput = current.EnableImageInput,
                EnableAudioInput = current.EnableAudioInput,
                EnableVideoInput = current.EnableVideoInput,
                EnableDocumentInput = current.EnableDocumentInput
            };
            if (string.Equals(current.InterfaceType, "Google", StringComparison.OrdinalIgnoreCase))
            {
                dup.GoogleSettings.ModelId = current.GoogleSettings.ModelId;
                dup.GoogleSettings.ApiKey = current.GoogleSettings.ApiKey;
                dup.GoogleSettings.BaseUrl = current.GoogleSettings.BaseUrl;
                dup.GoogleSettings.UseTemperature = current.GoogleSettings.UseTemperature;
                dup.GoogleSettings.Temperature = current.GoogleSettings.Temperature;
                dup.GoogleSettings.UseTopP = current.GoogleSettings.UseTopP;
                dup.GoogleSettings.TopP = current.GoogleSettings.TopP;
                dup.GoogleSettings.UseMaxOutputTokens = current.GoogleSettings.UseMaxOutputTokens;
                dup.GoogleSettings.MaxOutputTokens = current.GoogleSettings.MaxOutputTokens;
                dup.GoogleSettings.UseThinkingLevel = current.GoogleSettings.UseThinkingLevel;
                dup.GoogleSettings.ThinkingLevel = current.GoogleSettings.ThinkingLevel;
                dup.GoogleSettings.UseThinkingBudget = current.GoogleSettings.UseThinkingBudget;
                dup.GoogleSettings.ThinkingBudget = current.GoogleSettings.ThinkingBudget;
                dup.GoogleSettings.IncludeThoughts = current.GoogleSettings.IncludeThoughts;
            }
            else
            {
                dup.OpenAiSettings.ModelId = current.OpenAiSettings.ModelId;
                dup.OpenAiSettings.ApiKey = current.OpenAiSettings.ApiKey;
                dup.OpenAiSettings.BaseUrl = current.OpenAiSettings.BaseUrl;
                dup.OpenAiSettings.UseTemperature = current.OpenAiSettings.UseTemperature;
                dup.OpenAiSettings.Temperature = current.OpenAiSettings.Temperature;
                dup.OpenAiSettings.UseTopP = current.OpenAiSettings.UseTopP;
                dup.OpenAiSettings.TopP = current.OpenAiSettings.TopP;
                dup.OpenAiSettings.UseMaxOutputTokens = current.OpenAiSettings.UseMaxOutputTokens;
                dup.OpenAiSettings.MaxOutputTokens = current.OpenAiSettings.MaxOutputTokens;
                dup.OpenAiSettings.UsePresencePenalty = current.OpenAiSettings.UsePresencePenalty;
                dup.OpenAiSettings.PresencePenalty = current.OpenAiSettings.PresencePenalty;
                dup.OpenAiSettings.UseFrequencyPenalty = current.OpenAiSettings.UseFrequencyPenalty;
                dup.OpenAiSettings.FrequencyPenalty = current.OpenAiSettings.FrequencyPenalty;
                dup.OpenAiSettings.UseSeed = current.OpenAiSettings.UseSeed;
                dup.OpenAiSettings.Seed = current.OpenAiSettings.Seed;
                dup.OpenAiSettings.UseReasoningEffort = current.OpenAiSettings.UseReasoningEffort;
                dup.OpenAiSettings.ReasoningEffort = current.OpenAiSettings.ReasoningEffort;
                dup.OpenAiSettings.UseReasoningOutput = current.OpenAiSettings.UseReasoningOutput;
                dup.OpenAiSettings.ReasoningOutput = current.OpenAiSettings.ReasoningOutput;
            }
            WizardLanguageModels.Add(dup);
            SelectedWizardLanguageModel = dup;
        }

        private void BtnRemoveLM_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedWizardLanguageModel == null) return;
            if (WizardLanguageModels.Count <= 1)
            {
                string msg = TryFindResource("Msg_KeepAtLeastOneLM") as string ?? "系统必须保留至少一个语言模型配置。";
                string title = TryFindResource("Msg_Title_Warning") as string ?? "提示";
                MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var toRemove = SelectedWizardLanguageModel;
            int idx = WizardLanguageModels.IndexOf(toRemove);
            WizardLanguageModels.Remove(toRemove);
            if (idx >= WizardLanguageModels.Count) idx = WizardLanguageModels.Count - 1;
            SelectedWizardLanguageModel = WizardLanguageModels[idx];
        }

        // 功能层级相关辅助函数
        private void AddLayerEntry(int layerIndex)
        {
            if (CapabilityLayers.Count > layerIndex)
            {
                string defaultKey = WizardLanguageModels.Count > 0 ? WizardLanguageModels[0].Key : string.Empty;
                CapabilityLayers[layerIndex].LanguageModels.Add(new WizardCapabilityLayerEntry { LanguageModelKey = defaultKey });
            }
        }

        private void MoveLayerEntry(int layerIndex, object buttonSender, bool up)
        {
            if (CapabilityLayers.Count <= layerIndex) return;
            var layer = CapabilityLayers[layerIndex];
            if (buttonSender is FrameworkElement element && element.DataContext is WizardCapabilityLayerEntry entry)
            {
                int index = layer.LanguageModels.IndexOf(entry);
                if (index < 0) return;
                int target = up ? index - 1 : index + 1;
                if (target >= 0 && target < layer.LanguageModels.Count)
                {
                    layer.LanguageModels.Move(index, target);
                }
            }
        }

        private void DeleteLayerEntry(int layerIndex, object buttonSender)
        {
            if (CapabilityLayers.Count <= layerIndex) return;
            var layer = CapabilityLayers[layerIndex];
            if (buttonSender is FrameworkElement element && element.DataContext is WizardCapabilityLayerEntry entry)
            {
                layer.LanguageModels.Remove(entry);
            }
        }

        // 三个内置卡片的 Click 事件绑定
        private void BtnAddLayer1_Click(object sender, RoutedEventArgs e) => AddLayerEntry(0);
        private void BtnMoveUpLayer1_Click(object sender, RoutedEventArgs e) => MoveLayerEntry(0, sender, true);
        private void BtnMoveDownLayer1_Click(object sender, RoutedEventArgs e) => MoveLayerEntry(0, sender, false);
        private void BtnDeleteLayer1_Click(object sender, RoutedEventArgs e) => DeleteLayerEntry(0, sender);

        private void BtnAddLayer2_Click(object sender, RoutedEventArgs e) => AddLayerEntry(1);
        private void BtnMoveUpLayer2_Click(object sender, RoutedEventArgs e) => MoveLayerEntry(1, sender, true);
        private void BtnMoveDownLayer2_Click(object sender, RoutedEventArgs e) => MoveLayerEntry(1, sender, false);
        private void BtnDeleteLayer2_Click(object sender, RoutedEventArgs e) => DeleteLayerEntry(1, sender);

        private void BtnAddLayer3_Click(object sender, RoutedEventArgs e) => AddLayerEntry(2);
        private void BtnMoveUpLayer3_Click(object sender, RoutedEventArgs e) => MoveLayerEntry(2, sender, true);
        private void BtnMoveDownLayer3_Click(object sender, RoutedEventArgs e) => MoveLayerEntry(2, sender, false);
        private void BtnDeleteLayer3_Click(object sender, RoutedEventArgs e) => DeleteLayerEntry(2, sender);

        private const string LicenseText = @"Creative Commons CC0 1.0 Universal
==================================

CREATIVE COMMONS CORPORATION IS NOT A LAW FIRM AND DOES NOT PROVIDE LEGAL SERVICES. DISTRIBUTION OF THIS DOCUMENT DOES NOT CREATE AN ATTORNEY-CLIENT RELATIONSHIP. CREATIVE COMMONS PROVIDES THIS INFORMATION ON AN ""AS-IS"" BASIS. CREATIVE COMMONS MAKES NO WARRANTIES REGARDING THE USE OF THIS DOCUMENT OR THE INFORMATION OR WORKS PROVIDED HEREUNDER, AND DISCLAIMS LIABILITY FOR DAMAGES RESULTING FROM THE USE OF THIS DOCUMENT OR THE INFORMATION OR WORKS PROVIDED HEREUNDER.

Statement of Purpose
--------------------

The laws of most jurisdictions throughout the world automatically confer exclusive Copyright and Related Rights (defined below) upon the creator and subsequent owner(s) (each and all, an ""owner"") of an original work of authorship and/or a database (each, a ""Work"").

Certain owners wish to permanently relinquish those rights to a Work for the purpose of contributing to a commons of creative, cultural and scientific works (""Commons"") that the public can reliably and without fear of later claims of infringement build upon, modify, incorporate in other works, reuse and redistribute as freely as possible in any form whatsoever and for any purposes, including without limitation commercial purposes. These owners may contribute to the Commons to promote the ideal of a free culture and the further production of creative, cultural and scientific works, or without reputation or promotion.

In furtherance of these purposes and/or expectations, the person associating CC0 with a Work (the ""Affirmer"") hereby publicly associates CC0 with the Work and elects to treat the Work as public domain.

1. Copyright and Related Rights.
--------------------------------

A Work made available under CC0 may be protected by copyright and related or neighboring rights (""Copyright and Related Rights""). Copyright and Related Rights include, but are not limited to, the following:

  i. the right to reproduce, adapt, distribute, perform, display, communicate, and translate a Work;
  ii. moral rights retained by the original author(s) and/or performer(s);
  iii. publicity and privacy rights pertaining to a person's image or likeness depicted in a Work;
  iv. rights protecting against unfair competition in relation to a Work, subject to the limitations in Paragraph 4(a), below;
  v. rights protecting the extraction, dissemination, use and reuse of data in a Work;
  vi. database rights (such as those arising under Directive 96/9/EC of the European Parliament and of the Council of 11 March 1996 on the legal protection of databases, and under any national implementation thereof, including any amended or successor version of such directive); and
  vii. other similar, equivalent or corresponding rights throughout the world based on applicable law or treaty, and any national implementations thereof.

2. Waiver.
----------

To the greatest extent permitted by, but not in contravention of, applicable law, Affirmer hereby overtly, fully, permanently, irrevocably and unconditionally waives, abandons, and surrenders all of Affirmer's Copyright and Related Rights and associated claims and causes of action, whether now known or unknown (including existing as well as future claims and causes of action), in the Work (i) in all territories worldwide, (ii) for the maximum duration provided by applicable law or treaty (including future time extensions), (iii) in any current or future medium and for any number of copies, and (iv) for any purpose whatsoever, including without limitation commercial, advertising or promotional purposes (the ""Waiver""). Affirmer makes the Waiver for the benefit of each member of the public at large and to the detriment of Affirmer's heirs and successors, fully intending that such Waiver shall not be subject to revocation, rescission, cancellation, termination, or any other legal or equitable action to project the Work from the Commons.";

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
            
            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") 
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
            }
            LfsWorkingDir = Path.Combine(userProfile, "Ferrita", "LateralFS");

            UpdateLocalizedContent();
            
            // 初始化内置层级
            CapabilityLayers.Add(new WizardCapabilityLayerDefinition
            {
                Key = "builtin.context-compression",
                Name = "上下文压缩"
            });
            CapabilityLayers.Add(new WizardCapabilityLayerDefinition
            {
                Key = "builtin.utility-i-fast",
                Name = "实用I（快速）"
            });
            CapabilityLayers.Add(new WizardCapabilityLayerDefinition
            {
                Key = "builtin.utility-ii-smart",
                Name = "实用II（智能）"
            });

            // 初始化默认的语言模型
            // 已移除默认添加的语言模型，一切交给用户。

            // 为三个内置层级默认绑定模型
            // 已移除默认层级绑定模型引用。

            // 解析默认智能体列表
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(DefaultAgentsXml);
                var agents = doc.Root?.Elements("Agent");
                if (agents != null)
                {
                    foreach (var agent in agents)
                    {
                        var agentId = agent.Attribute("AgentId")?.Value ?? string.Empty;
                        var displayName = agent.Element("DisplayName")?.Value ?? agentId;
                        var systemPrompt = agent.Element("SystemPrompt")?.Value ?? string.Empty;
                        
                        string description = string.Empty;
                        if (agentId == "NiceChatter")
                            description = "友善的助理，帮助用户进行普通聊天与各项日常任务。";
                        else if (agentId == "Coder")
                            description = "专业的编程智能体，擅长在工作区中与用户协作解决复杂的代码问题。";
                        else if (agentId == "CoderWithPowershell")
                            description = "拥有命令行权限的编程智能体，可以直接在工作区运行 PowerShell 命令调试项目。";
                        else if (agentId == "Puterperson")
                            description = "计算机的化身，帮助用户深度操作计算机系统并自动化完成复杂的工作。";
                        else if (agentId == "Investigator")
                            description = "探索型子智能体，主要执行代码库的深度搜索、文件树分析与结构调查。";
                        else if (agentId == "GPSubAgent")
                            description = "通用子智能体，根据主智能体派发的任务，自主规划并使用各种工具协助解决任务。";

                        var parsedMode = AgentLanguageModelSelectionMode.SpecificLanguageModel;
                        if (Enum.TryParse<AgentLanguageModelSelectionMode>(agent.Element("LanguageModelSelectionMode")?.Value, true, out var pm))
                        {
                            parsedMode = pm;
                        }
                        var lmKey = agent.Element("SelectedLanguageModelKey")?.Value ?? string.Empty;
                        var clKey = agent.Element("SelectedCapabilityLayerKey")?.Value ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(lmKey)) lmKey = string.Empty;
                        if (string.IsNullOrWhiteSpace(clKey)) clKey = "builtin.utility-ii-smart";

                        AgentOptions.Add(new AgentConfigOption
                        {
                            IsEnabled = true, // 默认都启用
                            AgentId = agentId,
                            DisplayName = displayName,
                            SystemPrompt = systemPrompt,
                            Description = description,
                            LanguageModelSelectionMode = parsedMode,
                            SelectedLanguageModelKey = lmKey,
                            SelectedCapabilityLayerKey = clKey,
                            RawElement = agent
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error parsing default agents: " + ex.Message);
            }
            UpdateLocalizedContent();
            UpdateDiskSpaceInfo();
        }

        /// <summary>
        /// 更新当前的向导页面和底部控制按钮状态
        /// </summary>
        private void UpdateStepUI()
        {
            if (MainTabControl == null) return;
            if (BtnBack == null || BtnNext == null || BtnCancel == null || TxtFooterStatus == null) return;
            if (_currentStep < 1) _currentStep = 1;
            if (_currentStep > 11) _currentStep = 11;

            // 更新 TabControl 选中的 Index
            if (_currentStep <= 3)
            {
                MainTabControl.SelectedIndex = _currentStep - 1;
            }
            else if (_currentStep >= 4 && _currentStep <= 9)
            {
                MainTabControl.SelectedIndex = 3; // Ferrita tab
                if (FerritaTabControl != null)
                {
                    FerritaTabControl.SelectedIndex = _currentStep - 4;
                }
            }
            else if (_currentStep == 10)
            {
                MainTabControl.SelectedIndex = 4; // 安装进度
            }
            else if (_currentStep == 11)
            {
                MainTabControl.SelectedIndex = 5; // 完成
            }

            // 根据步骤调整按钮
            switch (_currentStep)
            {
                case 1:
                    BtnBack.IsEnabled = false;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_ReadyToInstall") as string ?? "准备安装";
                    break;

                case 2:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_ReadingLicense") as string ?? "阅读许可协议";
                    break;

                case 3:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_SelectDirectory") as string ?? "选择安装目标目录";
                    UpdateDiskSpaceInfo();
                    break;

                case 4:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_ConfigureLM") as string ?? "配置语言模型 API";
                    break;

                case 5:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_ConfigureLayers") as string ?? "配置功能层级优先级";
                    break;

                case 6:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_ConfigureAgents") as string ?? "配置主智能体 (Agent)";
                    break;

                case 7:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_PrepareFlows") as string ?? "准备默认会话流";
                    
                    var existingSelections = new System.Collections.Generic.Dictionary<string, bool>();
                    foreach (var opt in FlowOptions)
                    {
                        existingSelections[opt.AgentId] = opt.IsSelected;
                    }

                    FlowOptions.Clear();
                    foreach (var agent in AgentOptions)
                    {
                        if (agent.IsEnabled)
                        {
                            bool isSelected = true;
                            // 检查该智能体是否是仅作为子代理运行 (SubAgentOnly)
                            bool isSubAgentOnly = string.Equals(agent.RawElement?.Element("RuntimeRole")?.Value, "SubAgentOnly", StringComparison.OrdinalIgnoreCase);
                            if (isSubAgentOnly)
                            {
                                isSelected = false;
                            }
                            if (existingSelections.TryGetValue(agent.AgentId, out var prevSelected))
                            {
                                isSelected = prevSelected;
                            }
                            var flowDescFormat = TryFindResource("Flow_Desc_Template") as string ?? "使用 {0} 代理自动生成默认会话流节点图文件 ({1}.xml)。";
                            FlowOptions.Add(new SessionFlowConfigOption
                            {
                                AgentId = agent.AgentId,
                                DisplayName = agent.DisplayName,
                                Description = string.Format(flowDescFormat, agent.DisplayName, agent.AgentId),
                                IsSelected = isSelected
                            });
                        }
                    }
                    break;

                case 8:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Next") as string ?? "下一步 >";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_ConfigureLFS") as string ?? "配置侧向文件系统 LateralFS";
                    break;

                case 9:
                    BtnBack.IsEnabled = true;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Install") as string ?? "安装";
                    BtnCancel.IsEnabled = true;
                    TxtFooterStatus.Text = TryFindResource("Status_SelectIntegration") as string ?? "选择系统集成选项";
                    break;

                case 10:
                    BtnBack.IsEnabled = false;
                    BtnNext.IsEnabled = false;
                    BtnCancel.IsEnabled = false;
                    TxtFooterStatus.Text = TryFindResource("Status_Installing") as string ?? "正在安装，请勿关闭程序...";
                    if (!_installationStarted)
                    {
                        _installationStarted = true;
                        _ = RunInstallationAsync();
                    }
                    break;

                case 11:
                    BtnBack.IsEnabled = false;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = TryFindResource("Btn_Complete") as string ?? "完成";
                    BtnCancel.IsEnabled = false;
                    TxtFooterStatus.Text = TryFindResource("Status_InstallComplete") as string ?? "安装成功完成";
                    break;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 1 && _currentStep != 10)
            {
                _currentStep--;
                UpdateStepUI();
            }
        }

        /// <summary>
        /// 下一步 / 安装 / 完成按钮点击事件
        /// </summary>
        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 11)
            {
                if (ChkLaunch.IsChecked == true)
                {
                    LaunchInstalledFerrita();
                }
                this.Close();
                return;
            }

            if (_currentStep == 6)
            {
                bool anyEnabled = false;
                foreach (var option in AgentOptions)
                {
                    if (option.IsEnabled)
                    {
                        anyEnabled = true;
                        break;
                    }
                }
                if (!anyEnabled)
                {
                    string msg = TryFindResource("Msg_SelectAtLeastOneAgent") as string ?? "请至少选择并启用一个智能体以继续安装。";
                    string title = TryFindResource("Msg_Title_Warning") as string ?? "提示";
                    MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (_currentStep < 11)
            {
                _currentStep++;
                UpdateStepUI();
            }
        }

        /// <summary>
        /// 取消按钮点击事件
        /// </summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            string msg = TryFindResource("Msg_ConfirmExit") as string ?? "您确定要退出 Ferrita 安装向导吗？";
            string title = TryFindResource("Msg_Title_Exit") as string ?? "退出安装";
            var result = MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                this.Close();
            }
        }

        /// <summary>
        /// 浏览安装文件夹
        /// </summary>
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = TryFindResource("Browse_Description") as string ?? "选择 Ferrita 的安装文件夹";
                dialog.SelectedPath = TxtDestPath.Text;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TxtDestPath.Text = dialog.SelectedPath;
                    UpdateDiskSpaceInfo();
                }
            }
        }

        /// <summary>
        /// 浏览 LateralFS 工作文件夹
        /// </summary>
        private void BtnBrowseLfs_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = TryFindResource("LFS_BrowseDescription") as string ?? "选择 LateralFS 的工作根目录";
                dialog.SelectedPath = LfsWorkingDir;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    LfsWorkingDir = dialog.SelectedPath;
                }
            }
        }

        /// <summary>
        /// 路径文本框内容变化时刷新磁盘空间信息
        /// </summary>
        private void TxtDestPath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateDiskSpaceInfo();
        }

        /// <summary>
        /// 计算 App 文件夹的实际体积作为所需空间，获取目标磁盘的可用空间
        /// </summary>
        private void UpdateDiskSpaceInfo()
        {
            if (TxtSpaceRequired == null || TxtSpaceAvailable == null || TxtDestPath == null) return;

            try
            {
                // 计算应用程序体积：获取安装程序旁边的 App 文件夹的实际大小
                var appFolderPath = Path.Combine(AppContext.BaseDirectory, "App");
                long appSizeBytes = 0;
                if (Directory.Exists(appFolderPath))
                {
                    foreach (var file in Directory.GetFiles(appFolderPath, "*", SearchOption.AllDirectories))
                    {
                        try { appSizeBytes += new FileInfo(file).Length; }
                        catch { /* 忽略无法访问的文件 */ }
                    }
                }
                TxtSpaceRequired.Text = FormatFileSize(appSizeBytes);
            }
            catch
            {
                TxtSpaceRequired.Text = "--";
            }

            try
            {
                // 获取目标路径所在磁盘的可用空间
                var destPath = TxtDestPath.Text.Trim();
                if (!string.IsNullOrWhiteSpace(destPath))
                {
                    destPath = Environment.ExpandEnvironmentVariables(destPath);
                    var root = Path.GetPathRoot(Path.GetFullPath(destPath));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var driveInfo = new DriveInfo(root);
                        if (driveInfo.IsReady)
                        {
                            TxtSpaceAvailable.Text = FormatFileSize(driveInfo.AvailableFreeSpace);
                        }
                        else
                        {
                            TxtSpaceAvailable.Text = "--";
                        }
                    }
                    else
                    {
                        TxtSpaceAvailable.Text = "--";
                    }
                }
                else
                {
                    TxtSpaceAvailable.Text = "--";
                }
            }
            catch
            {
                TxtSpaceAvailable.Text = "--";
            }
        }

        /// <summary>
        /// 将字节数格式化为人类可读的文件大小字符串
        /// </summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unitIndex = 0;
            double size = bytes;
            while (size >= 1024.0 && unitIndex < units.Length - 1)
            {
                size /= 1024.0;
                unitIndex++;
            }
            return $"{size:F1} {units[unitIndex]}";
        }

        /// <summary>
        /// 启动模拟安装过程的定时器
        /// </summary>
        private async Task RunInstallationAsync()
        {
            try
            {
                UpdateInstallProgress(0, "Preparing installation...");

                var destinationPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(TxtDestPath.Text.Trim()));
                var sourcePath = Path.Combine(AppContext.BaseDirectory, "App", "Ferrita");
                var createDesktopShortcut = ChkShortcut.IsChecked == true;
                var createStartMenuShortcut = ChkStartMenu.IsChecked == true;
                var createStartupShortcut = false;
                var enableShellIntegration = ChkShellIntegration.IsChecked == true;
                var shellMenuText = TryFindResource("Shell_Menu_Text") as string ?? "Ask Ferrita...";
                _installedExecutablePath = Path.Combine(destinationPath, "Ferrita.exe");

                // 获取选中的语言代码
                string selectedLanguage = "zh-CN";
                if (ComboLanguage.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                {
                    selectedLanguage = item.Tag as string ?? "zh-CN";
                }

                var enableLfs = IsLfsEnabled;
                var lfsWorkingDir = LfsWorkingDir;

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(destinationPath);

                    var sourceFiles = Directory.Exists(sourcePath)
                        ? Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories)
                        : Array.Empty<string>();

                    var totalWork = Math.Max(sourceFiles.Length, 1) + (enableLfs ? 11 : 10);
                    var completedWork = 0;

                    ReportInstallStep(++completedWork, totalWork, "Saving language model configuration...");
                    Dispatcher.Invoke(SaveLanguageModelConfig);

                    ReportInstallStep(++completedWork, totalWork, "Saving capability layer configuration...");
                    Dispatcher.Invoke(SaveCapabilityLayerConfig);

                    ReportInstallStep(++completedWork, totalWork, "Saving agent configuration...");
                    Dispatcher.Invoke(SaveAgentConfig);

                    ReportInstallStep(++completedWork, totalWork, "Saving session flow configuration...");
                    Dispatcher.Invoke(SaveSessionFlowConfigs);

                    ReportInstallStep(++completedWork, totalWork, "Saving localization configuration...");
                    Dispatcher.Invoke(() => SaveLocalizationConfig(selectedLanguage));

                    ReportInstallStep(++completedWork, totalWork, "Saving lateral file system configuration...");
                    Dispatcher.Invoke(() => SaveLateralFileSystemConfig(enableLfs, lfsWorkingDir));

                    if (enableLfs)
                    {
                        string progressMsg = "Configuring Windows Projected File System (ProjFS)...";
                        Dispatcher.Invoke(() => {
                            progressMsg = TryFindResource("Progress_EnableProjFS") as string ?? progressMsg;
                        });
                        ReportInstallStep(++completedWork, totalWork, progressMsg);
                        EnableProjFSFeature();
                    }

                    ReportInstallStep(++completedWork, totalWork, "Copying application files...");
                    if (Directory.Exists(sourcePath))
                    {
                        CopyDirectoryWithProgress(sourcePath, destinationPath, sourceFiles, ref completedWork, totalWork);
                    }
                    else
                    {
                        completedWork++;
                        ReportInstallStep(completedWork, totalWork, "Application payload folder is empty.");
                    }

                    ReportInstallStep(++completedWork, totalWork, "Creating shortcuts...");
                    ApplyShortcutOptions(destinationPath, createDesktopShortcut, createStartMenuShortcut, createStartupShortcut);

                    ReportInstallStep(++completedWork, totalWork, "Registering shell integration...");
                    SaveShellIntegrationConfig(enableShellIntegration, destinationPath, shellMenuText);

                    ReportInstallStep(++completedWork, totalWork, "Finalizing installation...");
                });

                UpdateInstallProgress(100, "Installation complete.");
                _currentStep = 11;
                UpdateStepUI();
            }
            catch (Exception ex)
            {
                _installationStarted = false;
                BtnBack.IsEnabled = true;
                BtnNext.IsEnabled = true;
                BtnCancel.IsEnabled = true;
                TxtFooterStatus.Text = "Installation failed";
                TxtProgressStatus.Text = ex.Message;
                MessageBox.Show(ex.Message, TryFindResource("Msg_Title_Warning") as string ?? "Warning", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyDirectoryWithProgress(string sourcePath, string destinationPath, string[] sourceFiles, ref int completedWork, int totalWork)
        {
            foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relativePath = directory.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationPath, relativePath));
            }

            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = sourceFile.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destinationFile = Path.Combine(destinationPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destinationPath);
                File.Copy(sourceFile, destinationFile, overwrite: true);
                ReportInstallStep(++completedWork, totalWork, "Copying " + relativePath);
            }
        }

        private void ApplyShortcutOptions(
            string destinationPath,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            bool createStartupShortcut)
        {
            var executablePath = Path.Combine(destinationPath, "Ferrita.exe");
            var shortcutName = "Ferrita.lnk";

            if (createDesktopShortcut)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), shortcutName), executablePath, destinationPath);
            }

            if (createStartMenuShortcut)
            {
                var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Ferrita");
                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(Path.Combine(startMenuDir, shortcutName), executablePath, destinationPath);
            }

            var oldStartupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Skylifter.lnk");
            if (File.Exists(oldStartupShortcut))
            {
                try { File.Delete(oldStartupShortcut); } catch { }
            }

            var startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Ferrita.lnk");
            if (createStartupShortcut)
            {
                CreateShortcut(startupShortcut, executablePath, destinationPath, "--daemon");
            }
            else if (File.Exists(startupShortcut))
            {
                try { File.Delete(startupShortcut); } catch { }
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string? arguments = null)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is not available on this system.");
            var shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Failed to create WScript.Shell.");
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath })
                ?? throw new InvalidOperationException("Failed to create shortcut.");
            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            }
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
            if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        private void ReportInstallStep(int completedWork, int totalWork, string statusText)
        {
            var percent = Math.Max(0, Math.Min(99, (int)Math.Round(completedWork * 100.0 / Math.Max(totalWork, 1))));
            Dispatcher.Invoke(() => UpdateInstallProgress(percent, statusText));
        }

        private void UpdateInstallProgress(int percent, string statusText)
        {
            _progressVal = percent;
            ProgBar.Value = percent;
            TxtProgressPercent.Text = percent + "%";
            TxtProgressStatus.Text = statusText;
        }

        private void LaunchInstalledFerrita()
        {
            var executablePath = _installedExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(TxtDestPath.Text.Trim())), "Ferrita.exe");
            }

            if (!File.Exists(executablePath))
            {
                MessageBox.Show("Ferrita.exe was not found in the installation folder.", TryFindResource("Msg_Title_Warning") as string ?? "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }

        /// <summary>
        /// 保存语言模型配置列表到 Ferrita 的 AppData 配置文件中
        /// </summary>
        private void SaveLanguageModelConfig()
        {
            try
            {
                string configDir = GetTrueConfigurationDirectoryPath();
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, "LanguageModel.xml");

                var languageModelsElement = new System.Xml.Linq.XElement("LanguageModels", new System.Xml.Linq.XAttribute("SchemaVersion", 2));

                foreach (var lm in WizardLanguageModels)
                {
                    System.Xml.Linq.XElement interfaceSettingsEl;
                    if (string.Equals(lm.InterfaceType, "Google", StringComparison.OrdinalIgnoreCase))
                    {
                        var g = lm.GoogleSettings;
                        interfaceSettingsEl = new System.Xml.Linq.XElement("InterfaceSettings",
                            new System.Xml.Linq.XAttribute("Type", "Google"),
                            new System.Xml.Linq.XElement("ModelId", g.ModelId),
                            new System.Xml.Linq.XElement("ApiKey", g.ApiKey),
                            new System.Xml.Linq.XElement("BaseUrl", g.BaseUrl),
                            new System.Xml.Linq.XElement("UseTemperature", g.UseTemperature),
                            new System.Xml.Linq.XElement("Temperature", g.Temperature),
                            new System.Xml.Linq.XElement("UseTopP", g.UseTopP),
                            new System.Xml.Linq.XElement("TopP", g.TopP),
                            new System.Xml.Linq.XElement("UseMaxOutputTokens", g.UseMaxOutputTokens),
                            new System.Xml.Linq.XElement("MaxOutputTokens", g.MaxOutputTokens),
                            new System.Xml.Linq.XElement("UseThinkingLevel", g.UseThinkingLevel),
                            new System.Xml.Linq.XElement("ThinkingLevel", g.ThinkingLevel),
                            new System.Xml.Linq.XElement("UseThinkingBudget", g.UseThinkingBudget),
                            new System.Xml.Linq.XElement("ThinkingBudget", g.ThinkingBudget),
                            new System.Xml.Linq.XElement("IncludeThoughts", g.IncludeThoughts)
                        );
                    }
                    else if (string.Equals(lm.InterfaceType, "OpenAI Responses API", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = lm.OpenAiSettings;
                        interfaceSettingsEl = new System.Xml.Linq.XElement("InterfaceSettings",
                            new System.Xml.Linq.XAttribute("Type", "OpenAI Responses API"),
                            new System.Xml.Linq.XElement("ModelId", m.ModelId),
                            new System.Xml.Linq.XElement("ApiKey", m.ApiKey),
                            new System.Xml.Linq.XElement("BaseUrl", m.BaseUrl),
                            new System.Xml.Linq.XElement("UseTemperature", m.UseTemperature),
                            new System.Xml.Linq.XElement("Temperature", m.Temperature),
                            new System.Xml.Linq.XElement("UseTopP", m.UseTopP),
                            new System.Xml.Linq.XElement("TopP", m.TopP),
                            new System.Xml.Linq.XElement("UseMaxOutputTokens", m.UseMaxOutputTokens),
                            new System.Xml.Linq.XElement("MaxOutputTokens", m.MaxOutputTokens),
                            new System.Xml.Linq.XElement("UseReasoningEffort", m.UseReasoningEffort),
                            new System.Xml.Linq.XElement("ReasoningEffort", m.ReasoningEffort)
                        );
                    }
                    else
                    {
                        var m = lm.OpenAiSettings;
                        interfaceSettingsEl = new System.Xml.Linq.XElement("InterfaceSettings",
                            new System.Xml.Linq.XAttribute("Type", "OpenAI Chat Completions API"),
                            new System.Xml.Linq.XElement("ModelId", m.ModelId),
                            new System.Xml.Linq.XElement("ApiKey", m.ApiKey),
                            new System.Xml.Linq.XElement("BaseUrl", m.BaseUrl),
                            new System.Xml.Linq.XElement("UseTemperature", m.UseTemperature),
                            new System.Xml.Linq.XElement("Temperature", m.Temperature),
                            new System.Xml.Linq.XElement("UseTopP", m.UseTopP),
                            new System.Xml.Linq.XElement("TopP", m.TopP),
                            new System.Xml.Linq.XElement("UseMaxOutputTokens", m.UseMaxOutputTokens),
                            new System.Xml.Linq.XElement("MaxOutputTokens", m.MaxOutputTokens),
                            new System.Xml.Linq.XElement("UsePresencePenalty", m.UsePresencePenalty),
                            new System.Xml.Linq.XElement("PresencePenalty", m.PresencePenalty),
                            new System.Xml.Linq.XElement("UseFrequencyPenalty", m.UseFrequencyPenalty),
                            new System.Xml.Linq.XElement("FrequencyPenalty", m.FrequencyPenalty),
                            new System.Xml.Linq.XElement("UseSeed", m.UseSeed),
                            new System.Xml.Linq.XElement("Seed", m.Seed),
                            new System.Xml.Linq.XElement("UseReasoningEffort", m.UseReasoningEffort),
                            new System.Xml.Linq.XElement("ReasoningEffort", m.ReasoningEffort),
                            new System.Xml.Linq.XElement("UseReasoningOutput", m.UseReasoningOutput),
                            new System.Xml.Linq.XElement("ReasoningOutput", m.ReasoningOutput)
                        );
                    }

                    var lmEl = new System.Xml.Linq.XElement("LanguageModel",
                        new System.Xml.Linq.XElement("Key", lm.Key),
                        new System.Xml.Linq.XElement("DisplayName", lm.DisplayName),
                        new System.Xml.Linq.XElement("InterfaceType", lm.InterfaceType),
                        new System.Xml.Linq.XElement("ContextWindowTokens", lm.ContextWindowTokens),
                        new System.Xml.Linq.XElement("EnableImageInput", lm.EnableImageInput),
                        new System.Xml.Linq.XElement("EnableAudioInput", lm.EnableAudioInput),
                        new System.Xml.Linq.XElement("EnableVideoInput", lm.EnableVideoInput),
                        new System.Xml.Linq.XElement("EnableDocumentInput", lm.EnableDocumentInput),
                        interfaceSettingsEl
                    );

                    languageModelsElement.Add(lmEl);
                }

                var doc = new System.Xml.Linq.XDocument(languageModelsElement);
                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save language model configuration: " + ex.Message);
            }
        }

        /// <summary>
        /// 保存功能层级配置到 Ferrita 的 AppData 配置文件中
        /// </summary>
        private void SaveCapabilityLayerConfig()
        {
            try
            {
                string configDir = GetTrueConfigurationDirectoryPath();
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, "CapabilityLayer.xml");

                var doc = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("CapabilityLayers",
                        new System.Xml.Linq.XAttribute("SchemaVersion", 2),
                        System.Linq.Enumerable.Select(CapabilityLayers, definition => new System.Xml.Linq.XElement("CapabilityLayer",
                            new System.Xml.Linq.XElement("Key", definition.Key),
                            new System.Xml.Linq.XElement("Name", definition.Name),
                            System.Linq.Enumerable.Select(definition.LanguageModels, item => new System.Xml.Linq.XElement("LanguageModelRef",
                                new System.Xml.Linq.XAttribute("Key", item.LanguageModelKey ?? string.Empty)))))));

                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save capability layer configuration: " + ex.Message);
            }
        }

        /// <summary>
        /// 保存初步的智能体配置到 Ferrita 的 AppData 配置文件中
        /// </summary>
        private void SaveAgentConfig()
        {
            try
            {
                string configDir = GetTrueConfigurationDirectoryPath();
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, "AgentConfiguration.xml");

                var doc = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("AgentConfigurations",
                        new System.Xml.Linq.XAttribute("SchemaVersion", 3)
                    )
                );

                foreach (var option in AgentOptions)
                {
                    if (option.IsEnabled && option.RawElement != null)
                    {
                        var agentEl = new System.Xml.Linq.XElement(option.RawElement);
                        
                        var modeEl = agentEl.Element("LanguageModelSelectionMode");
                        if (modeEl != null)
                            modeEl.Value = option.LanguageModelSelectionMode.ToString();
                        else
                            agentEl.Add(new System.Xml.Linq.XElement("LanguageModelSelectionMode", option.LanguageModelSelectionMode.ToString()));

                        var modelKeyEl = agentEl.Element("SelectedLanguageModelKey");
                        if (modelKeyEl != null)
                            modelKeyEl.Value = option.SelectedLanguageModelKey ?? string.Empty;
                        else
                            agentEl.Add(new System.Xml.Linq.XElement("SelectedLanguageModelKey", option.SelectedLanguageModelKey ?? string.Empty));

                        var layerKeyEl = agentEl.Element("SelectedCapabilityLayerKey");
                        if (layerKeyEl != null)
                            layerKeyEl.Value = option.SelectedCapabilityLayerKey ?? string.Empty;
                        else
                            agentEl.Add(new System.Xml.Linq.XElement("SelectedCapabilityLayerKey", option.SelectedCapabilityLayerKey ?? string.Empty));

                        doc.Root?.Add(agentEl);
                    }
                }

                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save agent configuration: " + ex.Message);
            }
        }

        /// <summary>
        /// 保存选中的会话流节点图 XML 到用户的会话流目录中
        /// </summary>
        private void SaveSessionFlowConfigs()
        {
            try
            {
                string nodegraphsDir = GetTrueSessionFlowsDirectoryPath();
                Directory.CreateDirectory(nodegraphsDir);

                foreach (var option in FlowOptions)
                {
                    if (option.IsSelected)
                    {
                        string filePath = Path.Combine(nodegraphsDir, $"{option.AgentId}.xml");
                        
                        // 生成符合要求的节点图 XML
                        string graphGuid = Guid.NewGuid().ToString("N");
                        string userInputGuid = Guid.NewGuid().ToString("N");
                        string returnGuid = Guid.NewGuid().ToString("N");
                        string agentGuid = Guid.NewGuid().ToString("N");
                        string connection1Guid = Guid.NewGuid().ToString("N");
                        string connection2Guid = Guid.NewGuid().ToString("N");
                        string utcNowStr = DateTime.UtcNow.ToString("o"); // ISO 8601 格式

                        var doc = new System.Xml.Linq.XDocument(
                            new System.Xml.Linq.XDeclaration("1.0", "utf-8", null),
                            new System.Xml.Linq.XElement("SessionFlowGraph",
                                new System.Xml.Linq.XAttribute("SchemaVersion", 2),
                                new System.Xml.Linq.XAttribute("GraphId", graphGuid),
                                new System.Xml.Linq.XAttribute("Name", string.Format(TryFindResource("Flow_GraphNameFormat") as string ?? "{0}会话流", option.DisplayName)),
                                new System.Xml.Linq.XAttribute("CreatedAtUtc", utcNowStr),
                                new System.Xml.Linq.XAttribute("UpdatedAtUtc", utcNowStr),
                                new System.Xml.Linq.XElement("Canvas", 
                                    new System.Xml.Linq.XAttribute("Width", 3200),
                                    new System.Xml.Linq.XAttribute("Height", 2000)
                                ),
                                new System.Xml.Linq.XElement("Nodes",
                                    new System.Xml.Linq.XElement("Node",
                                        new System.Xml.Linq.XAttribute("Id", userInputGuid),
                                        new System.Xml.Linq.XAttribute("Kind", "UserInput"),
                                        new System.Xml.Linq.XAttribute("Title", "用户输入"),
                                        new System.Xml.Linq.XAttribute("X", 20),
                                        new System.Xml.Linq.XAttribute("Y", 16),
                                        new System.Xml.Linq.XAttribute("Width", 220),
                                        new System.Xml.Linq.XAttribute("IsFixed", true),
                                        new System.Xml.Linq.XAttribute("AgentId", ""),
                                        new System.Xml.Linq.XAttribute("AgentDisplayName", ""),
                                        new System.Xml.Linq.XAttribute("IsHiddenAgent", false),
                                        new System.Xml.Linq.XElement("Inputs"),
                                        new System.Xml.Linq.XElement("Outputs",
                                            new System.Xml.Linq.XElement("Port",
                                                new System.Xml.Linq.XAttribute("Id", "output-user-text"),
                                                new System.Xml.Linq.XAttribute("Name", "自然语言输出"),
                                                new System.Xml.Linq.XAttribute("Direction", "Output"),
                                                new System.Xml.Linq.XAttribute("Type", "NaturalLanguage"),
                                                new System.Xml.Linq.XAttribute("IsFlexiblePlaceholder", false),
                                                new System.Xml.Linq.XAttribute("IsBooleanCondition", false),
                                                new System.Xml.Linq.XAttribute("IsTransparentOutput", false),
                                                new System.Xml.Linq.XAttribute("PairKey", "")
                                            )
                                        )
                                    ),
                                    new System.Xml.Linq.XElement("Node",
                                        new System.Xml.Linq.XAttribute("Id", returnGuid),
                                        new System.Xml.Linq.XAttribute("Kind", "Return"),
                                        new System.Xml.Linq.XAttribute("Title", "返回"),
                                        new System.Xml.Linq.XAttribute("X", 1072),
                                        new System.Xml.Linq.XAttribute("Y", 16),
                                        new System.Xml.Linq.XAttribute("Width", 220),
                                        new System.Xml.Linq.XAttribute("IsFixed", true),
                                        new System.Xml.Linq.XAttribute("AgentId", ""),
                                        new System.Xml.Linq.XAttribute("AgentDisplayName", ""),
                                        new System.Xml.Linq.XAttribute("IsHiddenAgent", false),
                                        new System.Xml.Linq.XElement("Inputs",
                                            new System.Xml.Linq.XElement("Port",
                                                new System.Xml.Linq.XAttribute("Id", "input-return-text"),
                                                new System.Xml.Linq.XAttribute("Name", "自然语言输入"),
                                                new System.Xml.Linq.XAttribute("Direction", "Input"),
                                                new System.Xml.Linq.XAttribute("Type", "NaturalLanguage"),
                                                new System.Xml.Linq.XAttribute("IsFlexiblePlaceholder", false),
                                                new System.Xml.Linq.XAttribute("IsBooleanCondition", false),
                                                new System.Xml.Linq.XAttribute("IsTransparentOutput", false),
                                                new System.Xml.Linq.XAttribute("PairKey", "")
                                            ),
                                            new System.Xml.Linq.XElement("Port",
                                                new System.Xml.Linq.XAttribute("Id", "input-return-xml"),
                                                new System.Xml.Linq.XAttribute("Name", "XML字段输入"),
                                                new System.Xml.Linq.XAttribute("Direction", "Input"),
                                                new System.Xml.Linq.XAttribute("Type", "XmlField"),
                                                new System.Xml.Linq.XAttribute("IsFlexiblePlaceholder", false),
                                                new System.Xml.Linq.XAttribute("IsBooleanCondition", false),
                                                new System.Xml.Linq.XAttribute("IsTransparentOutput", false),
                                                new System.Xml.Linq.XAttribute("PairKey", "")
                                            )
                                        ),
                                        new System.Xml.Linq.XElement("Outputs")
                                    ),
                                    new System.Xml.Linq.XElement("Node",
                                        new System.Xml.Linq.XAttribute("Id", agentGuid),
                                        new System.Xml.Linq.XAttribute("Kind", "Agent"),
                                        new System.Xml.Linq.XAttribute("Title", option.DisplayName),
                                        new System.Xml.Linq.XAttribute("X", 448),
                                        new System.Xml.Linq.XAttribute("Y", 16),
                                        new System.Xml.Linq.XAttribute("Width", 250),
                                        new System.Xml.Linq.XAttribute("IsFixed", false),
                                        new System.Xml.Linq.XAttribute("AgentId", option.AgentId),
                                        new System.Xml.Linq.XAttribute("AgentDisplayName", option.DisplayName),
                                        new System.Xml.Linq.XAttribute("IsHiddenAgent", false),
                                        new System.Xml.Linq.XElement("Inputs",
                                            new System.Xml.Linq.XElement("Port",
                                                new System.Xml.Linq.XAttribute("Id", "agent-input"),
                                                new System.Xml.Linq.XAttribute("Name", "文本输入"),
                                                new System.Xml.Linq.XAttribute("Direction", "Input"),
                                                new System.Xml.Linq.XAttribute("Type", "NaturalLanguage"),
                                                new System.Xml.Linq.XAttribute("IsFlexiblePlaceholder", false),
                                                new System.Xml.Linq.XAttribute("IsBooleanCondition", false),
                                                new System.Xml.Linq.XAttribute("IsTransparentOutput", false),
                                                new System.Xml.Linq.XAttribute("PairKey", "")
                                            )
                                        ),
                                        new System.Xml.Linq.XElement("Outputs",
                                            new System.Xml.Linq.XElement("Port",
                                                new System.Xml.Linq.XAttribute("Id", "agent-output"),
                                                new System.Xml.Linq.XAttribute("Name", "文本输出"),
                                                new System.Xml.Linq.XAttribute("Direction", "Output"),
                                                new System.Xml.Linq.XAttribute("Type", "NaturalLanguage"),
                                                new System.Xml.Linq.XAttribute("IsFlexiblePlaceholder", false),
                                                new System.Xml.Linq.XAttribute("IsBooleanCondition", false),
                                                new System.Xml.Linq.XAttribute("IsTransparentOutput", false),
                                                new System.Xml.Linq.XAttribute("PairKey", "")
                                            )
                                        )
                                    )
                                ),
                                new System.Xml.Linq.XElement("Connections",
                                    new System.Xml.Linq.XElement("Connection",
                                        new System.Xml.Linq.XAttribute("Id", connection1Guid),
                                        new System.Xml.Linq.XAttribute("SourceNodeId", userInputGuid),
                                        new System.Xml.Linq.XAttribute("SourcePortId", "output-user-text"),
                                        new System.Xml.Linq.XAttribute("TargetNodeId", agentGuid),
                                        new System.Xml.Linq.XAttribute("TargetPortId", "agent-input")
                                    ),
                                    new System.Xml.Linq.XElement("Connection",
                                        new System.Xml.Linq.XAttribute("Id", connection2Guid),
                                        new System.Xml.Linq.XAttribute("SourceNodeId", agentGuid),
                                        new System.Xml.Linq.XAttribute("SourcePortId", "agent-output"),
                                        new System.Xml.Linq.XAttribute("TargetNodeId", returnGuid),
                                        new System.Xml.Linq.XAttribute("TargetPortId", "input-return-text")
                                    )
                                )
                            )
                        );
                        doc.Save(filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save session flow configs: " + ex.Message);
            }
        }

        /// <summary>
        /// 保存并应用 Shell 集成配置，向注册表注册右键菜单
        /// </summary>
        private void SaveShellIntegrationConfig()
        {
            SaveShellIntegrationConfig(
                ChkShellIntegration.IsChecked == true,
                TxtDestPath.Text,
                TryFindResource("Shell_Menu_Text") as string ?? "Ask Ferrita...");
        }

        private void SaveShellIntegrationConfig(bool isEnabled, string destPath, string menuText)
        {
            try
            {
                string configDir = GetTrueConfigurationDirectoryPath();
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, "ShellIntegration.xml");

                var doc = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("ShellIntegrationConfiguration",
                        new System.Xml.Linq.XAttribute("SchemaVersion", 1),
                        new System.Xml.Linq.XElement("IsEnabled", isEnabled),
                        new System.Xml.Linq.XElement("SessionFlow",
                            new System.Xml.Linq.XElement("GraphId", string.Empty),
                            new System.Xml.Linq.XElement("GraphName", string.Empty),
                            new System.Xml.Linq.XElement("FilePath", string.Empty)
                        )
                    )
                );

                doc.Save(filePath);

                // 应用注册表项
                ApplyShellIntegrationRegistration(isEnabled, destPath, menuText);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save shell integration config: " + ex.Message);
            }
        }

        private const string ShellVerbName = "Ferrita.ShellChat";
        private const string ShellFileVerbPath = @"Software\Classes\*\shell\" + ShellVerbName;
        private const string ShellDirectoryVerbPath = @"Software\Classes\Directory\shell\" + ShellVerbName;
        private const string ShellDirectoryBackgroundVerbPath = @"Software\Classes\Directory\Background\shell\" + ShellVerbName;

        private void ApplyShellIntegrationRegistration(bool isEnabled, string destPath, string menuText)
        {
            try
            {
                if (isEnabled)
                {
                    var executablePath = Path.Combine(destPath, "Ferrita.exe");
                    var iconValue = $"\"{executablePath}\",0";
                    RegisterShellVerb(
                        ShellFileVerbPath,
                        menuText,
                        iconValue,
                        $"\"{executablePath}\" --shell-chat --shell-context \"%1\"",
                        supportsMultiSelect: true);
                    RegisterShellVerb(
                        ShellDirectoryVerbPath,
                        menuText,
                        iconValue,
                        $"\"{executablePath}\" --shell-chat --shell-context \"%1\"",
                        supportsMultiSelect: false);
                    RegisterShellVerb(
                        ShellDirectoryBackgroundVerbPath,
                        menuText,
                        iconValue,
                        $"\"{executablePath}\" --shell-chat --shell-background \"%V\"",
                        supportsMultiSelect: false);
                }
                else
                {
                    DeleteShellVerb(ShellFileVerbPath);
                    DeleteShellVerb(ShellDirectoryVerbPath);
                    DeleteShellVerb(ShellDirectoryBackgroundVerbPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to apply shell integration registry: " + ex.Message);
            }
        }

        private string GetTrueConfigurationDirectoryPath()
        {
            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") 
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
            }

            string defaultDir = Path.Combine(userProfile, "Ferrita", "Configuration");
            string directoriesXmlPath = Path.Combine(defaultDir, "Directories.xml");

            if (File.Exists(directoriesXmlPath))
            {
                try
                {
                    var document = System.Xml.Linq.XDocument.Load(directoriesXmlPath);
                    var root = document.Root;
                    if (root != null)
                    {
                        var configDirEl = root.Element("ConfigurationDirectory");
                        if (configDirEl != null && !string.IsNullOrWhiteSpace(configDirEl.Value))
                        {
                            string rawPath = configDirEl.Value.Trim();
                            string expandedPath = Environment.ExpandEnvironmentVariables(rawPath);
                            return Path.GetFullPath(expandedPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to load/parse Directories.xml: " + ex.Message);
                }
            }

            return defaultDir;
        }

        private string GetTrueSessionFlowsDirectoryPath()
        {
            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") 
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
            }

            string defaultDir = Path.Combine(userProfile, "Ferrita", "Nodegraphs");
            string defaultConfigDir = Path.Combine(userProfile, "Ferrita", "Configuration");
            string directoriesXmlPath = Path.Combine(defaultConfigDir, "Directories.xml");

            if (File.Exists(directoriesXmlPath))
            {
                try
                {
                    var document = System.Xml.Linq.XDocument.Load(directoriesXmlPath);
                    var root = document.Root;
                    if (root != null)
                    {
                        var flowsDirEl = root.Element("SessionFlowsDirectory");
                        if (flowsDirEl != null && !string.IsNullOrWhiteSpace(flowsDirEl.Value))
                        {
                            string rawPath = flowsDirEl.Value.Trim();
                            string expandedPath = Environment.ExpandEnvironmentVariables(rawPath);
                            return Path.GetFullPath(expandedPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to load/parse Directories.xml: " + ex.Message);
                }
            }

            return defaultDir;
        }

        /// <summary>
        /// 保存选中的语言到 Ferrita 的 AppData 配置文件中
        /// </summary>
        private void SaveLocalizationConfig(string selectedLanguage)
        {
            try
            {
                string configDir = GetTrueConfigurationDirectoryPath();
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, "Localization.xml");

                var doc = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("LocalizationConfiguration",
                        new System.Xml.Linq.XAttribute("SchemaVersion", 1),
                        new System.Xml.Linq.XElement("LanguageCode", selectedLanguage)
                    )
                );
                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save localization configuration: " + ex.Message);
            }
        }

        /// <summary>
        /// 保存侧向文件系统配置到 Ferrita 的 AppData 配置文件中
        /// </summary>
        private void SaveLateralFileSystemConfig(bool enableLfs, string lfsWorkingDir)
        {
            try
            {
                string configDir = GetTrueConfigurationDirectoryPath();
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, "LateralFileSystem.xml");

                var doc = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("LateralFileSystemConfiguration",
                        new System.Xml.Linq.XAttribute("SchemaVersion", 1),
                        new System.Xml.Linq.XElement("IsEnabled", enableLfs),
                        new System.Xml.Linq.XElement("WorkingRootDirectory", lfsWorkingDir ?? string.Empty)
                    )
                );
                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save lateral file system configuration: " + ex.Message);
            }
        }

        private void EnableProjFSFeature()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dism.exe",
                    Arguments = "/online /enable-feature /featurename:Client-ProjFS /norestart /all",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        process.WaitForExit();
                        if (process.ExitCode != 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"dism.exe exited with code {process.ExitCode}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to enable Windows ProjFS feature: " + ex.Message);
            }
        }

        private static void RegisterShellVerb(
            string verbPath,
            string menuText,
            string iconValue,
            string commandValue,
            bool supportsMultiSelect)
        {
            using (var verbKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(verbPath, writable: true))
            {
                if (verbKey == null) throw new InvalidOperationException($"Failed to create registry key: HKCU\\{verbPath}");

                verbKey.SetValue(string.Empty, menuText, Microsoft.Win32.RegistryValueKind.String);
                verbKey.SetValue("MUIVerb", menuText, Microsoft.Win32.RegistryValueKind.String);
                verbKey.SetValue("Icon", iconValue, Microsoft.Win32.RegistryValueKind.String);

                if (supportsMultiSelect)
                {
                    verbKey.SetValue("MultiSelectModel", "Player", Microsoft.Win32.RegistryValueKind.String);
                }
                else
                {
                    verbKey.DeleteValue("MultiSelectModel", throwOnMissingValue: false);
                }

                using (var commandKey = verbKey.CreateSubKey("command", writable: true))
                {
                    if (commandKey == null) throw new InvalidOperationException($"Failed to create registry key: HKCU\\{verbPath}\\command");
                    commandKey.SetValue(string.Empty, commandValue, Microsoft.Win32.RegistryValueKind.String);
                }
            }
        }

        private static void DeleteShellVerb(string verbPath)
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(verbPath, throwOnMissingSubKey: false);
        }

        /// <summary>
        /// 定时器 Tick 事件，模拟进度和状态刷新
        /// </summary>
        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_progressVal < 100)
            {
                // 每次增加随机值以模拟真实的复杂计算和文件释放的忽快忽慢节奏
                _progressVal += 1;
                if (_progressVal > 100) _progressVal = 100;

                ProgBar.Value = _progressVal;
                TxtProgressPercent.Text = $"{_progressVal}%";

                // 随着进度改变展示不同的状态信息
                if (_progressVal <= 10)
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_PrepareFiles") as string ?? "准备释放文件...";
                }
                else if (_progressVal <= 30)
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_ExtractCore") as string ?? "正在解压 Ferrita 核心运行库...";
                }
                else if (_progressVal <= 50)
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_ExtractDB") as string ?? "正在释放向量图数据库引擎 (AerialCity)...";
                }
                else if (_progressVal <= 70)
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_DeployDaemon") as string ?? "正在配置后台守护进程 (Ferrita)...";
                }
                else if (_progressVal <= 85)
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_ConfigureNetwork") as string ?? "正在配置本地端口与网络通道 (Tunnel-Next)...";
                }
                else if (_progressVal <= 95)
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_CreateShortcuts") as string ?? "正在创建系统快捷方式与初始化环境变量...";
                }
                else
                {
                    TxtProgressStatus.Text = TryFindResource("Progress_CleanCache") as string ?? "正在清理临时安装程序缓存...";
                }

                // 随机微调下次触发的时间间隔以增强“不均匀速度”的真实感
                return;
            }
            else
            {
                // 进度到 100% 后关闭定时器，自动进入最后完成一步
                _currentStep = 10;
                UpdateStepUI();
            }
        }

        private void ComboLanguage_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboLanguage == null) return;
            var selectedItem = ComboLanguage.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (selectedItem != null)
            {
                string tag = selectedItem.Tag as string ?? "en-US";
                SwitchLanguage(tag);
                UpdateLocalizedContent();
            }
        }

        private void SwitchLanguage(string cultureCode)
        {
            try
            {
                string resourcePath = $"Resources/Strings/Strings.{cultureCode}.xaml";
                var newDict = new ResourceDictionary
                {
                    Source = new Uri(resourcePath, UriKind.RelativeOrAbsolute)
                };

                if (Application.Current.Resources.MergedDictionaries.Count > 0)
                {
                    Application.Current.Resources.MergedDictionaries[0] = newDict;
                }
                else
                {
                    Application.Current.Resources.MergedDictionaries.Add(newDict);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SwitchLanguage failed: " + ex.Message);
            }
        }

        private void UpdateLocalizedContent()
        {
            if (MainTabControl == null) return;
            // 1. 更新协议文本
            if (TxtLicenseAgreement != null)
            {
                TxtLicenseAgreement.Text = TryFindResource("License_Text") as string ?? string.Empty;
            }

            // 2. 更新功能层级 (CapabilityLayers)
            if (CapabilityLayers != null && CapabilityLayers.Count >= 3)
            {
                CapabilityLayers[0].Name = TryFindResource("Layer_ContextCompression_Name") as string ?? "上下文压缩";
                CapabilityLayers[1].Name = TryFindResource("Layer_UtilityIFast_Name") as string ?? "实用I（快速）";
                CapabilityLayers[2].Name = TryFindResource("Layer_UtilityIISmart_Name") as string ?? "实用II（智能）";

                foreach (var layer in CapabilityLayers)
                {
                    layer.RaisePropertyChanged(nameof(layer.Name));
                }
            }

            // 3. 更新预置智能体配置 (AgentOptions)
            if (AgentOptions != null)
            {
                foreach (var opt in AgentOptions)
                {
                    if (opt.AgentId == "NiceChatter")
                    {
                        opt.DisplayName = TryFindResource("Agent_NiceChatter_Name") as string ?? "NiceChatter";
                        opt.Description = TryFindResource("Agent_NiceChatter_Desc") as string ?? "友善的助理，帮助用户进行普通聊天与各项日常任务。";
                    }
                    else if (opt.AgentId == "Coder")
                    {
                        opt.DisplayName = TryFindResource("Agent_Coder_Name") as string ?? "Coder";
                        opt.Description = TryFindResource("Agent_Coder_Desc") as string ?? "专业的编程智能体，擅长在工作区中与用户协作解决复杂的代码问题。";
                    }
                    else if (opt.AgentId == "CoderWithPowershell")
                    {
                        opt.DisplayName = TryFindResource("Agent_CoderWithPowershell_Name") as string ?? "Coder With Powershell";
                        opt.Description = TryFindResource("Agent_CoderWithPowershell_Desc") as string ?? "拥有命令行权限的编程智能体，可以直接在工作区运行 PowerShell 命令调试项目。";
                    }
                    else if (opt.AgentId == "Puterperson")
                    {
                        opt.DisplayName = TryFindResource("Agent_Puterperson_Name") as string ?? "Puterperson";
                        opt.Description = TryFindResource("Agent_Puterperson_Desc") as string ?? "计算机的化身，帮助用户深度操作计算机系统并自动化完成复杂的工作。";
                    }
                    else if (opt.AgentId == "Investigator")
                    {
                        opt.DisplayName = TryFindResource("Agent_Investigator_Name") as string ?? "Investigator";
                        opt.Description = TryFindResource("Agent_Investigator_Desc") as string ?? "探索型子智能体，主要执行代码库的深度搜索、文件树分析与结构调查。";
                    }
                    else if (opt.AgentId == "GPSubAgent")
                    {
                        opt.DisplayName = TryFindResource("Agent_GPSubAgent_Name") as string ?? "General Purpose Subagent";
                        opt.Description = TryFindResource("Agent_GPSubAgent_Desc") as string ?? "通用子智能体，根据主智能体派发的任务，自主规划并使用各种工具协助解决任务。";
                    }

                    opt.RaisePropertyChanged(nameof(opt.DisplayName));
                    opt.RaisePropertyChanged(nameof(opt.Description));
                }
            }

            // 4. 更新会话流 (FlowOptions)
            if (FlowOptions != null)
            {
                var flowDescFormat = TryFindResource("Flow_Desc_Template") as string ?? "使用 {0} 代理自动生成默认会话流节点图文件 ({1}.xml)。";
                foreach (var opt in FlowOptions)
                {
                    AgentConfigOption? agentOpt = null;
                    if (AgentOptions != null)
                    {
                        foreach (var a in AgentOptions)
                        {
                            if (a.AgentId == opt.AgentId)
                            {
                                agentOpt = a;
                                break;
                            }
                        }
                    }
                    if (agentOpt != null)
                    {
                        opt.DisplayName = agentOpt.DisplayName;
                        opt.Description = string.Format(flowDescFormat, agentOpt.DisplayName, opt.AgentId);
                    }

                    opt.RaisePropertyChanged(nameof(opt.DisplayName));
                    opt.RaisePropertyChanged(nameof(opt.Description));
                }
            }

            // 5. 更新底部操作栏与当前步骤的状态字与按钮文字
            UpdateStepUI();
        }
    }
}
