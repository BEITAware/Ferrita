using System.ComponentModel;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Services;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Models
{
    public sealed class EmbeddingModelDefinition : ObservableObject
    {
        public const int DefaultDimensions = 1536;
        public const int DefaultMaxInputTokens = 8192;

        private string _key = Guid.NewGuid().ToString("N");
        private string _displayName = string.Empty;
        private string _interfaceType = EmbeddingModelInterfaceCatalog.DefaultInterfaceType;
        private EmbeddingModelInterfaceSettings _interfaceSettings =
            EmbeddingModelInterfaceCatalog.CreateInterfaceSettings(EmbeddingModelInterfaceCatalog.DefaultInterfaceType);
        private int _dimensions = DefaultDimensions;
        private int _maxInputTokens = DefaultMaxInputTokens;
        private bool _normalize = true;
        private bool _supportsMultimodalEmbedding;
        private bool _includeBinaryDataInTextProjection;
        private string _testResponse = string.Empty;
        private bool _isTesting;
        private bool _canCancelTest;
        private Func<EmbeddingModelDefinition, Task>? _testAction;
        private Action<EmbeddingModelDefinition>? _cancelTestAction;

        public EmbeddingModelDefinition()
        {
            AttachInterfaceSettings(_interfaceSettings);
            TestCommand = new AsyncRelayCommand(ExecuteTestAsync, CanExecuteTest);
            CancelTestCommand = new RelayCommand(ExecuteCancelTest, CanExecuteCancelTest);
        }

        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value?.Trim() ?? string.Empty);
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetProperty(ref _displayName, value?.Trim() ?? string.Empty))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public string InterfaceType
        {
            get => _interfaceType;
            set
            {
                var normalizedValue = EmbeddingModelInterfaceCatalog.NormalizeInterfaceType(value);
                if (!SetProperty(ref _interfaceType, normalizedValue))
                {
                    return;
                }

                if (!string.Equals(InterfaceSettings.InterfaceType, normalizedValue, StringComparison.Ordinal))
                {
                    InterfaceSettings = EmbeddingModelInterfaceCatalog.CreateInterfaceSettings(normalizedValue);
                }

                NotifyDerivedStateChanged();
            }
        }

        public EmbeddingModelInterfaceSettings InterfaceSettings
        {
            get => _interfaceSettings;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                if (ReferenceEquals(_interfaceSettings, value))
                {
                    return;
                }

                DetachInterfaceSettings(_interfaceSettings);
                _interfaceSettings = value;
                AttachInterfaceSettings(_interfaceSettings);

                if (!string.Equals(_interfaceType, _interfaceSettings.InterfaceType, StringComparison.Ordinal))
                {
                    _interfaceType = _interfaceSettings.InterfaceType;
                    OnPropertyChanged(nameof(InterfaceType));
                }

                OnPropertyChanged(nameof(InterfaceSettings));
                NotifyDerivedStateChanged();
            }
        }

        public int Dimensions
        {
            get => _dimensions;
            set
            {
                var normalizedValue = value > 0 ? value : DefaultDimensions;
                if (SetProperty(ref _dimensions, normalizedValue))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public int MaxInputTokens
        {
            get => _maxInputTokens;
            set
            {
                var normalizedValue = value > 0 ? value : DefaultMaxInputTokens;
                if (SetProperty(ref _maxInputTokens, normalizedValue))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public bool Normalize
        {
            get => _normalize;
            set => SetProperty(ref _normalize, value);
        }

        public bool SupportsMultimodalEmbedding
        {
            get => _supportsMultimodalEmbedding;
            set
            {
                if (SetProperty(ref _supportsMultimodalEmbedding, value))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public bool IncludeBinaryDataInTextProjection
        {
            get => _includeBinaryDataInTextProjection;
            set => SetProperty(ref _includeBinaryDataInTextProjection, value);
        }

        public bool IsFullyConfigured =>
            !string.IsNullOrWhiteSpace(DisplayName) &&
            !string.IsNullOrWhiteSpace(InterfaceType) &&
            Dimensions > 0 &&
            MaxInputTokens > 0 &&
            InterfaceSettings.IsFullyConfigured;

        public bool IsTesting
        {
            get => _isTesting;
            set
            {
                if (SetProperty(ref _isTesting, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string TestResponse
        {
            get => _testResponse;
            set => SetProperty(ref _testResponse, value ?? string.Empty);
        }

        public bool CanCancelTest
        {
            get => _canCancelTest;
            set
            {
                if (SetProperty(ref _canCancelTest, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string DimensionsText => $"{Dimensions:N0}d";

        public string MaxInputTokensText => $"{MaxInputTokens:N0} Tokens";

        public string MultimodalEmbeddingSupportText => SupportsMultimodalEmbedding ? "支持" : "不支持";

        public static IReadOnlyList<string> AvailableInterfaceTypes => EmbeddingModelInterfaceCatalog.AvailableInterfaceTypes;

        public ICommand TestCommand { get; }

        public ICommand CancelTestCommand { get; }

        public string ConfigurationStatusText => IsFullyConfigured ? "已完成" : "未完成";

        public string SummaryModelId => InterfaceSettings.SummaryModelId;

        public void SetTestAction(Func<EmbeddingModelDefinition, Task> testAction)
        {
            _testAction = testAction ?? throw new ArgumentNullException(nameof(testAction));
            CommandManager.InvalidateRequerySuggested();
        }

        public void SetCancelTestAction(Action<EmbeddingModelDefinition> cancelTestAction)
        {
            _cancelTestAction = cancelTestAction ?? throw new ArgumentNullException(nameof(cancelTestAction));
            CommandManager.InvalidateRequerySuggested();
        }

        private void AttachInterfaceSettings(EmbeddingModelInterfaceSettings settings)
        {
            settings.PropertyChanged -= OnInterfaceSettingsPropertyChanged;
            settings.PropertyChanged += OnInterfaceSettingsPropertyChanged;
        }

        private void DetachInterfaceSettings(EmbeddingModelInterfaceSettings settings)
        {
            settings.PropertyChanged -= OnInterfaceSettingsPropertyChanged;
        }

        private void OnInterfaceSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(InterfaceSettings));
            NotifyDerivedStateChanged();
        }

        private void NotifyDerivedStateChanged()
        {
            OnPropertyChanged(nameof(IsFullyConfigured));
            OnPropertyChanged(nameof(ConfigurationStatusText));
            OnPropertyChanged(nameof(SummaryModelId));
            OnPropertyChanged(nameof(DimensionsText));
            OnPropertyChanged(nameof(MaxInputTokensText));
            OnPropertyChanged(nameof(MultimodalEmbeddingSupportText));
        }

        private bool CanExecuteTest()
        {
            return !IsTesting && _testAction != null;
        }

        private bool CanExecuteCancelTest()
        {
            return IsTesting && CanCancelTest && _cancelTestAction != null;
        }

        private async Task ExecuteTestAsync()
        {
            if (_testAction == null)
            {
                return;
            }

            await _testAction(this).ConfigureAwait(true);
        }

        private void ExecuteCancelTest()
        {
            _cancelTestAction?.Invoke(this);
        }
    }
}
