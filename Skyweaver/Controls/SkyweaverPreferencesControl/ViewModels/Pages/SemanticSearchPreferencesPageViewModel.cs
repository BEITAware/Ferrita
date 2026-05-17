using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Services;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.AerialCityRag;
using Skyweaver.Services.AerialCityRag;
using Skyweaver.Services.Directories;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels.Pages
{
    public sealed class SemanticSearchPreferencesPageViewModel : ObservableObject
    {
        private readonly AerialCityRagConfigurationRepository _configurationRepository;
        private readonly EmbeddingModelConfigurationRepository _embeddingModelRepository;
        private AerialCityRagConfiguration _configuration;
        private EmbeddingModelOption? _selectedEmbeddingModel;
        private string _statusMessage = "语义搜索配置已加载。";

        public SemanticSearchPreferencesPageViewModel()
        {
            _configurationRepository = new AerialCityRagConfigurationRepository();
            _embeddingModelRepository = new EmbeddingModelConfigurationRepository(new EmbeddingModelConfigurationPathProvider());
            _configuration = _configurationRepository.Load();

            RefreshEmbeddingModelsCommand = new RelayCommand(RefreshEmbeddingModels);
            OpenConfigurationDirectoryCommand = new RelayCommand(OpenConfigurationDirectory);

            RefreshEmbeddingModels();
        }

        public string Title { get; } = "语义搜索";

        public string Description { get; } = "配置 AerialCity RAG 工具的启用状态与检索时使用的嵌入模型。";

        public ObservableCollection<EmbeddingModelOption> EmbeddingModels { get; } = new();

        public bool IsAerialCityRagEnabled
        {
            get => _configuration.IsEnabled;
            set
            {
                if (_configuration.IsEnabled == value)
                {
                    return;
                }

                _configuration.IsEnabled = value;
                OnPropertyChanged();
                PersistConfiguration(value ? "AerialCity RAG 已启用。" : "AerialCity RAG 已关闭。");
            }
        }

        public EmbeddingModelOption? SelectedEmbeddingModel
        {
            get => _selectedEmbeddingModel;
            set
            {
                if (!SetProperty(ref _selectedEmbeddingModel, value))
                {
                    return;
                }

                _configuration.SelectedEmbeddingModelKey = value?.Key ?? string.Empty;
                OnPropertyChanged(nameof(SelectedEmbeddingModelSummary));
                PersistConfiguration(value == null ? "嵌入模型选择已清空。" : $"已选择嵌入模型：{value.DisplayName}");
            }
        }

        public string SelectedEmbeddingModelSummary => SelectedEmbeddingModel?.Summary ?? "未选择嵌入模型";

        public string AerialCityDirectoryPath => SkyweaverDirectoryRuntime.Instance.AerialCityDirectoryPath;

        public string ConfigurationFilePath => _configurationRepository.ConfigurationFilePath;

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public ICommand RefreshEmbeddingModelsCommand { get; }

        public ICommand OpenConfigurationDirectoryCommand { get; }

        private void RefreshEmbeddingModels()
        {
            var selectedKey = _configuration.SelectedEmbeddingModelKey;
            EmbeddingModels.Clear();

            foreach (var model in _embeddingModelRepository.Load())
            {
                EmbeddingModels.Add(EmbeddingModelOption.FromDefinition(model));
            }

            SelectedEmbeddingModel = EmbeddingModels.FirstOrDefault(model =>
                    string.Equals(model.Key, selectedKey, StringComparison.Ordinal)) ??
                EmbeddingModels.FirstOrDefault();

            if (SelectedEmbeddingModel != null && !string.Equals(selectedKey, SelectedEmbeddingModel.Key, StringComparison.Ordinal))
            {
                _configuration.SelectedEmbeddingModelKey = SelectedEmbeddingModel.Key;
                PersistConfiguration($"已自动选择嵌入模型：{SelectedEmbeddingModel.DisplayName}");
                return;
            }

            StatusMessage = EmbeddingModels.Count == 0
                ? "尚未配置嵌入模型。请先在嵌入模型配置面板中添加模型。"
                : $"已获取 {EmbeddingModels.Count} 个嵌入模型。";
        }

        private void OpenConfigurationDirectory()
        {
            try
            {
                var directoryPath = Path.GetDirectoryName(ConfigurationFilePath) ?? SkyweaverDirectoryRuntime.Instance.ConfigurationDirectoryPath;
                Directory.CreateDirectory(directoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = directoryPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开配置目录失败：{ex.Message}";
            }
        }

        private void PersistConfiguration(string successMessage)
        {
            try
            {
                _configurationRepository.Save(_configuration);
                _configuration = _configurationRepository.Load();
                OnPropertyChanged(nameof(IsAerialCityRagEnabled));
                OnPropertyChanged(nameof(AerialCityDirectoryPath));
                OnPropertyChanged(nameof(ConfigurationFilePath));
                StatusMessage = successMessage;
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存失败：{ex.Message}";
            }
        }

        public sealed class EmbeddingModelOption
        {
            public required string Key { get; init; }

            public required string DisplayName { get; init; }

            public required string Summary { get; init; }

            public bool IsFullyConfigured { get; init; }

            public bool SupportsMultimodalEmbedding { get; init; }

            public static EmbeddingModelOption FromDefinition(EmbeddingModelDefinition definition)
            {
                var displayName = string.IsNullOrWhiteSpace(definition.DisplayName)
                    ? definition.SummaryModelId
                    : definition.DisplayName;

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = definition.Key;
                }

                var readiness = definition.IsFullyConfigured ? "已配置" : "未完成";
                var multimodal = definition.SupportsMultimodalEmbedding ? "多模态" : "文本";
                var modelId = string.IsNullOrWhiteSpace(definition.SummaryModelId)
                    ? definition.InterfaceType
                    : $"{definition.InterfaceType} / {definition.SummaryModelId}";

                return new EmbeddingModelOption
                {
                    Key = definition.Key,
                    DisplayName = displayName,
                    Summary = $"{modelId}，{definition.Dimensions:N0}d，{multimodal}，{readiness}",
                    IsFullyConfigured = definition.IsFullyConfigured,
                    SupportsMultimodalEmbedding = definition.SupportsMultimodalEmbedding
                };
            }
        }
    }
}
