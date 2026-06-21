using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
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
                        ContextWindowTokens = ParseInt((string?)element.Element("ContextWindowTokens"), LanguageModelDefinition.DefaultContextWindowTokens),
                        EnableImageInput = ParseBool((string?)element.Element("EnableImageInput"), true),
                        EnableAudioInput = ParseBool((string?)element.Element("EnableAudioInput"), true),
                        EnableVideoInput = ParseBool((string?)element.Element("EnableVideoInput"), true),
                        EnableDocumentInput = ParseBool((string?)element.Element("EnableDocumentInput"), true),
                        EnableUniversalToolCalling = ParseBool((string?)element.Element("EnableUniversalToolCalling"), false)
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
                            new XElement("EnableImageInput", definition.EnableImageInput),
                            new XElement("EnableAudioInput", definition.EnableAudioInput),
                            new XElement("EnableVideoInput", definition.EnableVideoInput),
                            new XElement("EnableDocumentInput", definition.EnableDocumentInput),
                            new XElement("EnableUniversalToolCalling", definition.EnableUniversalToolCalling),
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
                "OPENAI" or "OPENAI CHAT COMPLETIONS API" => LoadOpenAiSettings(element, settingsElement, isResponsesApi: false),
                "OPENAI RESPONSES API" => LoadOpenAiSettings(element, settingsElement, isResponsesApi: true),
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

        private static OpenAiLanguageModelSettings LoadOpenAiSettings(XElement legacyElement, XElement? settingsElement, bool isResponsesApi = false)
        {
            XElement ValueSource(string name) => settingsElement?.Element(name) ?? legacyElement.Element(name) ?? new XElement(name);

            OpenAiLanguageModelSettings settings = isResponsesApi ? new OpenAiResponsesLanguageModelSettings() : new OpenAiLanguageModelSettings();

            settings.ModelId = ((string?)ValueSource("ModelId") ?? string.Empty).Trim();
            settings.ApiKey = ((string?)ValueSource("ApiKey") ?? string.Empty).Trim();
            settings.BaseUrl = ((string?)ValueSource("BaseUrl") ?? string.Empty).Trim();
            settings.UseTemperature = ParseBool((string?)ValueSource("UseTemperature"));
            settings.Temperature = ParseDecimal((string?)ValueSource("Temperature"), 1.0m);
            settings.UseTopP = ParseBool((string?)ValueSource("UseTopP"));
            settings.TopP = ParseDecimal((string?)ValueSource("TopP"), 1.0m);
            settings.UseMaxOutputTokens = ParseBool((string?)ValueSource("UseMaxOutputTokens"));
            settings.MaxOutputTokens = ParseInt((string?)ValueSource("MaxOutputTokens"), 2048);
            settings.UsePresencePenalty = ParseBool((string?)ValueSource("UsePresencePenalty"));
            settings.PresencePenalty = ParseDecimal((string?)ValueSource("PresencePenalty"), 0m);
            settings.UseFrequencyPenalty = ParseBool((string?)ValueSource("UseFrequencyPenalty"));
            settings.FrequencyPenalty = ParseDecimal((string?)ValueSource("FrequencyPenalty"), 0m);
            settings.UseSeed = ParseBool((string?)ValueSource("UseSeed"));
            settings.Seed = ParseLong((string?)ValueSource("Seed"), 0L);
            settings.UseReasoningEffort = ParseBool((string?)ValueSource("UseReasoningEffort"));
            settings.ReasoningEffort = ((string?)ValueSource("ReasoningEffort") ?? "Medium").Trim();
            settings.UseReasoningOutput = ParseBool((string?)ValueSource("UseReasoningOutput"));
            settings.ReasoningOutput = ((string?)ValueSource("ReasoningOutput") ?? "Full").Trim();

            return settings;
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
                OpenAiLanguageModelSettings openai => new XElement("InterfaceSettings",
                    new XAttribute("Type", openai.InterfaceType),
                    new XElement("ModelId", openai.ModelId),
                    new XElement("ApiKey", openai.ApiKey),
                    new XElement("BaseUrl", openai.BaseUrl),
                    new XElement("UseTemperature", openai.UseTemperature),
                    new XElement("Temperature", openai.Temperature),
                    new XElement("UseTopP", openai.UseTopP),
                    new XElement("TopP", openai.TopP),
                    new XElement("UseMaxOutputTokens", openai.UseMaxOutputTokens),
                    new XElement("MaxOutputTokens", openai.MaxOutputTokens),
                    new XElement("UsePresencePenalty", openai.UsePresencePenalty),
                    new XElement("PresencePenalty", openai.PresencePenalty),
                    new XElement("UseFrequencyPenalty", openai.UseFrequencyPenalty),
                    new XElement("FrequencyPenalty", openai.FrequencyPenalty),
                    new XElement("UseSeed", openai.UseSeed),
                    new XElement("Seed", openai.Seed),
                    new XElement("UseReasoningEffort", openai.UseReasoningEffort),
                    new XElement("ReasoningEffort", openai.ReasoningEffort),
                    new XElement("UseReasoningOutput", openai.UseReasoningOutput),
                    new XElement("ReasoningOutput", openai.ReasoningOutput)),
                _ => throw new InvalidDataException($"不支持的接口类型配置：{settings.InterfaceType}")
            };
        }
    }
}
