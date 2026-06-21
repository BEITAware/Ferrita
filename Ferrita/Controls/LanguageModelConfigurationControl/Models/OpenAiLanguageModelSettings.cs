using System;
using System.Collections.Generic;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Models
{
    public class OpenAiLanguageModelSettings : LanguageModelInterfaceSettings
    {
        private static readonly string[] s_supportedReasoningEfforts = ["Low", "Medium", "High"];
        private string _modelId = string.Empty;
        private string _apiKey = string.Empty;
        private string _baseUrl = "https://api.openai.com/v1";
        private bool _useTemperature;
        private decimal _temperature = 1.0m;
        private bool _useTopP;
        private decimal _topP = 1.0m;
        private bool _useMaxOutputTokens;
        private int _maxOutputTokens = 2048;
        private bool _usePresencePenalty;
        private decimal _presencePenalty;
        private bool _useFrequencyPenalty;
        private decimal _frequencyPenalty;
        private bool _useSeed;
        private long _seed;
        private bool _useReasoningEffort;
        private string _reasoningEffort = "Medium";
        private bool _useReasoningOutput = true;
        private string _reasoningOutput = "Full";

        public override string InterfaceType => "OpenAI Chat Completions API";

        public IReadOnlyList<string> SupportedReasoningEfforts => s_supportedReasoningEfforts;

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

        public bool UsePresencePenalty
        {
            get => _usePresencePenalty;
            set => SetProperty(ref _usePresencePenalty, value);
        }

        public decimal PresencePenalty
        {
            get => _presencePenalty;
            set => SetProperty(ref _presencePenalty, value);
        }

        public bool UseFrequencyPenalty
        {
            get => _useFrequencyPenalty;
            set => SetProperty(ref _useFrequencyPenalty, value);
        }

        public decimal FrequencyPenalty
        {
            get => _frequencyPenalty;
            set => SetProperty(ref _frequencyPenalty, value);
        }

        public bool UseSeed
        {
            get => _useSeed;
            set => SetProperty(ref _useSeed, value);
        }

        public long Seed
        {
            get => _seed;
            set => SetProperty(ref _seed, value);
        }

        public bool UseReasoningEffort
        {
            get => _useReasoningEffort;
            set => SetProperty(ref _useReasoningEffort, value);
        }

        public string ReasoningEffort
        {
            get => _reasoningEffort;
            set => SetProperty(ref _reasoningEffort, NormalizeReasoningEffort(value));
        }

        public bool UseReasoningOutput
        {
            get => _useReasoningOutput;
            set => SetProperty(ref _useReasoningOutput, value);
        }

        public string ReasoningOutput
        {
            get => _reasoningOutput;
            set => SetProperty(ref _reasoningOutput, string.IsNullOrWhiteSpace(value) ? "Full" : value.Trim());
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

        private static string NormalizeReasoningEffort(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "LOW" => "Low",
                "MEDIUM" => "Medium",
                "HIGH" => "High",
                "EXTRAHIGH" => "High",
                "EXTRA_HIGH" => "High",
                _ => "Medium"
            };
        }
    }
}
