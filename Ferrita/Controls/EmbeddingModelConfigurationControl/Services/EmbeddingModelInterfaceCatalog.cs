using AerialCity.Embedding;
using Ferrita.Controls.EmbeddingModelConfigurationControl.Models;

namespace Ferrita.Controls.EmbeddingModelConfigurationControl.Services
{
    public static class EmbeddingModelInterfaceCatalog
    {
        public const string DefaultInterfaceType = "OpenAI";

        private static readonly IReadOnlyDictionary<string, Func<EmbeddingModelInterfaceSettings>> s_settingsFactories =
            new Dictionary<string, Func<EmbeddingModelInterfaceSettings>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Google"] = static () => new GoogleEmbeddingModelSettings(),
                ["OpenAI"] = static () => new OpenAiEmbeddingModelSettings()
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
            var normalizedType = (interfaceType ?? string.Empty).Trim();
            if (normalizedType.Length == 0) return DefaultInterfaceType;
            if (string.Equals(normalizedType, "openai", StringComparison.OrdinalIgnoreCase)) return "OpenAI";
            if (string.Equals(normalizedType, "google", StringComparison.OrdinalIgnoreCase)) return "Google";
            return normalizedType;
        }

        internal static EmbeddingApiType ToApiType(string? interfaceType)
        {
            var normalized = NormalizeInterfaceType(interfaceType);
            if (string.Equals(normalized, "Google", StringComparison.OrdinalIgnoreCase)) return EmbeddingApiType.Google;
            if (string.Equals(normalized, "OpenAI", StringComparison.OrdinalIgnoreCase)) return EmbeddingApiType.OpenAI;
            throw new InvalidOperationException($"Unsupported embedding model interface type: {normalized}");
        }
    }
}
