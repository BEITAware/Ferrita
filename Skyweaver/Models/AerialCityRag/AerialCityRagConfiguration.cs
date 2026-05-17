using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Models.AerialCityRag
{
    public sealed class AerialCityRagConfiguration : ObservableObject
    {
        private bool _isEnabled;
        private string _selectedEmbeddingModelKey = string.Empty;

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public string SelectedEmbeddingModelKey
        {
            get => _selectedEmbeddingModelKey;
            set => SetProperty(ref _selectedEmbeddingModelKey, value?.Trim() ?? string.Empty);
        }
    }
}
