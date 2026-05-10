using System.IO;
using System.Xml.Linq;
using Skyweaver.Controls.AgentConfigurationControl.Models;

namespace Skyweaver.Controls.AgentConfigurationControl.Services
{
    public sealed class AgentConfigurationRepository
    {
        private readonly AgentConfigurationPathProvider _pathProvider;
        private readonly object _syncRoot = new();

        public AgentConfigurationRepository(AgentConfigurationPathProvider pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public string ConfigurationDirectoryPath => _pathProvider.ConfigurationDirectoryPath;

        public string ConfigurationFilePath => _pathProvider.ConfigurationFilePath;

        public IReadOnlyList<AgentDefinition> Load()
        {
            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                if (!File.Exists(ConfigurationFilePath))
                {
                    Save(Array.Empty<AgentDefinition>());
                    return Array.Empty<AgentDefinition>();
                }

                var document = XDocument.Load(ConfigurationFilePath);
                var root = document.Root ?? throw new InvalidDataException("代理配置 XML 缺少根节点。");

                var agents = new List<AgentDefinition>();
                foreach (var agentElement in root.Elements("Agent"))
                {
                    var definition = new AgentDefinition
                    {
                        AgentId = ((string?)agentElement.Attribute("AgentId") ?? (string?)agentElement.Element("AgentId") ?? string.Empty).Trim(),
                        DisplayName = ((string?)agentElement.Element("DisplayName") ?? string.Empty).Trim(),
                        AvatarPath = ((string?)agentElement.Element("AvatarPath") ?? AgentDefinition.DefaultAvatarPath).Trim(),
                        SystemPrompt = ((string?)agentElement.Element("SystemPrompt") ?? string.Empty),
                        IsStructuredXmlIO = ParseBool((string?)agentElement.Element("IsStructuredXmlIO"), false),
                        InputDescription = ((string?)agentElement.Element("InputDescription") ?? string.Empty),
                        OutputDescription = ((string?)agentElement.Element("OutputDescription") ?? string.Empty),
                        RuntimeRole = ParseRuntimeRole((string?)agentElement.Element("RuntimeRole")),
                        SubAgentIntroduction = ((string?)agentElement.Element("SubAgentIntroduction") ?? string.Empty),
                        LanguageModelSelectionMode = ParseLanguageModelSelectionMode((string?)agentElement.Element("LanguageModelSelectionMode")),
                        SelectedLanguageModelKey = ((string?)agentElement.Element("SelectedLanguageModelKey") ?? string.Empty).Trim(),
                        SelectedCapabilityLayerKey = ((string?)agentElement.Element("SelectedCapabilityLayerKey") ?? string.Empty).Trim()
                    };

                    var defaultToolKitElements = agentElement.Element("DefaultToolKits")?.Elements("ToolKit") ?? Enumerable.Empty<XElement>();
                    foreach (var toolKitElement in defaultToolKitElements)
                    {
                        definition.DefaultToolKits.Add(new AgentToolKitSelectionDefinition
                        {
                            ToolKitKey = ((string?)toolKitElement.Attribute("Key") ?? string.Empty).Trim()
                        });
                    }

                    var toolElements = agentElement.Element("ToolPermissions")?.Elements("Tool") ?? Enumerable.Empty<XElement>();
                    foreach (var toolElement in toolElements)
                    {
                        var toolName = ((string?)toolElement.Attribute("Name") ?? string.Empty).Trim();
                        if (toolName.Length == 0)
                        {
                            continue;
                        }

                        definition.ToolPermissions.Add(new AgentToolPermissionDefinition
                        {
                            ToolName = toolName,
                            Permission = ParsePermissionMode((string?)toolElement.Attribute("Permission"))
                        });
                    }

                    LoadSchemaChildren(definition.InputSchemaRoot, agentElement.Element("InputSchema"));
                    LoadSchemaChildren(definition.OutputSchemaRoot, agentElement.Element("OutputSchema"));

                    agents.Add(definition);
                }

                return agents;
            }
        }

