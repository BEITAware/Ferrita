using AerialCity.Embedding;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Services
{
    public static class EmbeddingModelInterfaceCatalog
    {
        public const string DefaultInterfaceType = "OPENAI";

        private static readonly IReadOnlyDictionary<string, Func<EmbeddingModelInterfaceSettings>> s_settingsFactories =
            new Dictionary<string, Func<EmbeddingModelInterfaceSettings>>(StringComparer.OrdinalIgnoreCase)
            {
                ["GOOGLE"] = static () => new GoogleEmbeddingModelSettings(),
                ["OPENAI"] = static () => new OpenAiEmbeddingModelSettings()
            };

        public static IReadOnlyList<string> AvailableInterfaceTypes { get; } = s_settingsFactories.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        public static EmbeddingModelInterfaceSettings CreateInterfaceSettings(string? interfaceType)
        {
            var normalizedType = NormalizeInterfaceType(interfaceType);
            if (s_settingsFactories.TryGetValue(normalizedType, out var factory))
            {
                return factory();
            }

            throw new InvalidOperationException($"Unsupported embedding model interface type: {normalizedType}");
        }

        public static bool IsKnownInterfaceType(string? interfaceType)
        {
            return s_settingsFactories.ContainsKey(NormalizeInterfaceType(interfaceType));
        }

        public static string NormalizeInterfaceType(string? interfaceType)
        {
            var normalizedType = (interfaceType ?? string.Empty).Trim().ToUpperInvariant();
            return normalizedType.Length == 0 ? DefaultInterfaceType : normalizedType;
        }

        internal static EmbeddingApiType ToApiType(string? interfaceType)
        {
            return NormalizeInterfaceType(interfaceType) switch
            {
                "GOOGLE" => EmbeddingApiType.Google,
                "OPENAI" => EmbeddingApiType.OpenAI,
                var value => throw new InvalidOperationException($"Unsupported embedding model interface type: {value}")
            };
        }
    }
}
