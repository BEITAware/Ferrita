namespace Ferrita.Controls.EmbeddingModelConfigurationControl.Models
{
    public sealed class OpenAiEmbeddingModelSettings : EmbeddingModelInterfaceSettings
    {
        private string _modelId = string.Empty;
        private string _apiKey = string.Empty;
        private string _baseUrl = "https://api.openai.com/v1";
        private string _user = string.Empty;

        public override string InterfaceType => "OpenAI";

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

        public string User
        {
            get => _user;
            set => SetProperty(ref _user, value?.Trim() ?? string.Empty);
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
    }
}
