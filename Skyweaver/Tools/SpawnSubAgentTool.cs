using System.Text;
using System.IO;
using System.Windows;
using Newtonsoft.Json.Linq;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Services.AgentLoop;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class SpawnSubAgentTool : ISkyweaverTool, ISkyweaverToolInvocationPresentationProvider, ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = SkyweaverBuiltInToolNames.SpawnSubAgent;

        private readonly AgentConfigurationRepository _agentConfigurationRepository;
        private readonly AgentLoopService _agentLoopService;

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Spawns a sub-agent from the current runtime agents and returns the sub-agent's PassToMainAgent payload to the caller.",
            "GuideBot",
            [
                new SkyweaverToolParameterDefinition(
                    "SubAgentID",
                    "The AgentId of the sub-agent to spawn.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    SkyweaverBuiltInToolNames.SpawnSubAgentMissionParameter,
                    "The mission prompt to give the sub-agent. This is the sub-agent's user prompt.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    SkyweaverBuiltInToolNames.SpawnSubAgentRequirementsParameter,
                    "The required format or expectations for what the sub-agent should return to the main agent.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    SkyweaverBuiltInToolNames.SpawnSubAgentResourcesParameter,
                    "Optional JSON array of file paths whose full content will be provided to the sub-agent.",
                    SkyweaverToolParameterType.Json,
                    isRequired: false)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            supportsAsyncInvocation: false);

        public SpawnSubAgentTool()
            : this(new AgentConfigurationRepository(new AgentConfigurationPathProvider()), new AgentLoopService())
        {
        }

        public SpawnSubAgentTool(
            AgentConfigurationRepository agentConfigurationRepository,
            AgentLoopService agentLoopService)
        {
            _agentConfigurationRepository = agentConfigurationRepository ?? throw new ArgumentNullException(nameof(agentConfigurationRepository));
            _agentLoopService = agentLoopService ?? throw new ArgumentNullException(nameof(agentLoopService));
        }

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var agents = LoadAvailableSubAgents();
            if (agents.Count == 0)
            {
                return "SpawnSubAgent calls a sub-agent by AgentId. Currently there are no agents configured as sub-agents.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("SpawnSubAgent calls one configured sub-agent by AgentId. Available sub-agents:");
            foreach (var agent in agents)
            {
                builder.AppendLine($"- {agent.AgentId}: {agent.DisplayNameOrFallback}");
                var intro = NormalizeInline(agent.SubAgentIntroduction);
                if (intro.Length > 0)
                {
                    builder.AppendLine($"  介绍：{intro}");
                }
            }

            return builder.ToString().TrimEnd();
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Sub-agent", "SubAgentID", "Target AgentId"),
                    new ToolInvocationCardFieldDefinition("Mission", SkyweaverBuiltInToolNames.SpawnSubAgentMissionParameter, "User prompt for the sub-agent"),
                    new ToolInvocationCardFieldDefinition("Requirements", SkyweaverBuiltInToolNames.SpawnSubAgentRequirementsParameter, "Expected return format"),
                    new ToolInvocationCardFieldDefinition("Resources", SkyweaverBuiltInToolNames.SpawnSubAgentResourcesParameter, "Optional file paths JSON array")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subAgentId = arguments.GetString("SubAgentID")?.Trim() ?? string.Empty;
            if (subAgentId.Length == 0)
            {
                return SkyweaverToolResult.Failure("SpawnSubAgent requires SubAgentID.");
            }

            var mission = arguments.GetString(SkyweaverBuiltInToolNames.SpawnSubAgentMissionParameter)?.Trim() ?? string.Empty;
            var requirements = arguments.GetString(SkyweaverBuiltInToolNames.SpawnSubAgentRequirementsParameter)?.Trim() ?? string.Empty;
            var resourcePaths = ExtractResourcePaths(arguments.GetJson(SkyweaverBuiltInToolNames.SpawnSubAgentResourcesParameter));

            if (mission.Length == 0 || requirements.Length == 0)
            {
                return SkyweaverToolResult.Failure("SpawnSubAgent requires Mission and Requirements.");
            }

            var agents = LoadAvailableSubAgents();
            var subAgent = agents.FirstOrDefault(agent => string.Equals(agent.AgentId, subAgentId, StringComparison.OrdinalIgnoreCase));
            if (subAgent == null)
            {
                return SkyweaverToolResult.Failure($"SubAgent '{subAgentId}' was not found or is not allowed as a sub-agent.");
            }

            var inputBlocks = await BuildInputBlocksAsync(resourcePaths, cancellationToken).ConfigureAwait(false);
            var toolContext = context.WithSubAgentMode(true).WithRuntimeAgent(subAgent, context.SupportsHostToolConfirmation);
            var result = await _agentLoopService.RunAsync(new AgentLoopRequest
            {
                Agent = subAgent.DeepClone(),
                Input = BuildSubAgentInput(mission, requirements),
                InputContentBlocks = inputBlocks,
                History = Array.Empty<LanguageModelChatMessage>(),
                ToolContext = toolContext,
                EnableGemmaThoughtCompatibility = true,
                IsSubAgent = true,
                ToolCallIdFactory = new TransientToolCallIdFactory().Create,
                ToolConfirmationCallback = null
            }, cancellationToken).ConfigureAwait(false);

            if (!result.IsCompleted || result.FinalOutput == null)
            {
                return SkyweaverToolResult.Failure(result.FailureReason ?? "Sub-agent did not return a final payload.");
            }

            var payload = result.FinalOutput.Content;
            return SkyweaverToolResult.Success(
                "SpawnSubAgent completed.",
                new Dictionary<string, object?>
                {
                    ["subAgentId"] = subAgent.AgentId,
                    ["subAgentDisplayName"] = subAgent.DisplayNameOrFallback,
                    ["passToMainAgent"] = payload,
                    ["requirements"] = requirements
                });
        }

        private IReadOnlyList<AgentDefinition> LoadAvailableSubAgents()
        {
            return _agentConfigurationRepository.Load()
                .Where(agent => !string.IsNullOrWhiteSpace(agent.AgentId) && agent.CanRunAsSubAgent)
                .OrderBy(agent => agent.DisplayNameOrFallback, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ExtractResourcePaths(JToken? token)
        {
            if (token is not JArray array)
            {
                return Array.Empty<string>();
            }

            return array.Values<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static async Task<IReadOnlyList<LanguageModelChatContentBlock>> BuildInputBlocksAsync(
            IReadOnlyList<string> resourcePaths,
            CancellationToken cancellationToken)
        {
            if (resourcePaths.Count == 0)
            {
                return Array.Empty<LanguageModelChatContentBlock>();
            }

            var blocks = new List<LanguageModelChatContentBlock>(resourcePaths.Count);
            foreach (var path in resourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                {
                    continue;
                }

                var fullText = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                blocks.Add(LanguageModelChatContentBlock.CreateHostPreservedContent(
                    $"<SkyweaverPreservedContent><Resource Path=\"{System.Security.SecurityElement.Escape(path)}\">{System.Security.SecurityElement.Escape(fullText)}</Resource></SkyweaverPreservedContent>"));
            }

            return blocks;
        }

        private static string BuildSubAgentInput(string mission, string requirements)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Mission:");
            builder.AppendLine(mission.Trim());
            builder.AppendLine();
            builder.AppendLine("Requirements for PassToMainAgent return:");
            builder.AppendLine(requirements.Trim());
            return builder.ToString().TrimEnd();
        }

        private static string NormalizeInline(string? text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        }

        private sealed class TransientToolCallIdFactory
        {
            private readonly HashSet<string> _reservedIds = new(StringComparer.OrdinalIgnoreCase);
            private int _nextId;

            public string Create()
            {
                while (_nextId < int.MaxValue)
                {
                    var candidate = $"TC{++_nextId}";
                    if (_reservedIds.Add(candidate))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException("Unable to allocate a unique transient tool call id.");
            }
        }
    }
}
