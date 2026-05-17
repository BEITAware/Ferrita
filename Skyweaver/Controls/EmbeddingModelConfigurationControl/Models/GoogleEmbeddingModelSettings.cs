namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Models
{
    public sealed class GoogleEmbeddingModelSettings : EmbeddingModelInterfaceSettings
    {
        private static readonly string[] s_supportedTaskTypes =
        [
            "RETRIEVAL_QUERY",
            "RETRIEVAL_DOCUMENT",
            "SEMANTIC_SIMILARITY",
            "CLASSIFICATION",
            "CLUSTERING",
            "QUESTION_ANSWERING",
            "FACT_VERIFICATION",
            "CODE_RETRIEVAL_QUERY"
        ];

        private string _modelId = string.Empty;
        private string _apiKey = string.Empty;
        private string _baseUrl = "https://generativelanguage.googleapis.com/v1beta";
        private bool _useTaskType;
        private string _taskType = "RETRIEVAL_DOCUMENT";
        private bool _sendInlineData;

        public override string InterfaceType => "GOOGLE";

        public IReadOnlyList<string> SupportedTaskTypes => s_supportedTaskTypes;

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

        public bool UseTaskType
        {
            get => _useTaskType;
            set => SetProperty(ref _useTaskType, value);
        }

        public string TaskType
        {
            get => _taskType;
            set => SetProperty(ref _taskType, NormalizeTaskType(value));
        }

        public bool SendInlineData
        {
            get => _sendInlineData;
            set => SetProperty(ref _sendInlineData, value);
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

        private static string NormalizeTaskType(string? value)
        {
            var normalizedValue = (value ?? string.Empty).Trim().ToUpperInvariant();
            return s_supportedTaskTypes.Contains(normalizedValue, StringComparer.Ordinal)
                ? normalizedValue
                : "RETRIEVAL_DOCUMENT";
        }
    }
}
