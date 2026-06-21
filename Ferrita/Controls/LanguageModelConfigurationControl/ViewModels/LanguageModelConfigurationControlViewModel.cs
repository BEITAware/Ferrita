using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows;
using System.Threading;
using Ferrita.Commands;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Controls.LanguageModelConfigurationControl.Services;
using Ferrita.Infrastructure.Mvvm;
using Ferrita.Services.Localization;

namespace Ferrita.Controls.LanguageModelConfigurationControl.ViewModels
{
    public sealed class LanguageModelConfigurationControlViewModel : ObservableObject
    {
        private readonly LanguageModelConfigurationRepository _languageModelRepository;
        private readonly CapabilityLayerConfigurationRepository _capabilityLayerRepository;
        private readonly ILanguageModelTestService _languageModelTestService;
        private readonly Dictionary<string, CancellationTokenSource> _testCancellationSources = new(StringComparer.Ordinal);
        private bool _isLoading;
        private LanguageModelDefinition? _selectedLanguageModel;
        private CapabilityLayerDefinition? _selectedCapabilityLayer;

        public string Title => L("LanguageModelConfiguration.Title", "语言模型配置");

        public string Description => L(
            "LanguageModelConfiguration.Description",
            "配置具体语言模型连接信息，并为上层功能定义可回退的模型调用顺序。");

        public ObservableCollection<LanguageModelDefinition> LanguageModels { get; } = new();

        public ObservableCollection<CapabilityLayerDefinition> CapabilityLayers { get; } = new();

        public IReadOnlyList<string> AvailableInterfaceTypes => LanguageModelDefinition.AvailableInterfaceTypes;

        public string InterfaceSettingsSectionTitle => SelectedLanguageModel == null
            ? L("LanguageModelConfiguration.InterfaceSettings.Default", "接口配置")
            : LF(
                "LanguageModelConfiguration.InterfaceSettings.Format",
                "{0} 接口配置",
                SelectedLanguageModel.InterfaceType);

        public string LanguageModelConfigurationFilePath => _languageModelRepository.ConfigurationFilePath;

        public string CapabilityLayerConfigurationFilePath => _capabilityLayerRepository.ConfigurationFilePath;

