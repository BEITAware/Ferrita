using System.Xml.Linq;
using Skyweaver.Controls.AgentConfigurationControl.Models;

namespace Skyweaver.Services.SkyweaverTools
{
    public sealed class SkyweaverToolContext
    {
        public string ApplicationName { get; init; } = "Skyweaver";

        public string? SessionTitle { get; init; }

        public string? WorkspacePath { get; init; }

        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

        public IReadOnlyDictionary<string, string> Properties { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string? CurrentToolName { get; init; }

        public SkyweaverToolConfigurationState? CurrentToolConfiguration { get; init; }

        public AgentDefinition? CurrentAgent { get; init; }

        public bool SupportsHostToolConfirmation { get; init; }

        public IReadOnlyList<SkyweaverToolKitDefinition> AvailableToolKits { get; init; } =
            Array.Empty<SkyweaverToolKitDefinition>();

        public bool IsSubAgent { get; init; }

        public SkyweaverToolContext WithRuntimeAgent(
            AgentDefinition? agent,
            bool supportsHostToolConfirmation)
        {
            return new SkyweaverToolContext
            {
                ApplicationName = ApplicationName,
                SessionTitle = SessionTitle,
                WorkspacePath = WorkspacePath,
                Timestamp = Timestamp,
                Properties = CloneProperties(Properties),
                CurrentToolName = CurrentToolName,
                CurrentToolConfiguration = CurrentToolConfiguration == null
                    ? null
                    : new SkyweaverToolConfigurationState(
                        CurrentToolConfiguration.ToolName,
                        CurrentToolConfiguration.GetPayload()),
                CurrentAgent = agent,
                SupportsHostToolConfirmation = supportsHostToolConfirmation,
                AvailableToolKits = CloneToolKits(AvailableToolKits),
                IsSubAgent = IsSubAgent
            };
        }

        public SkyweaverToolContext WithSubAgentMode(bool isSubAgent)
        {
            return new SkyweaverToolContext
            {
                ApplicationName = ApplicationName,
                SessionTitle = SessionTitle,
                WorkspacePath = WorkspacePath,
                Timestamp = Timestamp,
                Properties = CloneProperties(Properties),
                CurrentToolName = CurrentToolName,
                CurrentToolConfiguration = CurrentToolConfiguration == null
                    ? null
                    : new SkyweaverToolConfigurationState(
                        CurrentToolConfiguration.ToolName,
                        CurrentToolConfiguration.GetPayload()),
                CurrentAgent = CurrentAgent,
                SupportsHostToolConfirmation = SupportsHostToolConfirmation,
                AvailableToolKits = CloneToolKits(AvailableToolKits),
                IsSubAgent = isSubAgent
            };
        }

        public SkyweaverToolContext WithAvailableToolKits(IReadOnlyList<SkyweaverToolKitDefinition>? availableToolKits)
        {
            return new SkyweaverToolContext
            {
                ApplicationName = ApplicationName,
                SessionTitle = SessionTitle,
                WorkspacePath = WorkspacePath,
                Timestamp = Timestamp,
                Properties = CloneProperties(Properties),
                CurrentToolName = CurrentToolName,
                CurrentToolConfiguration = CurrentToolConfiguration == null
                    ? null
                    : new SkyweaverToolConfigurationState(
                        CurrentToolConfiguration.ToolName,
                        CurrentToolConfiguration.GetPayload()),
                CurrentAgent = CurrentAgent,
                SupportsHostToolConfirmation = SupportsHostToolConfirmation,
                AvailableToolKits = CloneToolKits(availableToolKits),
                IsSubAgent = IsSubAgent
            };
        }

        public SkyweaverToolContext WithCurrentToolConfiguration(string toolName, XElement? configuration)
        {
            var normalizedToolName = (toolName ?? string.Empty).Trim();
            if (normalizedToolName.Length == 0)
            {
                throw new ArgumentException("Tool name cannot be empty.", nameof(toolName));
            }

            return new SkyweaverToolContext
            {
                ApplicationName = ApplicationName,
                SessionTitle = SessionTitle,
                WorkspacePath = WorkspacePath,
                Timestamp = Timestamp,
                Properties = CloneProperties(Properties),
                CurrentToolName = normalizedToolName,
                CurrentToolConfiguration = new SkyweaverToolConfigurationState(normalizedToolName, configuration),
                CurrentAgent = CurrentAgent,
                SupportsHostToolConfirmation = SupportsHostToolConfirmation,
                AvailableToolKits = CloneToolKits(AvailableToolKits),
                IsSubAgent = IsSubAgent
            };
        }

        private static IReadOnlyDictionary<string, string> CloneProperties(IReadOnlyDictionary<string, string> properties)
        {
            return properties.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<SkyweaverToolKitDefinition> CloneToolKits(
            IReadOnlyList<SkyweaverToolKitDefinition>? toolKits)
        {
            if (toolKits == null || toolKits.Count == 0)
            {
                return Array.Empty<SkyweaverToolKitDefinition>();
            }

            return toolKits
                .Select(toolKit => toolKit.DeepClone())
                .ToArray();
        }
    }
}
