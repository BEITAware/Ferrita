using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Controls.AgentConfigurationControl.Services;
using Skyweaver.Services.AerialCityRag;

namespace Skyweaver.Services.SkyweaverTools
{
    public sealed class SkyweaverPromptToolCatalogService
    {
        private readonly SkyweaverToolManager _toolManager;
        private readonly SkyweaverToolKitService _toolKitService;

        public SkyweaverPromptToolCatalogService()
            : this(new SkyweaverToolManager(), new SkyweaverToolKitService())
        {
        }

        public SkyweaverPromptToolCatalogService(
            SkyweaverToolManager toolManager,
            SkyweaverToolKitService toolKitService)
        {
            _toolManager = toolManager ?? throw new ArgumentNullException(nameof(toolManager));
            _toolKitService = toolKitService ?? throw new ArgumentNullException(nameof(toolKitService));
        }

        public IReadOnlyList<SkyweaverPromptToolDefinition> ResolveCallableTools(
            AgentDefinition agent,
            bool supportsHostToolConfirmation,
            IReadOnlyCollection<string>? activeToolKitKeys = null,
            IReadOnlyCollection<string>? restrictToToolNames = null,
            IReadOnlyList<SkyweaverToolKitDefinition>? availableToolKits = null)
        {
            ArgumentNullException.ThrowIfNull(agent);

            var toolKits = availableToolKits ?? _toolKitService.Load();
            var activeToolKitKeySet = activeToolKitKeys == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    activeToolKitKeys.Where(item => !string.IsNullOrWhiteSpace(item)),
                    StringComparer.OrdinalIgnoreCase);
            var restrictedToolNameSet = restrictToToolNames == null
                ? null
                : new HashSet<string>(
                    restrictToToolNames.Where(item => !string.IsNullOrWhiteSpace(item)),
                    StringComparer.OrdinalIgnoreCase);
            var toolKitMembershipMap = _toolKitService.BuildToolKitMembershipMap(toolKits);

            var tools = new List<SkyweaverPromptToolDefinition>();
            var exposeAerialCityRagTools = AerialCityRagAvailability.AreToolsAvailable();

            foreach (var registration in _toolManager.GetRegisteredTools(resolveIcons: false)
                         .Where(item => item.RequiresAgentPermission && item.Definition.SupportsAsyncInvocation)
                         .OrderBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (AerialCityRagAvailability.IsAerialCityRagTool(registration.Definition.Name) &&
                    !exposeAerialCityRagTools)
                {
                    continue;
                }

                if (restrictedToolNameSet != null &&
                    !restrictedToolNameSet.Contains(registration.Definition.Name))
                {
                    continue;
                }

                if (restrictedToolNameSet != null && !registration.Definition.CanBelongToToolKit)
                {
                    continue;
                }

                if (registration.Definition.CanBelongToToolKit &&
                    toolKitMembershipMap.TryGetValue(registration.Definition.Name, out var membershipKeys) &&
                    membershipKeys.Count > 0 &&
                    !membershipKeys.Any(activeToolKitKeySet.Contains))
                {
                    continue;
                }

                var permissionDecision = AgentToolPermissionEvaluator.Resolve(agent, registration);
                switch (permissionDecision)
                {
                    case AgentToolEffectiveDecision.Allowed:
                        tools.Add(CreatePromptToolDefinition(registration, toolKits, requiresHostConfirmation: false));
                        break;

                    case AgentToolEffectiveDecision.RequiresUserConfirmation when supportsHostToolConfirmation:
                        tools.Add(CreatePromptToolDefinition(registration, toolKits, requiresHostConfirmation: true));
                        break;
                }
            }

            return tools;
        }

        private static SkyweaverPromptToolDefinition CreatePromptToolDefinition(
            SkyweaverToolRegistration registration,
            IReadOnlyList<SkyweaverToolKitDefinition> availableToolKits,
            bool requiresHostConfirmation)
        {
            return new SkyweaverPromptToolDefinition(
                registration.Definition.Name,
                SkyweaverToolPromptSupport.ResolvePromptDescription(registration, availableToolKits),
                registration.Definition.Parameters,
                requiresHostConfirmation);
        }
    }
}
