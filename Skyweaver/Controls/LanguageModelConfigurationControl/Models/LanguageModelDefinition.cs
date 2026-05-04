using System.ComponentModel;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Models
{
    public sealed class LanguageModelDefinition : ObservableObject
    {
        public const int DefaultContextWindowTokens = 128_000;

        private string _key = Guid.NewGuid().ToString("N");
        private string _displayName = string.Empty;
        private string _interfaceType = LanguageModelInterfaceCatalog.DefaultInterfaceType;
        private LanguageModelInterfaceSettings _interfaceSettings =
            LanguageModelInterfaceCatalog.CreateInterfaceSettings(LanguageModelInterfaceCatalog.DefaultInterfaceType);
        private int _contextWindowTokens = DefaultContextWindowTokens;
        private string _testResponse = string.Empty;
        private bool _isTesting;
        private bool _canCancelTest;
        private Func<LanguageModelDefinition, Task>? _testAction;
        private Action<LanguageModelDefinition>? _cancelTestAction;

        public LanguageModelDefinition()
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
                var normalizedValue = LanguageModelInterfaceCatalog.NormalizeInterfaceType(value);
                if (!SetProperty(ref _interfaceType, normalizedValue))
                {
                    return;
                }

                if (!string.Equals(InterfaceSettings.InterfaceType, normalizedValue, StringComparison.Ordinal))
                {
                    InterfaceSettings = CreateInterfaceSettings(normalizedValue);
                }

                NotifyDerivedStateChanged();
            }
        }

        public LanguageModelInterfaceSettings InterfaceSettings
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

        public int ContextWindowTokens
        {
            get => _contextWindowTokens;
            set
            {
                var normalizedValue = value > 0 ? value : DefaultContextWindowTokens;
                if (SetProperty(ref _contextWindowTokens, normalizedValue))
                {
                    NotifyDerivedStateChanged();
                }
            }
        }

        public bool IsFullyConfigured =>
            !string.IsNullOrWhiteSpace(DisplayName) &&
            !string.IsNullOrWhiteSpace(InterfaceType) &&
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

        public int EffectiveContextWindowTokens => ContextWindowTokens > 0 ? ContextWindowTokens : DefaultContextWindowTokens;

        public string ContextWindowText => $"{EffectiveContextWindowTokens:N0} Tokens";

        public static IReadOnlyList<string> AvailableInterfaceTypes => LanguageModelInterfaceCatalog.AvailableInterfaceTypes;

        public ICommand TestCommand { get; }

        public ICommand CancelTestCommand { get; }

        public string ConfigurationStatusText => IsFullyConfigured ? "已完善" : "未完善";

        public string SummaryModelId => InterfaceSettings.SummaryModelId;

        public void SetTestAction(Func<LanguageModelDefinition, Task> testAction)
        {
            _testAction = testAction ?? throw new ArgumentNullException(nameof(testAction));
            CommandManager.InvalidateRequerySuggested();
        }

        public void SetCancelTestAction(Action<LanguageModelDefinition> cancelTestAction)
        {
            _cancelTestAction = cancelTestAction ?? throw new ArgumentNullException(nameof(cancelTestAction));
            CommandManager.InvalidateRequerySuggested();
        }

        public static LanguageModelInterfaceSettings CreateInterfaceSettings(string? interfaceType)
        {
            return LanguageModelInterfaceCatalog.CreateInterfaceSettings(interfaceType);
        }

        private void AttachInterfaceSettings(LanguageModelInterfaceSettings settings)
        {
            settings.PropertyChanged -= OnInterfaceSettingsPropertyChanged;
            settings.PropertyChanged += OnInterfaceSettingsPropertyChanged;
        }

        private void DetachInterfaceSettings(LanguageModelInterfaceSettings settings)
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
            OnPropertyChanged(nameof(EffectiveContextWindowTokens));
            OnPropertyChanged(nameof(ContextWindowText));
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
