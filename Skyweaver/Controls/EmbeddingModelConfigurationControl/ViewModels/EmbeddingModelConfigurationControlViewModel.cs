using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Services;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.ViewModels
{
    public sealed class EmbeddingModelConfigurationControlViewModel : ObservableObject
    {
        private readonly EmbeddingModelConfigurationRepository _embeddingModelRepository;
        private readonly IEmbeddingModelTestService _embeddingModelTestService;
        private readonly Dictionary<string, CancellationTokenSource> _testCancellationSources = new(StringComparer.Ordinal);
        private bool _isLoading;
        private EmbeddingModelDefinition? _selectedEmbeddingModel;

        public EmbeddingModelConfigurationControlViewModel()
        {
            var pathProvider = new EmbeddingModelConfigurationPathProvider();
            _embeddingModelRepository = new EmbeddingModelConfigurationRepository(pathProvider);
            _embeddingModelTestService = new EmbeddingModelTestService();

            AddEmbeddingModelCommand = new RelayCommand(AddEmbeddingModel);
            DuplicateEmbeddingModelCommand = new RelayCommand(DuplicateSelectedEmbeddingModel, () => SelectedEmbeddingModel != null);
            RemoveEmbeddingModelCommand = new RelayCommand(RemoveSelectedEmbeddingModel, () => SelectedEmbeddingModel != null);
            OpenConfigurationDirectoryCommand = new RelayCommand(OpenConfigurationDirectory);

            EmbeddingModels.CollectionChanged += OnEmbeddingModelsCollectionChanged;

            Load();
        }

        public string Title { get; } = "嵌入模型配置";

        public string Description { get; } = "配置向量嵌入模型连接信息，并用于 AerialCity 检索与嵌入调用。";

        public ObservableCollection<EmbeddingModelDefinition> EmbeddingModels { get; } = new();

        public IReadOnlyList<string> AvailableInterfaceTypes => EmbeddingModelDefinition.AvailableInterfaceTypes;

        public string InterfaceSettingsSectionTitle => SelectedEmbeddingModel == null
            ? "接口配置"
            : $"{SelectedEmbeddingModel.InterfaceType} 接口配置";

        public string EmbeddingModelConfigurationFilePath => _embeddingModelRepository.ConfigurationFilePath;

        public EmbeddingModelDefinition? SelectedEmbeddingModel
        {
            get => _selectedEmbeddingModel;
            set
            {
                if (SetProperty(ref _selectedEmbeddingModel, value))
                {
                    OnPropertyChanged(nameof(InterfaceSettingsSectionTitle));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public ICommand AddEmbeddingModelCommand { get; }

        public ICommand DuplicateEmbeddingModelCommand { get; }

        public ICommand RemoveEmbeddingModelCommand { get; }

        public ICommand OpenConfigurationDirectoryCommand { get; }

        private void Load()
        {
            _isLoading = true;

            try
            {
                foreach (var model in _embeddingModelRepository.Load())
                {
                    AttachEmbeddingModel(model);
                    EmbeddingModels.Add(model);
                }

                SelectedEmbeddingModel = EmbeddingModels.FirstOrDefault();
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void AddEmbeddingModel()
        {
            var model = new EmbeddingModelDefinition
            {
                DisplayName = $"嵌入模型 {EmbeddingModels.Count + 1}",
                InterfaceType = EmbeddingModelInterfaceCatalog.DefaultInterfaceType
            };

            ApplySuggestedDefaults(model);

            AttachEmbeddingModel(model);
            EmbeddingModels.Add(model);
            SelectedEmbeddingModel = model;
            PersistAll();
        }

        private void RemoveSelectedEmbeddingModel()
        {
            if (SelectedEmbeddingModel == null)
            {
                return;
            }

            DetachEmbeddingModel(SelectedEmbeddingModel);
            EmbeddingModels.Remove(SelectedEmbeddingModel);
            SelectedEmbeddingModel = EmbeddingModels.FirstOrDefault();
            PersistAll();
        }

        private void DuplicateSelectedEmbeddingModel()
        {
            if (SelectedEmbeddingModel == null)
            {
                return;
            }

            var clone = CloneEmbeddingModel(SelectedEmbeddingModel);
            AttachEmbeddingModel(clone);
            EmbeddingModels.Add(clone);
            SelectedEmbeddingModel = clone;
            PersistAll();
        }

        private void OpenConfigurationDirectory()
        {
            var directoryPath = Path.GetDirectoryName(EmbeddingModelConfigurationFilePath) ?? string.Empty;
            Directory.CreateDirectory(directoryPath);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true
            });
        }

        private void OnEmbeddingModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (EmbeddingModelDefinition item in e.NewItems)
                {
                    AttachEmbeddingModel(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (EmbeddingModelDefinition item in e.OldItems)
                {
                    DetachEmbeddingModel(item);
                }
            }

            OnPropertyChanged(nameof(EmbeddingModels));
            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachEmbeddingModel(EmbeddingModelDefinition model)
        {
            model.SetTestAction(TestEmbeddingModelAsync);
            model.SetCancelTestAction(CancelEmbeddingModelTest);
            model.PropertyChanged -= OnEmbeddingModelPropertyChanged;
            model.PropertyChanged += OnEmbeddingModelPropertyChanged;
        }

        private void DetachEmbeddingModel(EmbeddingModelDefinition model)
        {
            model.PropertyChanged -= OnEmbeddingModelPropertyChanged;
            CancelAndDisposeTest(model.Key);
        }

        private void OnEmbeddingModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(EmbeddingModelDefinition.TestResponse), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(EmbeddingModelDefinition.IsTesting), StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(EmbeddingModelDefinition.InterfaceType), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(InterfaceSettingsSectionTitle));
            }

            PersistAll();
        }

        private void PersistAll()
        {
            if (_isLoading)
            {
                return;
            }

            try
            {
                _embeddingModelRepository.Save(EmbeddingModels);
            }
            catch (Exception ex)
            {
                if (SelectedEmbeddingModel != null)
                {
                    SelectedEmbeddingModel.TestResponse = $"保存失败：{ex.Message}";
                }
            }
        }

        private async Task TestEmbeddingModelAsync(EmbeddingModelDefinition model)
        {
            using var cancellationSource = new CancellationTokenSource();
            RegisterTestCancellation(model.Key, cancellationSource);

            model.IsTesting = true;
            model.CanCancelTest = true;
            model.TestResponse = "正在生成测试嵌入...";

            try
            {
                model.TestResponse = await _embeddingModelTestService.TestAsync(
                    model,
                    cancellationSource.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                model.TestResponse = "测试已中止。";
            }
            catch (Exception ex)
            {
                model.TestResponse = $"测试失败：{ex.Message}";
            }
            finally
            {
                UnregisterTestCancellation(model.Key, cancellationSource);
                model.CanCancelTest = false;
                model.IsTesting = false;
            }
        }

        private void CancelEmbeddingModelTest(EmbeddingModelDefinition model)
        {
            if (_testCancellationSources.TryGetValue(model.Key, out var cancellationSource))
            {
                cancellationSource.Cancel();
            }
        }

        private void RegisterTestCancellation(string modelKey, CancellationTokenSource cancellationSource)
        {
            CancelAndDisposeTest(modelKey);
            _testCancellationSources[modelKey] = cancellationSource;
        }

        private void UnregisterTestCancellation(string modelKey, CancellationTokenSource cancellationSource)
        {
            if (_testCancellationSources.TryGetValue(modelKey, out var current) && ReferenceEquals(current, cancellationSource))
            {
                _testCancellationSources.Remove(modelKey);
            }
        }

        private void CancelAndDisposeTest(string modelKey)
        {
            if (_testCancellationSources.TryGetValue(modelKey, out var cancellationSource))
            {
                _testCancellationSources.Remove(modelKey);
                cancellationSource.Cancel();
                cancellationSource.Dispose();
            }
        }

        private static EmbeddingModelDefinition CloneEmbeddingModel(EmbeddingModelDefinition source)
        {
            return new EmbeddingModelDefinition
            {
                DisplayName = BuildDuplicatedDisplayName(source.DisplayName),
                InterfaceType = source.InterfaceType,
                Dimensions = source.Dimensions,
                MaxInputTokens = source.MaxInputTokens,
                Normalize = source.Normalize,
                SupportsMultimodalEmbedding = source.SupportsMultimodalEmbedding,
                IncludeBinaryDataInTextProjection = source.IncludeBinaryDataInTextProjection,
                InterfaceSettings = CloneInterfaceSettings(source.InterfaceSettings)
            };
        }

        private static EmbeddingModelInterfaceSettings CloneInterfaceSettings(EmbeddingModelInterfaceSettings source)
        {
            return source switch
            {
                OpenAiEmbeddingModelSettings openAi => new OpenAiEmbeddingModelSettings
                {
                    ModelId = openAi.ModelId,
                    ApiKey = openAi.ApiKey,
                    BaseUrl = openAi.BaseUrl,
                    User = openAi.User
                },
                GoogleEmbeddingModelSettings google => new GoogleEmbeddingModelSettings
                {
                    ModelId = google.ModelId,
                    ApiKey = google.ApiKey,
                    BaseUrl = google.BaseUrl,
                    UseTaskType = google.UseTaskType,
                    TaskType = google.TaskType,
                    SendInlineData = google.SendInlineData
                },
                _ => EmbeddingModelInterfaceCatalog.CreateInterfaceSettings(source.InterfaceType)
            };
        }

        private static string BuildDuplicatedDisplayName(string? displayName)
        {
            var normalizedName = string.IsNullOrWhiteSpace(displayName) ? "嵌入模型" : displayName.Trim();
            return $"{normalizedName} - 副本";
        }

        private static void ApplySuggestedDefaults(EmbeddingModelDefinition model)
        {
            switch (model.InterfaceSettings)
            {
                case OpenAiEmbeddingModelSettings openAi:
                    openAi.BaseUrl = "https://api.openai.com/v1";
                    break;

                case GoogleEmbeddingModelSettings google:
                    google.BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
                    google.TaskType = "RETRIEVAL_DOCUMENT";
                    break;
            }
        }
    }
}
