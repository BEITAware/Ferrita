using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Skyweaver.Controls.LanguageModelConfigurationControl.Models;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    public sealed class LanguageModelConfigurationRepository
    {
        private readonly LanguageModelConfigurationPathProvider _pathProvider;
        private readonly object _syncRoot = new();

        public LanguageModelConfigurationRepository(LanguageModelConfigurationPathProvider pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public string ConfigurationFilePath => _pathProvider.LanguageModelFilePath;

        public IReadOnlyList<LanguageModelDefinition> Load()
        {
            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                if (!File.Exists(ConfigurationFilePath))
                {
                    Save(Array.Empty<LanguageModelDefinition>());
                    return Array.Empty<LanguageModelDefinition>();
                }

                var document = XDocument.Load(ConfigurationFilePath);
                var root = document.Root ?? throw new InvalidDataException("LanguageModel 配置 XML 缺少根节点。");

                return root.Elements("LanguageModel")
                    .Select(element => new LanguageModelDefinition
                    {
                        Key = ((string?)element.Element("Key") ?? Guid.NewGuid().ToString("N")).Trim(),
                        DisplayName = ((string?)element.Element("DisplayName") ?? string.Empty).Trim(),
                        InterfaceType = LanguageModelInterfaceCatalog.NormalizeInterfaceType(
                            (string?)element.Element("InterfaceType") ??
                            (string?)element.Element("InterfaceSettings")?.Attribute("Type")),
                        ContextWindowTokens = ParseInt((string?)element.Element("ContextWindowTokens"), LanguageModelDefinition.DefaultContextWindowTokens)
                    })
                    .Select(definition =>
                    {
                        var element = root.Elements("LanguageModel").First(item => ((string?)item.Element("Key") ?? string.Empty).Trim() == definition.Key);
                        definition.InterfaceSettings = LoadInterfaceSettings(element, definition.InterfaceType);
                        return definition;
                    })
                    .ToArray();
            }
        }

        public void Save(IEnumerable<LanguageModelDefinition> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);

            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                var document = new XDocument(
                    new XElement("LanguageModels",
                        new XAttribute("SchemaVersion", 2),
                        definitions.Select(definition => new XElement("LanguageModel",
                            new XElement("Key", definition.Key),
                            new XElement("DisplayName", definition.DisplayName),
                            new XElement("InterfaceType", definition.InterfaceType),
                            new XElement("ContextWindowTokens", definition.EffectiveContextWindowTokens),
                            SaveInterfaceSettings(definition.InterfaceSettings)))));

                document.Save(ConfigurationFilePath);
            }
        }

        private void EnsureConfigurationDirectory()
        {
            Directory.CreateDirectory(_pathProvider.ConfigurationDirectoryPath);
        }

        private static decimal ParseDecimal(string? value, decimal fallback)
        {
            return decimal.TryParse(value, out var result) ? result : fallback;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out var result) ? result : fallback;
        }

        private static long ParseLong(string? value, long fallback)
        {
            return long.TryParse(value, out var result) ? result : fallback;
        }

        private static bool ParseBool(string? value, bool fallback = false)
        {
            return bool.TryParse(value, out var result) ? result : fallback;
        }

        private static LanguageModelInterfaceSettings LoadInterfaceSettings(XElement element, string interfaceType)
        {
            var settingsElement = element.Element("InterfaceSettings");

            return interfaceType.Trim().ToUpperInvariant() switch
            {
                "GOOGLE" => LoadGoogleSettings(element, settingsElement),
                "MEAI" => LoadMeaiSettings(element, settingsElement),
                _ => throw new InvalidDataException($"Unsupported interface type configuration: {interfaceType}")
            };
        }

        private static GoogleLanguageModelSettings LoadGoogleSettings(XElement legacyElement, XElement? settingsElement)
        {
            XElement ValueSource(string name) => settingsElement?.Element(name) ?? legacyElement.Element(name) ?? new XElement(name);

            return new GoogleLanguageModelSettings
            {
                ModelId = ((string?)ValueSource("ModelId") ?? string.Empty).Trim(),
                ApiKey = ((string?)ValueSource("ApiKey") ?? string.Empty).Trim(),
                BaseUrl = ((string?)ValueSource("BaseUrl") ?? "https://generativelanguage.googleapis.com").Trim(),
                UseTemperature = ParseBool((string?)ValueSource("UseTemperature")),
                Temperature = ParseDecimal((string?)ValueSource("Temperature"), 1.0m),
                UseTopP = ParseBool((string?)ValueSource("UseTopP")),
                TopP = ParseDecimal((string?)ValueSource("TopP"), 0.95m),
                UseMaxOutputTokens = ParseBool((string?)ValueSource("UseMaxOutputTokens")),
                MaxOutputTokens = ParseInt((string?)ValueSource("MaxOutputTokens"), 2048),
                UseThinkingLevel = ParseBool((string?)ValueSource("UseThinkingLevel")),
                ThinkingLevel = ((string?)ValueSource("ThinkingLevel") ?? "High").Trim(),
                UseThinkingBudget = ParseBool((string?)ValueSource("UseThinkingBudget")),
                ThinkingBudget = ParseInt((string?)ValueSource("ThinkingBudget"), -1),
                IncludeThoughts = ParseBool((string?)ValueSource("IncludeThoughts"), true)
            };
        }

        private static MeaiLanguageModelSettings LoadMeaiSettings(XElement legacyElement, XElement? settingsElement)
        {
            XElement ValueSource(string name) => settingsElement?.Element(name) ?? legacyElement.Element(name) ?? new XElement(name);

            return new MeaiLanguageModelSettings
            {
                ModelId = ((string?)ValueSource("ModelId") ?? string.Empty).Trim(),
                ApiKey = ((string?)ValueSource("ApiKey") ?? string.Empty).Trim(),
                BaseUrl = ((string?)ValueSource("BaseUrl") ?? string.Empty).Trim(),
                UseTemperature = ParseBool((string?)ValueSource("UseTemperature")),
                Temperature = ParseDecimal((string?)ValueSource("Temperature"), 1.0m),
                UseTopP = ParseBool((string?)ValueSource("UseTopP")),
                TopP = ParseDecimal((string?)ValueSource("TopP"), 1.0m),
                UseMaxOutputTokens = ParseBool((string?)ValueSource("UseMaxOutputTokens")),
                MaxOutputTokens = ParseInt((string?)ValueSource("MaxOutputTokens"), 2048),
                UsePresencePenalty = ParseBool((string?)ValueSource("UsePresencePenalty")),
                PresencePenalty = ParseDecimal((string?)ValueSource("PresencePenalty"), 0m),
                UseFrequencyPenalty = ParseBool((string?)ValueSource("UseFrequencyPenalty")),
                FrequencyPenalty = ParseDecimal((string?)ValueSource("FrequencyPenalty"), 0m),
                UseSeed = ParseBool((string?)ValueSource("UseSeed")),
                Seed = ParseLong((string?)ValueSource("Seed"), 0L),
                UseReasoningEffort = ParseBool((string?)ValueSource("UseReasoningEffort")),
                ReasoningEffort = ((string?)ValueSource("ReasoningEffort") ?? "Medium").Trim(),
                UseReasoningOutput = ParseBool((string?)ValueSource("UseReasoningOutput")),
                ReasoningOutput = ((string?)ValueSource("ReasoningOutput") ?? "Full").Trim()
            };
        }

        private static XElement SaveInterfaceSettings(LanguageModelInterfaceSettings settings)
        {
            return settings switch
            {
                GoogleLanguageModelSettings google => new XElement("InterfaceSettings",
                    new XAttribute("Type", google.InterfaceType),
                    new XElement("ModelId", google.ModelId),
                    new XElement("ApiKey", google.ApiKey),
                    new XElement("BaseUrl", google.BaseUrl),
                    new XElement("UseTemperature", google.UseTemperature),
                    new XElement("Temperature", google.Temperature),
                    new XElement("UseTopP", google.UseTopP),
                    new XElement("TopP", google.TopP),
                    new XElement("UseMaxOutputTokens", google.UseMaxOutputTokens),
                    new XElement("MaxOutputTokens", google.MaxOutputTokens),
                    new XElement("UseThinkingLevel", google.UseThinkingLevel),
                    new XElement("ThinkingLevel", google.ThinkingLevel),
                    new XElement("UseThinkingBudget", google.UseThinkingBudget),
                    new XElement("ThinkingBudget", google.ThinkingBudget),
                    new XElement("IncludeThoughts", google.IncludeThoughts)),
                MeaiLanguageModelSettings meai => new XElement("InterfaceSettings",
                    new XAttribute("Type", meai.InterfaceType),
                    new XElement("ModelId", meai.ModelId),
                    new XElement("ApiKey", meai.ApiKey),
                    new XElement("BaseUrl", meai.BaseUrl),
                    new XElement("UseTemperature", meai.UseTemperature),
                    new XElement("Temperature", meai.Temperature),
                    new XElement("UseTopP", meai.UseTopP),
                    new XElement("TopP", meai.TopP),
                    new XElement("UseMaxOutputTokens", meai.UseMaxOutputTokens),
                    new XElement("MaxOutputTokens", meai.MaxOutputTokens),
                    new XElement("UsePresencePenalty", meai.UsePresencePenalty),
                    new XElement("PresencePenalty", meai.PresencePenalty),
                    new XElement("UseFrequencyPenalty", meai.UseFrequencyPenalty),
                    new XElement("FrequencyPenalty", meai.FrequencyPenalty),
                    new XElement("UseSeed", meai.UseSeed),
                    new XElement("Seed", meai.Seed),
                    new XElement("UseReasoningEffort", meai.UseReasoningEffort),
                    new XElement("ReasoningEffort", meai.ReasoningEffort),
                    new XElement("UseReasoningOutput", meai.UseReasoningOutput),
                    new XElement("ReasoningOutput", meai.ReasoningOutput)),
                _ => throw new InvalidDataException($"不支持的接口类型配置：{settings.InterfaceType}")
            };
        }
    }
}
