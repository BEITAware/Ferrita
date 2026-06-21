using Ferrita.Controls.AgentConfigurationControl.Models;
using Ferrita.Controls.AgentConfigurationControl.Services;
using Ferrita.Services.AerialCityRag;

namespace Ferrita.Services.FerritaTools
{
    public sealed class FerritaPromptToolCatalogService
    {
        private readonly FerritaToolManager _toolManager;
        private readonly FerritaToolKitService _toolKitService;

        public FerritaPromptToolCatalogService()
            : this(new FerritaToolManager(), new FerritaToolKitService())
        {
        }

        public FerritaPromptToolCatalogService(
            FerritaToolManager toolManager,
            FerritaToolKitService toolKitService)
        {
            _toolManager = toolManager ?? throw new ArgumentNullException(nameof(toolManager));
            _toolKitService = toolKitService ?? throw new ArgumentNullException(nameof(toolKitService));
        }

        public IReadOnlyList<FerritaPromptToolDefinition> ResolveCallableTools(
            AgentDefinition agent,
            bool supportsHostToolConfirmation,
            IReadOnlyCollection<string>? activeToolKitKeys = null,
            IReadOnlyCollection<string>? restrictToToolNames = null,
            IReadOnlyList<FerritaToolKitDefinition>? availableToolKits = null)
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

            var tools = new List<FerritaPromptToolDefinition>();
            var exposeAerialCityRagTools = AerialCityRagAvailability.AreToolsAvailable();

            foreach (var registration in _toolManager.GetRegisteredTools(resolveIcons: false)
                         .Where(item => item.RequiresAgentPermission)
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

        private static FerritaPromptToolDefinition CreatePromptToolDefinition(
            FerritaToolRegistration registration,
            IReadOnlyList<FerritaToolKitDefinition> availableToolKits,
            bool requiresHostConfirmation)
        {
            var rawDescription = FerritaToolPromptSupport.ResolvePromptDescription(registration, availableToolKits);
            var (description, fewShots) = FerritaToolPromptSupport.SplitFewShots(rawDescription);

            return new FerritaPromptToolDefinition(
                registration.Definition.Name,
                description,
                registration.Definition.Parameters,
                requiresHostConfirmation,
                fewShots);
        }
    }
}