        public void Save(IEnumerable<AgentDefinition> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);

            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                var document = new XDocument(
                    new XElement("AgentConfigurations",
                        new XAttribute("SchemaVersion", 3),
                        definitions.Select(definition =>
                            new XElement("Agent",
                                new XAttribute("AgentId", definition.AgentId),
                                new XElement("DisplayName", definition.DisplayName),
                                new XElement("AvatarPath", string.IsNullOrWhiteSpace(definition.AvatarPath) ? AgentDefinition.DefaultAvatarPath : definition.AvatarPath),
                                new XElement("SystemPrompt", definition.SystemPrompt ?? string.Empty),
                                new XElement("IsStructuredXmlIO", definition.IsStructuredXmlIO),
                                new XElement("InputDescription", definition.InputDescription ?? string.Empty),
                                new XElement("OutputDescription", definition.OutputDescription ?? string.Empty),
                                new XElement("RuntimeRole", definition.RuntimeRole),
                                new XElement("SubAgentIntroduction", definition.SubAgentIntroduction ?? string.Empty),
                                new XElement("LanguageModelSelectionMode", definition.LanguageModelSelectionMode),
                                new XElement("SelectedLanguageModelKey", definition.SelectedLanguageModelKey ?? string.Empty),
                                new XElement("SelectedCapabilityLayerKey", definition.SelectedCapabilityLayerKey ?? string.Empty),
                                new XElement("DefaultToolKits",
                                    definition.DefaultToolKits
                                        .Where(toolKit => !string.IsNullOrWhiteSpace(toolKit.ToolKitKey))
                                        .Select(toolKit => new XElement("ToolKit",
                                            new XAttribute("Key", toolKit.ToolKitKey)))),
                                new XElement("ToolPermissions",
                                    definition.ToolPermissions
                                        .Where(tool => !string.IsNullOrWhiteSpace(tool.ToolName))
                                        .OrderBy(tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
                                        .Select(tool => new XElement("Tool",
                                            new XAttribute("Name", tool.ToolName),
                                            new XAttribute("Permission", tool.Permission)))),
                                new XElement("InputSchema", definition.InputSchemaRoot.Children.Select(CreateNodeElement)),
                                new XElement("OutputSchema", definition.OutputSchemaRoot.Children.Select(CreateNodeElement))))));

                document.Save(ConfigurationFilePath);
            }
        }

        private void EnsureConfigurationDirectory()
        {
            Directory.CreateDirectory(ConfigurationDirectoryPath);
        }

        private static XElement CreateNodeElement(XmlElementNodeDefinition node)
        {
            return new XElement("Node",
                new XAttribute("Name", node.Name ?? string.Empty),
                node.Children.Select(CreateNodeElement));
        }

        private static void LoadSchemaChildren(XmlElementNodeDefinition rootNode, XElement? schemaElement)
        {
            foreach (var nodeElement in schemaElement?.Elements("Node") ?? Enumerable.Empty<XElement>())
            {
                rootNode.AddChild(ParseNode(nodeElement));
            }
        }

        private static XmlElementNodeDefinition ParseNode(XElement nodeElement)
        {
            var node = new XmlElementNodeDefinition(((string?)nodeElement.Attribute("Name") ?? string.Empty).Trim());
            foreach (var child in nodeElement.Elements("Node"))
            {
                node.AddChild(ParseNode(child));
            }

            return node;
        }

        private static bool ParseBool(string? value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static AgentToolPermissionMode ParsePermissionMode(string? value)
        {
            return Enum.TryParse<AgentToolPermissionMode>(value, true, out var parsed)
                ? parsed
                : AgentToolPermissionMode.RequireConfirmation;
        }

        private static AgentLanguageModelSelectionMode ParseLanguageModelSelectionMode(string? value)
        {
            return Enum.TryParse<AgentLanguageModelSelectionMode>(value, true, out var parsed)
                ? parsed
                : AgentLanguageModelSelectionMode.SpecificLanguageModel;
        }

        private static AgentRuntimeRole ParseRuntimeRole(string? value)
        {
            return Enum.TryParse<AgentRuntimeRole>(value, true, out var parsed)
                ? parsed
                : AgentRuntimeRole.MainOnly;
        }

    }
}
