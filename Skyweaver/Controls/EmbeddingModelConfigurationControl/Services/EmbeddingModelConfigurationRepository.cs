using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Services
{
    public sealed class EmbeddingModelConfigurationRepository
    {
        private readonly EmbeddingModelConfigurationPathProvider _pathProvider;
        private readonly object _syncRoot = new();

        public EmbeddingModelConfigurationRepository(EmbeddingModelConfigurationPathProvider pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public string ConfigurationFilePath => _pathProvider.EmbeddingModelFilePath;

        public IReadOnlyList<EmbeddingModelDefinition> Load()
        {
            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                if (!File.Exists(ConfigurationFilePath))
                {
                    Save(Array.Empty<EmbeddingModelDefinition>());
                    return Array.Empty<EmbeddingModelDefinition>();
                }

                var document = XDocument.Load(ConfigurationFilePath);
                var root = document.Root ?? throw new InvalidDataException("EmbeddingModel 配置 XML 缺少根节点。");

                return root.Elements("EmbeddingModel")
                    .Select(LoadDefinition)
                    .ToArray();
            }
        }

        public void Save(IEnumerable<EmbeddingModelDefinition> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);

            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                var document = new XDocument(
                    new XElement("EmbeddingModels",
                        new XAttribute("SchemaVersion", 2),
                        definitions.Select(definition => new XElement("EmbeddingModel",
                            new XElement("Key", definition.Key),
                            new XElement("DisplayName", definition.DisplayName),
                            new XElement("InterfaceType", definition.InterfaceType),
                            new XElement("Dimensions", definition.Dimensions),
                            new XElement("MaxInputTokens", definition.MaxInputTokens),
                            new XElement("Normalize", definition.Normalize),
                            new XElement("SupportsMultimodalEmbedding", definition.SupportsMultimodalEmbedding),
                            new XElement("IncludeBinaryDataInTextProjection", definition.IncludeBinaryDataInTextProjection),
                            SaveInterfaceSettings(definition.InterfaceSettings)))));

                document.Save(ConfigurationFilePath);
            }
        }

        private void EnsureConfigurationDirectory()
        {
            Directory.CreateDirectory(_pathProvider.ConfigurationDirectoryPath);
        }

        private static EmbeddingModelDefinition LoadDefinition(XElement element)
        {
            var interfaceType = EmbeddingModelInterfaceCatalog.NormalizeInterfaceType(
                (string?)element.Element("InterfaceType") ??
                (string?)element.Element("InterfaceSettings")?.Attribute("Type"));

            var definition = new EmbeddingModelDefinition
            {
                Key = ((string?)element.Element("Key") ?? Guid.NewGuid().ToString("N")).Trim(),
                DisplayName = ((string?)element.Element("DisplayName") ?? string.Empty).Trim(),
                InterfaceType = interfaceType,
                Dimensions = ParseInt((string?)element.Element("Dimensions"), EmbeddingModelDefinition.DefaultDimensions),
                MaxInputTokens = ParseInt((string?)element.Element("MaxInputTokens"), EmbeddingModelDefinition.DefaultMaxInputTokens),
                Normalize = ParseBool((string?)element.Element("Normalize"), true),
                SupportsMultimodalEmbedding = ParseBool((string?)element.Element("SupportsMultimodalEmbedding")),
                IncludeBinaryDataInTextProjection = ParseBool((string?)element.Element("IncludeBinaryDataInTextProjection"))
            };

            definition.InterfaceSettings = LoadInterfaceSettings(element, definition.InterfaceType);
            return definition;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out var result) ? result : fallback;
        }

        private static bool ParseBool(string? value, bool fallback = false)
        {
            return bool.TryParse(value, out var result) ? result : fallback;
        }

        private static EmbeddingModelInterfaceSettings LoadInterfaceSettings(XElement element, string interfaceType)
        {
            var settingsElement = element.Element("InterfaceSettings");

            return interfaceType.Trim().ToUpperInvariant() switch
            {
                "GOOGLE" => LoadGoogleSettings(element, settingsElement),
                "OPENAI" => LoadOpenAiSettings(element, settingsElement),
                _ => throw new InvalidDataException($"Unsupported embedding interface type configuration: {interfaceType}")
            };
        }

        private static OpenAiEmbeddingModelSettings LoadOpenAiSettings(XElement legacyElement, XElement? settingsElement)
        {
            XElement ValueSource(string name) => settingsElement?.Element(name) ?? legacyElement.Element(name) ?? new XElement(name);

            return new OpenAiEmbeddingModelSettings
            {
                ModelId = ((string?)ValueSource("ModelId") ?? string.Empty).Trim(),
                ApiKey = ((string?)ValueSource("ApiKey") ?? string.Empty).Trim(),
                BaseUrl = ((string?)ValueSource("BaseUrl") ?? "https://api.openai.com/v1").Trim(),
                User = ((string?)ValueSource("User") ?? string.Empty).Trim()
            };
        }

        private static GoogleEmbeddingModelSettings LoadGoogleSettings(XElement legacyElement, XElement? settingsElement)
        {
            XElement ValueSource(string name) => settingsElement?.Element(name) ?? legacyElement.Element(name) ?? new XElement(name);

            return new GoogleEmbeddingModelSettings
            {
                ModelId = ((string?)ValueSource("ModelId") ?? string.Empty).Trim(),
                ApiKey = ((string?)ValueSource("ApiKey") ?? string.Empty).Trim(),
                BaseUrl = ((string?)ValueSource("BaseUrl") ?? "https://generativelanguage.googleapis.com/v1beta").Trim(),
                UseTaskType = ParseBool((string?)ValueSource("UseTaskType")),
                TaskType = ((string?)ValueSource("TaskType") ?? "RETRIEVAL_DOCUMENT").Trim(),
                SendInlineData = ParseBool((string?)ValueSource("SendInlineData"))
            };
        }

        private static XElement SaveInterfaceSettings(EmbeddingModelInterfaceSettings settings)
        {
            return settings switch
            {
                OpenAiEmbeddingModelSettings openAi => new XElement("InterfaceSettings",
                    new XAttribute("Type", openAi.InterfaceType),
                    new XElement("ModelId", openAi.ModelId),
                    new XElement("ApiKey", openAi.ApiKey),
                    new XElement("BaseUrl", openAi.BaseUrl),
                    new XElement("User", openAi.User)),
                GoogleEmbeddingModelSettings google => new XElement("InterfaceSettings",
                    new XAttribute("Type", google.InterfaceType),
                    new XElement("ModelId", google.ModelId),
                    new XElement("ApiKey", google.ApiKey),
                    new XElement("BaseUrl", google.BaseUrl),
                    new XElement("UseTaskType", google.UseTaskType),
                    new XElement("TaskType", google.TaskType),
                    new XElement("SendInlineData", google.SendInlineData)),
                _ => throw new InvalidDataException($"不支持的嵌入模型接口配置：{settings.InterfaceType}")
            };
        }
    }
}
