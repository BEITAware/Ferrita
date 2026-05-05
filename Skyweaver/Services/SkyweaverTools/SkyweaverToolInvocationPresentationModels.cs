using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Services.SkyweaverTools
{
    public sealed class SkyweaverStreamingToolParameterSnapshot
    {
        public string Name { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;

        public bool IsClosed { get; init; }
    }

    public sealed class SkyweaverStreamingToolCallSnapshot
    {
        public int PartIndex { get; init; }

        public int ToolCallIndex { get; init; }

        public string ToolName { get; init; } = string.Empty;

        public string ToolXmlFragment { get; init; } = string.Empty;

        public bool IsInvocationClosed { get; init; }

        public IReadOnlyList<SkyweaverStreamingToolParameterSnapshot> Parameters { get; init; } =
            Array.Empty<SkyweaverStreamingToolParameterSnapshot>();
    }

    public sealed class SkyweaverToolInvocationParameterPresentationState : ObservableObject
    {
        private string _name;
        private string _description;
        private SkyweaverToolParameterType _parameterType;
        private bool _isRequired;
        private string _defaultValue;
        private string _value;
        private bool _isClosed;

        public SkyweaverToolInvocationParameterPresentationState(string name)
        {
            _name = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            _description = string.Empty;
            _defaultValue = string.Empty;
            _value = string.Empty;
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value?.Trim() ?? string.Empty);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value ?? string.Empty);
        }

        public SkyweaverToolParameterType ParameterType
        {
            get => _parameterType;
            set => SetProperty(ref _parameterType, value);
        }

        public bool IsRequired
        {
            get => _isRequired;
            set => SetProperty(ref _isRequired, value);
        }

        public string DefaultValue
        {
            get => _defaultValue;
            set => SetProperty(ref _defaultValue, value ?? string.Empty);
        }

        public string Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasValue));
                    OnPropertyChanged(nameof(IsStreaming));
                }
            }
        }

        public bool IsClosed
        {
            get => _isClosed;
            set
            {
                if (SetProperty(ref _isClosed, value))
                {
                    OnPropertyChanged(nameof(IsStreaming));
                }
            }
        }

        public bool HasValue => !string.IsNullOrEmpty(Value);

        public bool IsStreaming => HasValue && !IsClosed;

        public void ApplyDefinition(SkyweaverToolParameterDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);

            Name = definition.Name;
            Description = definition.Description;
            ParameterType = definition.ParameterType;
            IsRequired = definition.IsRequired;
            DefaultValue = definition.DefaultValue ?? string.Empty;
        }
    }

    public sealed class SkyweaverToolInvocationPresentationState : ObservableObject
    {
        private readonly Dictionary<string, SkyweaverToolInvocationParameterPresentationState> _parametersByName =
            new(StringComparer.OrdinalIgnoreCase);
        private string _toolName;
        private string _toolDescription;
        private string _iconPath;
        private string _rawToolXml;
        private string _toolResultContent;
        private string? _toolResultPresentationKind;
        private bool _isInvocationClosed;

        public SkyweaverToolInvocationPresentationState(
            int toolCallIndex,
            string toolName,
            string? toolDescription = null,
            string? iconPath = null)
        {
            ToolCallIndex = toolCallIndex;
            _toolName = string.IsNullOrWhiteSpace(toolName) ? $"Tool #{toolCallIndex}" : toolName.Trim();
            _toolDescription = toolDescription ?? string.Empty;
            _iconPath = iconPath ?? string.Empty;
            _rawToolXml = string.Empty;
            _toolResultContent = string.Empty;
            ToolResultDiffLines.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasToolResultDiffLines));
                OnPropertyChanged(nameof(HasToolResultText));
            };
        }

        public int ToolCallIndex { get; }

        public string ToolName
        {
            get => _toolName;
            set => SetProperty(ref _toolName, string.IsNullOrWhiteSpace(value) ? $"Tool #{ToolCallIndex}" : value.Trim());
        }

        public string ToolDescription
        {
            get => _toolDescription;
            set => SetProperty(ref _toolDescription, value ?? string.Empty);
        }

        public string IconPath
        {
            get => _iconPath;
            set => SetProperty(ref _iconPath, value ?? string.Empty);
        }

        public string RawToolXml
        {
            get => _rawToolXml;
            set => SetProperty(ref _rawToolXml, value ?? string.Empty);
        }

        public bool IsInvocationClosed
        {
            get => _isInvocationClosed;
            set => SetProperty(ref _isInvocationClosed, value);
        }

        public ObservableCollection<SkyweaverToolInvocationParameterPresentationState> Parameters { get; } = new();

        public string ToolResultContent
        {
            get => _toolResultContent;
            private set
            {
                if (SetProperty(ref _toolResultContent, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasToolResult));
                    OnPropertyChanged(nameof(HasToolResultText));
                }
            }
        }

        public string? ToolResultPresentationKind
        {
            get => _toolResultPresentationKind;
            private set => SetProperty(ref _toolResultPresentationKind, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }

        public ObservableCollection<SkyweaverLineDiffEntry> ToolResultDiffLines { get; } = new();

        public bool HasToolResult => !string.IsNullOrWhiteSpace(ToolResultContent);

        public bool HasToolResultDiffLines => ToolResultDiffLines.Count > 0;

        public bool HasToolResultText => HasToolResult && !HasToolResultDiffLines;

        public SkyweaverToolInvocationParameterPresentationState GetOrCreateParameterState(
            string parameterName,
            SkyweaverToolParameterDefinition? definition = null)
        {
            var normalizedName = string.IsNullOrWhiteSpace(parameterName)
                ? "Parameter"
                : parameterName.Trim();

            if (_parametersByName.TryGetValue(normalizedName, out var existing))
            {
                if (definition != null)
                {
                    existing.ApplyDefinition(definition);
                }

                return existing;
            }

            var state = new SkyweaverToolInvocationParameterPresentationState(normalizedName);
            if (definition != null)
            {
                state.ApplyDefinition(definition);
            }

            _parametersByName[normalizedName] = state;
            Parameters.Add(state);
            return state;
        }

        public void EnsureParameterDefinitions(IEnumerable<SkyweaverToolParameterDefinition>? parameterDefinitions)
        {
            if (parameterDefinitions == null)
            {
                return;
            }

            foreach (var definition in parameterDefinitions)
            {
                GetOrCreateParameterState(definition.Name, definition);
            }
        }

        public void ApplySnapshot(
            SkyweaverStreamingToolCallSnapshot snapshot,
            IEnumerable<SkyweaverToolParameterDefinition>? parameterDefinitions = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (!string.IsNullOrWhiteSpace(snapshot.ToolName))
            {
                ToolName = snapshot.ToolName;
            }

            RawToolXml = snapshot.ToolXmlFragment;
            IsInvocationClosed = snapshot.IsInvocationClosed;
            EnsureParameterDefinitions(parameterDefinitions);

            Dictionary<string, SkyweaverToolParameterDefinition>? definitionsByName = null;
            if (parameterDefinitions != null)
            {
                definitionsByName = parameterDefinitions
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            }

            foreach (var parameter in snapshot.Parameters)
            {
                SkyweaverToolParameterDefinition? definition = null;
                definitionsByName?.TryGetValue(parameter.Name, out definition);
                var state = GetOrCreateParameterState(parameter.Name, definition);
                state.Value = parameter.Value ?? string.Empty;
                state.IsClosed = parameter.IsClosed;
            }
        }

        public void ClearToolResult()
        {
            ApplyToolResult(null, null);
        }

        public void ApplyToolResult(string? content, string? presentationKind)
        {
            ToolResultContent = content ?? string.Empty;
            ToolResultPresentationKind = presentationKind;
            RebuildToolResultDiffLines();
        }

        private void RebuildToolResultDiffLines()
        {
            ToolResultDiffLines.Clear();

            if (!string.Equals(
                    ToolResultPresentationKind,
                    SkyweaverToolResultPresentationKinds.LineDiffV1,
                    StringComparison.OrdinalIgnoreCase) ||
                !SkyweaverLineDiffPresentation.TryParseContent(ToolResultContent, out var diffEntries))
            {
                return;
            }

            foreach (var entry in diffEntries)
            {
                ToolResultDiffLines.Add(entry);
            }
        }
    }

    public sealed class SkyweaverToolInvocationPresentationContext
    {
        public SkyweaverToolInvocationPresentationContext(
            SkyweaverToolDefinition baseDefinition,
            SkyweaverToolDefinition effectiveDefinition,
            string iconPath,
            SkyweaverToolInvocationPresentationState state)
        {
            BaseDefinition = baseDefinition ?? throw new ArgumentNullException(nameof(baseDefinition));
            EffectiveDefinition = effectiveDefinition ?? throw new ArgumentNullException(nameof(effectiveDefinition));
            IconPath = iconPath ?? string.Empty;
            State = state ?? throw new ArgumentNullException(nameof(state));
        }

        public string ToolName => EffectiveDefinition.Name;

        public SkyweaverToolDefinition BaseDefinition { get; }

        public SkyweaverToolDefinition EffectiveDefinition { get; }

        public string IconPath { get; }

        public SkyweaverToolInvocationPresentationState State { get; }
    }

    public interface ISkyweaverToolInvocationPresentationProvider
    {
        FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context);
    }

    public interface ISkyweaverToolConfirmationPresentationProvider
    {
        FrameworkElement? CreateConfirmationPresentation(SkyweaverToolInvocationPresentationContext context);
    }
}