        public LanguageModelDefinition? SelectedLanguageModel
        {
            get => _selectedLanguageModel;
            set
            {
                if (SetProperty(ref _selectedLanguageModel, value))
                {
                    OnPropertyChanged(nameof(InterfaceSettingsSectionTitle));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public CapabilityLayerDefinition? SelectedCapabilityLayer
        {
            get => _selectedCapabilityLayer;
            set
            {
                if (SetProperty(ref _selectedCapabilityLayer, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public ICommand AddLanguageModelCommand { get; }

        public ICommand DuplicateLanguageModelCommand { get; }

        public ICommand RemoveLanguageModelCommand { get; }

        public ICommand AddCapabilityLayerCommand { get; }

        public ICommand RemoveCapabilityLayerCommand { get; }

        public ICommand AddCapabilityLayerEntryCommand { get; }

        public ICommand RemoveCapabilityLayerEntryCommand { get; }

        public ICommand MoveCapabilityLayerEntryUpCommand { get; }

        public ICommand MoveCapabilityLayerEntryDownCommand { get; }

        public ICommand OpenConfigurationDirectoryCommand { get; }

        public LanguageModelConfigurationControlViewModel()
        {
            var pathProvider = new LanguageModelConfigurationPathProvider();
            _languageModelRepository = new LanguageModelConfigurationRepository(pathProvider);
            _capabilityLayerRepository = new CapabilityLayerConfigurationRepository(pathProvider);
            _languageModelTestService = new LanguageModelTestService();

            AddLanguageModelCommand = new RelayCommand(AddLanguageModel);
            DuplicateLanguageModelCommand = new RelayCommand(DuplicateSelectedLanguageModel, () => SelectedLanguageModel != null);
            RemoveLanguageModelCommand = new RelayCommand(RemoveSelectedLanguageModel, () => SelectedLanguageModel != null);
            AddCapabilityLayerCommand = new RelayCommand(AddCapabilityLayer);
            RemoveCapabilityLayerCommand = new RelayCommand(RemoveSelectedCapabilityLayer, () => SelectedCapabilityLayer?.CanDelete == true);
            AddCapabilityLayerEntryCommand = new RelayCommand<CapabilityLayerDefinition>(AddCapabilityLayerEntry, layer => layer != null && LanguageModels.Count > 0);
            RemoveCapabilityLayerEntryCommand = new RelayCommand<CapabilityLayerEntry>(RemoveCapabilityLayerEntry, entry => entry != null);
            MoveCapabilityLayerEntryUpCommand = new RelayCommand<CapabilityLayerEntry>(MoveCapabilityLayerEntryUp, CanMoveCapabilityLayerEntryUp);
            MoveCapabilityLayerEntryDownCommand = new RelayCommand<CapabilityLayerEntry>(MoveCapabilityLayerEntryDown, CanMoveCapabilityLayerEntryDown);
            OpenConfigurationDirectoryCommand = new RelayCommand(OpenConfigurationDirectory);

            LanguageModels.CollectionChanged += OnLanguageModelsCollectionChanged;
            CapabilityLayers.CollectionChanged += OnCapabilityLayersCollectionChanged;
            LocalizationRuntime.Instance.LanguageChanged += (_, _) => RefreshLocalizedText();

            Load();
        }

        public string GetLanguageModelDisplayName(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return L("LanguageModelConfiguration.Status.NoModelSelected", "未选择语言模型");
            }

            var model = LanguageModels.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (model == null)
            {
                return L("LanguageModelConfiguration.Status.MissingModel", "引用的语言模型不存在");
            }

            return string.IsNullOrWhiteSpace(model.DisplayName)
                ? LF("LanguageModelConfiguration.UnnamedModelFormat", "未命名模型 ({0})", model.SummaryModelId)
                : model.DisplayName;
        }

        private void Load()
        {
            _isLoading = true;

            try
            {
                foreach (var model in _languageModelRepository.Load())
                {
                    AttachLanguageModel(model);
                    LanguageModels.Add(model);
                }

                foreach (var layer in _capabilityLayerRepository.Load())
                {
                    AttachCapabilityLayer(layer);
                    CapabilityLayers.Add(layer);
                }

                SelectedLanguageModel = LanguageModels.FirstOrDefault();
                SelectedCapabilityLayer = CapabilityLayers.FirstOrDefault();
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void AddLanguageModel()
        {
            var model = new LanguageModelDefinition
            {
                DisplayName = LF("LanguageModelConfiguration.NewModelNameFormat", "语言模型 {0}", LanguageModels.Count + 1),
                InterfaceType = LanguageModelInterfaceCatalog.DefaultInterfaceType
            };

            ApplySuggestedDefaults(model.InterfaceSettings);

            AttachLanguageModel(model);
            LanguageModels.Add(model);
            SelectedLanguageModel = model;
            PersistAll(L("LanguageModelConfiguration.Status.Added", "语言模型已新增并保存。"));
        }

        private void RemoveSelectedLanguageModel()
        {
            if (SelectedLanguageModel == null)
            {
                return;
            }

            var removedKey = SelectedLanguageModel.Key;
            DetachLanguageModel(SelectedLanguageModel);
            LanguageModels.Remove(SelectedLanguageModel);

            foreach (var layer in CapabilityLayers)
            {
                var invalidEntries = layer.LanguageModels
                    .Where(item => string.Equals(item.LanguageModelKey, removedKey, StringComparison.Ordinal))
                    .ToArray();

                foreach (var entry in invalidEntries)
                {
                    DetachCapabilityLayerEntry(entry);
                    layer.LanguageModels.Remove(entry);
                }
            }

            SelectedLanguageModel = LanguageModels.FirstOrDefault();
            PersistAll(L("LanguageModelConfiguration.Status.Removed", "语言模型已删除并保存。"), refreshCapabilityLayerDisplayNames: true);
        }

        private void DuplicateSelectedLanguageModel()
        {
            if (SelectedLanguageModel == null)
            {
                return;
            }

            var clone = CloneLanguageModel(SelectedLanguageModel);
            AttachLanguageModel(clone);
            LanguageModels.Add(clone);
            SelectedLanguageModel = clone;
            PersistAll(L("LanguageModelConfiguration.Status.Duplicated", "语言模型已复制并保存。"));
        }

        private void AddCapabilityLayer()
        {
            var layer = new CapabilityLayerDefinition
            {
                Name = LF("LanguageModelConfiguration.NewLayerNameFormat", "功能层级 {0}", CapabilityLayers.Count + 1)
            };

            AttachCapabilityLayer(layer);
            CapabilityLayers.Add(layer);
            SelectedCapabilityLayer = layer;
            PersistAll(L("LanguageModelConfiguration.Status.LayerAdded", "功能层级已新增并保存。"));
        }

        private void RemoveSelectedCapabilityLayer()
        {
            if (SelectedCapabilityLayer == null || !SelectedCapabilityLayer.CanDelete)
            {
                return;
            }

            DetachCapabilityLayer(SelectedCapabilityLayer);
            CapabilityLayers.Remove(SelectedCapabilityLayer);
            SelectedCapabilityLayer = CapabilityLayers.FirstOrDefault();
            PersistAll(L("LanguageModelConfiguration.Status.LayerRemoved", "功能层级已删除并保存。"));
        }

        private void AddCapabilityLayerEntry(CapabilityLayerDefinition? layer)
        {
            if (layer == null || LanguageModels.Count == 0)
            {
                return;
            }

            var entry = new CapabilityLayerEntry
            {
                LanguageModelKey = LanguageModels[0].Key
            };

            AttachCapabilityLayerEntry(entry);
            layer.LanguageModels.Add(entry);
            PersistAll(L("LanguageModelConfiguration.Status.LayerOrderSaved", "功能层级中的语言模型顺序已保存。"), refreshCapabilityLayerDisplayNames: true);
        }

        private void RemoveCapabilityLayerEntry(CapabilityLayerEntry? entry)
        {
            if (entry == null)
            {
                return;
            }

            var layer = FindParentLayer(entry);
            if (layer == null)
            {
                return;
            }

            DetachCapabilityLayerEntry(entry);
            layer.LanguageModels.Remove(entry);
            PersistAll(L("LanguageModelConfiguration.Status.LayerOrderSaved", "功能层级中的语言模型顺序已保存。"));
        }

        private bool CanMoveCapabilityLayerEntryUp(CapabilityLayerEntry? entry)
        {
            var layer = entry == null ? null : FindParentLayer(entry);
            return layer != null && layer.LanguageModels.IndexOf(entry!) > 0;
        }

        private void MoveCapabilityLayerEntryUp(CapabilityLayerEntry? entry)
        {
            if (entry == null)
            {
                return;
            }

            var layer = FindParentLayer(entry);
            if (layer == null)
            {
                return;
            }

            var index = layer.LanguageModels.IndexOf(entry);
            if (index <= 0)
            {
                return;
            }

            layer.LanguageModels.Move(index, index - 1);
            PersistAll(L("LanguageModelConfiguration.Status.LayerOrderSaved", "功能层级中的语言模型顺序已保存。"));
        }

        private bool CanMoveCapabilityLayerEntryDown(CapabilityLayerEntry? entry)
        {
            var layer = entry == null ? null : FindParentLayer(entry);
            return layer != null && layer.LanguageModels.IndexOf(entry!) < layer.LanguageModels.Count - 1;
        }

        private void MoveCapabilityLayerEntryDown(CapabilityLayerEntry? entry)
        {
            if (entry == null)
            {
                return;
            }

            var layer = FindParentLayer(entry);
            if (layer == null)
            {
                return;
            }

            var index = layer.LanguageModels.IndexOf(entry);
            if (index < 0 || index >= layer.LanguageModels.Count - 1)
            {
                return;
            }

            layer.LanguageModels.Move(index, index + 1);
            PersistAll(L("LanguageModelConfiguration.Status.LayerOrderSaved", "功能层级中的语言模型顺序已保存。"));
        }

        private void OpenConfigurationDirectory()
        {
            var directoryPath = Path.GetDirectoryName(LanguageModelConfigurationFilePath) ?? string.Empty;
            Directory.CreateDirectory(directoryPath);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true
            });
        }

        private void OnLanguageModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (LanguageModelDefinition item in e.NewItems)
                {
                    AttachLanguageModel(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (LanguageModelDefinition item in e.OldItems)
                {
                    DetachLanguageModel(item);
                }
            }

            OnPropertyChanged(nameof(LanguageModels));
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnCapabilityLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (CapabilityLayerDefinition item in e.NewItems)
                {
                    AttachCapabilityLayer(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (CapabilityLayerDefinition item in e.OldItems)
                {
                    DetachCapabilityLayer(item);
                }
            }

            OnPropertyChanged(nameof(CapabilityLayers));
            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachLanguageModel(LanguageModelDefinition model)
        {
            model.SetTestAction(TestLanguageModelAsync);
            model.SetCancelTestAction(CancelLanguageModelTest);
            model.PropertyChanged -= OnLanguageModelPropertyChanged;
            model.PropertyChanged += OnLanguageModelPropertyChanged;
        }

        private static LanguageModelDefinition CloneLanguageModel(LanguageModelDefinition source)
        {
            return new LanguageModelDefinition
            {
                DisplayName = BuildDuplicatedDisplayName(source.DisplayName),
                InterfaceType = source.InterfaceType,
                ContextWindowTokens = source.EffectiveContextWindowTokens,
                EnableImageInput = source.EnableImageInput,
                EnableAudioInput = source.EnableAudioInput,
                EnableVideoInput = source.EnableVideoInput,
                EnableDocumentInput = source.EnableDocumentInput,
                EnableUniversalToolCalling = source.EnableUniversalToolCalling,
                InterfaceSettings = CloneInterfaceSettings(source.InterfaceSettings)
            };
        }

        private static LanguageModelInterfaceSettings CloneInterfaceSettings(LanguageModelInterfaceSettings source)
        {
            return source switch
            {
                GoogleLanguageModelSettings google => new GoogleLanguageModelSettings
                {
                    ModelId = google.ModelId,
                    ApiKey = google.ApiKey,
                    BaseUrl = google.BaseUrl,
                    UseTemperature = google.UseTemperature,
                    Temperature = google.Temperature,
                    UseTopP = google.UseTopP,
                    TopP = google.TopP,
                    UseMaxOutputTokens = google.UseMaxOutputTokens,
                    MaxOutputTokens = google.MaxOutputTokens,
                    UseThinkingLevel = google.UseThinkingLevel,
                    ThinkingLevel = google.ThinkingLevel,
                    UseThinkingBudget = google.UseThinkingBudget,
                    ThinkingBudget = google.ThinkingBudget,
                    IncludeThoughts = google.IncludeThoughts
                },
                OpenAiLanguageModelSettings openai => new OpenAiLanguageModelSettings
                {
                    ModelId = openai.ModelId,
                    ApiKey = openai.ApiKey,
                    BaseUrl = openai.BaseUrl,
                    UseTemperature = openai.UseTemperature,
                    Temperature = openai.Temperature,
                    UseTopP = openai.UseTopP,
                    TopP = openai.TopP,
                    UseMaxOutputTokens = openai.UseMaxOutputTokens,
                    MaxOutputTokens = openai.MaxOutputTokens,
                    UsePresencePenalty = openai.UsePresencePenalty,
                    PresencePenalty = openai.PresencePenalty,
                    UseFrequencyPenalty = openai.UseFrequencyPenalty,
                    FrequencyPenalty = openai.FrequencyPenalty,
                    UseSeed = openai.UseSeed,
                    Seed = openai.Seed,
                    UseReasoningEffort = openai.UseReasoningEffort,
                    ReasoningEffort = openai.ReasoningEffort,
                    UseReasoningOutput = openai.UseReasoningOutput,
                    ReasoningOutput = openai.ReasoningOutput
                },
                _ => LanguageModelDefinition.CreateInterfaceSettings(source.InterfaceType)
            };
        }

        private static string BuildDuplicatedDisplayName(string? displayName)
        {
            var normalizedName = string.IsNullOrWhiteSpace(displayName)
                ? L("LanguageModelConfiguration.DefaultModelName", "语言模型")
                : displayName.Trim();
            return LF("LanguageModelConfiguration.DuplicateSuffixFormat", "{0} - 副本", normalizedName);
        }

        private static void ApplySuggestedDefaults(LanguageModelInterfaceSettings settings)
        {
            switch (settings)
            {
                case GoogleLanguageModelSettings google:
                    google.BaseUrl = "https://generativelanguage.googleapis.com";
                    google.TopP = 0.95m;
                    google.MaxOutputTokens = 2048;
                    google.ThinkingLevel = "High";
                    google.IncludeThoughts = true;
                    break;

                case OpenAiLanguageModelSettings openai:
                    openai.BaseUrl = "https://api.openai.com/v1";
                    openai.Temperature = 1.0m;
                    openai.TopP = 1.0m;
                    openai.MaxOutputTokens = 2048;
                    openai.ReasoningEffort = "Medium";
                    break;
            }
        }

        private void DetachLanguageModel(LanguageModelDefinition model)
        {
            model.PropertyChanged -= OnLanguageModelPropertyChanged;
            CancelAndDisposeTest(model.Key);
        }

        private void AttachCapabilityLayer(CapabilityLayerDefinition layer)
        {
            layer.PropertyChanged -= OnCapabilityLayerPropertyChanged;
            layer.PropertyChanged += OnCapabilityLayerPropertyChanged;
            layer.LanguageModels.CollectionChanged -= OnCapabilityLayerEntriesCollectionChanged;
            layer.LanguageModels.CollectionChanged += OnCapabilityLayerEntriesCollectionChanged;

            foreach (var entry in layer.LanguageModels)
            {
                AttachCapabilityLayerEntry(entry);
            }
        }

        private void DetachCapabilityLayer(CapabilityLayerDefinition layer)
        {
            layer.PropertyChanged -= OnCapabilityLayerPropertyChanged;
            layer.LanguageModels.CollectionChanged -= OnCapabilityLayerEntriesCollectionChanged;

            foreach (var entry in layer.LanguageModels)
            {
                DetachCapabilityLayerEntry(entry);
            }
        }

        private void AttachCapabilityLayerEntry(CapabilityLayerEntry entry)
        {
            entry.PropertyChanged -= OnCapabilityLayerEntryPropertyChanged;
            entry.PropertyChanged += OnCapabilityLayerEntryPropertyChanged;
        }

        private void DetachCapabilityLayerEntry(CapabilityLayerEntry entry)
        {
            entry.PropertyChanged -= OnCapabilityLayerEntryPropertyChanged;
        }

        private void OnLanguageModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(LanguageModelDefinition.TestResponse), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(LanguageModelDefinition.IsTesting), StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(LanguageModelDefinition.InterfaceType), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(InterfaceSettingsSectionTitle));
            }

            PersistAll(
                L("LanguageModelConfiguration.Status.ModelSaved", "语言模型配置已保存。"),
                refreshCapabilityLayerDisplayNames: string.Equals(e.PropertyName, nameof(LanguageModelDefinition.DisplayName), StringComparison.Ordinal));
        }

        private void OnCapabilityLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            PersistAll(L("LanguageModelConfiguration.Status.LayerSaved", "功能层级配置已保存。"));
        }

        private void OnCapabilityLayerEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (CapabilityLayerEntry item in e.NewItems)
                {
                    AttachCapabilityLayerEntry(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (CapabilityLayerEntry item in e.OldItems)
                {
                    DetachCapabilityLayerEntry(item);
                }
            }

            PersistAll(L("LanguageModelConfiguration.Status.LayerSaved", "功能层级配置已保存。"));
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnCapabilityLayerEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            PersistAll(L("LanguageModelConfiguration.Status.LayerSaved", "功能层级配置已保存。"));
        }

        private CapabilityLayerDefinition? FindParentLayer(CapabilityLayerEntry entry)
        {
            return CapabilityLayers.FirstOrDefault(layer => layer.LanguageModels.Contains(entry));
        }

        private void PersistAll(string successMessage, bool refreshCapabilityLayerDisplayNames = false)
        {
            if (_isLoading)
            {
                return;
            }

            try
            {
                _languageModelRepository.Save(LanguageModels);
                _capabilityLayerRepository.Save(CapabilityLayers);

                if (refreshCapabilityLayerDisplayNames)
                {
                    OnPropertyChanged(nameof(CapabilityLayers));
                }

            }
            catch (Exception ex)
            {
                if (SelectedLanguageModel != null)
                {
                    SelectedLanguageModel.TestResponse = LF("LanguageModelConfiguration.Status.SaveFailedFormat", "保存失败：{0}", ex.Message);
                }
            }
        }

        private async Task TestLanguageModelAsync(LanguageModelDefinition model)
        {
            var responseBuilder = new StringBuilder();
            var hasVisibleContent = false;
            using var cancellationSource = new CancellationTokenSource();

            RegisterTestCancellation(model.Key, cancellationSource);

            model.IsTesting = true;
            model.CanCancelTest = true;
            model.TestResponse = string.Empty;

            try
            {
                await _languageModelTestService.StreamTestAsync(
                    model,
                    chunk =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var normalizedChunk = hasVisibleContent ? chunk : chunk.TrimStart();
                            if (normalizedChunk.Length == 0)
                            {
                                return;
                            }

                            responseBuilder.Append(normalizedChunk);
                            hasVisibleContent = true;
                            model.TestResponse = responseBuilder.ToString();
                        });
                    },
                    cancellationSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                model.TestResponse = string.IsNullOrWhiteSpace(model.TestResponse)
                    ? L("LanguageModelConfiguration.Status.TestCanceled", "测试已中止。")
                    : $"{model.TestResponse}{Environment.NewLine}{Environment.NewLine}{L("LanguageModelConfiguration.Status.TestCanceledInline", "[测试已中止]")}";
            }
            catch (Exception ex)
            {
                model.TestResponse = LF("LanguageModelConfiguration.Status.TestFailedFormat", "测试失败：{0}", ex.Message);
            }
            finally
            {
                UnregisterTestCancellation(model.Key, cancellationSource);
                model.CanCancelTest = false;
                model.IsTesting = false;
            }
        }

        private void CancelLanguageModelTest(LanguageModelDefinition model)
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

        private void RefreshLocalizedText()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(InterfaceSettingsSectionTitle));

            foreach (var model in LanguageModels)
            {
                model.RefreshLocalizedText();
            }

            foreach (var layer in CapabilityLayers)
            {
                layer.RefreshLocalizedText();
            }
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static string LF(string resourceKey, string fallback, params object[] args)
        {
            return string.Format(L(resourceKey, fallback), args);
        }
    }
}
