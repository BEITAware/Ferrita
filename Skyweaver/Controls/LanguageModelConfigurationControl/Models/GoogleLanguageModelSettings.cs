namespace Skyweaver.Controls.LanguageModelConfigurationControl.Models
{
    public sealed class GoogleLanguageModelSettings : LanguageModelInterfaceSettings
    {
        private static readonly string[] s_supportedThinkingLevels = ["Minimal", "Low", "Medium", "High"];

        private string _modelId = string.Empty;
        private string _apiKey = string.Empty;
        private string _baseUrl = "https://generativelanguage.googleapis.com";
        private bool _useTemperature;
        private decimal _temperature = 1.0m;
        private bool _useTopP;
        private decimal _topP = 0.95m;
        private bool _useMaxOutputTokens;
        private int _maxOutputTokens = 2048;
        private bool _useThinkingLevel;
        private string _thinkingLevel = "High";
        private bool _useThinkingBudget;
        private int _thinkingBudget = -1;
        private bool _includeThoughts = true;

        public override string InterfaceType => "GOOGLE";

        public IReadOnlyList<string> SupportedThinkingLevels => s_supportedThinkingLevels;

        public string ModelId
        {
            get => _modelId;
            set
            {
                if (SetProperty(ref _modelId, value?.Trim() ?? string.Empty))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (SetProperty(ref _apiKey, value?.Trim() ?? string.Empty))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                if (SetProperty(ref _baseUrl, value?.Trim() ?? string.Empty))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public bool UseTemperature
        {
            get => _useTemperature;
            set => SetProperty(ref _useTemperature, value);
        }

        public decimal Temperature
        {
            get => _temperature;
            set => SetProperty(ref _temperature, value);
        }

        public bool UseTopP
        {
            get => _useTopP;
            set => SetProperty(ref _useTopP, value);
        }

        public decimal TopP
        {
            get => _topP;
            set => SetProperty(ref _topP, value);
        }

        public bool UseMaxOutputTokens
        {
            get => _useMaxOutputTokens;
            set => SetProperty(ref _useMaxOutputTokens, value);
        }

        public int MaxOutputTokens
        {
            get => _maxOutputTokens;
            set => SetProperty(ref _maxOutputTokens, value);
        }

        public bool UseThinkingLevel
        {
            get => _useThinkingLevel;
            set => SetProperty(ref _useThinkingLevel, value);
        }

        public string ThinkingLevel
        {
            get => _thinkingLevel;
            set => SetProperty(ref _thinkingLevel, NormalizeThinkingLevel(value));
        }

        public bool UseThinkingBudget
        {
            get => _useThinkingBudget;
            set => SetProperty(ref _useThinkingBudget, value);
        }

        public int ThinkingBudget
        {
            get => _thinkingBudget;
            set => SetProperty(ref _thinkingBudget, value);
        }

        public bool IncludeThoughts
        {
            get => _includeThoughts;
            set => SetProperty(ref _includeThoughts, value);
        }

        public override bool IsFullyConfigured =>
            !string.IsNullOrWhiteSpace(ModelId) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(BaseUrl);

        public override string SummaryModelId => ModelId;

        private void NotifyDerivedStateChanged()
        {
            OnPropertyChanged(nameof(IsFullyConfigured));
            OnPropertyChanged(nameof(SummaryModelId));
        }

        private static string NormalizeThinkingLevel(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "MINIMAL" => "Minimal",
                "LOW" => "Low",
                "MEDIUM" => "Medium",
                "HIGH" => "High",
                _ => "High"
            };
        }
    }
}
