using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    internal interface ILanguageModelInterfaceAdapter
    {
        string InterfaceType { get; }

        LanguageModelInterfaceSettings CreateInterfaceSettings();

        void Validate(LanguageModelDefinition model);

        Task<LanguageModelChatResponse> GetResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null);

        Task<int> CountTokensAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default);

        IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null);
    }

    public static class LanguageModelInterfaceCatalog
    {
        public const string DefaultInterfaceType = "OpenAI Chat Completions API";

        private static readonly IReadOnlyDictionary<string, Func<ILanguageModelInterfaceAdapter>> s_adapterFactories =
            new Dictionary<string, Func<ILanguageModelInterfaceAdapter>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Google"] = static () => new GoogleLanguageModelInterfaceAdapter(),
                ["OpenAI Chat Completions API"] = static () => new OpenAiLanguageModelInterfaceAdapter(),
                ["OpenAI Responses API"] = static () => new OpenAiResponsesLanguageModelInterfaceAdapter()
            };

        public static IReadOnlyList<string> AvailableInterfaceTypes { get; } = s_adapterFactories.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        public static LanguageModelInterfaceSettings CreateInterfaceSettings(string? interfaceType)
        {
            return CreateAdapter(interfaceType).CreateInterfaceSettings();
        }

        internal static ILanguageModelInterfaceAdapter CreateAdapter(string? interfaceType)
        {
            var normalizedType = NormalizeInterfaceType(interfaceType);
            if (s_adapterFactories.TryGetValue(normalizedType, out var factory))
            {
                return factory();
            }

            throw new InvalidOperationException($"Unsupported language model interface type: {normalizedType}");
        }

        public static bool IsKnownInterfaceType(string? interfaceType)
        {
            return s_adapterFactories.ContainsKey(NormalizeInterfaceType(interfaceType));
        }

        public static string NormalizeInterfaceType(string? interfaceType)
        {
            var normalizedType = (interfaceType ?? string.Empty).Trim();
            if (normalizedType.Length == 0) return DefaultInterfaceType;
            if (string.Equals(normalizedType, "openai", StringComparison.OrdinalIgnoreCase)) return "OpenAI Chat Completions API";
            if (string.Equals(normalizedType, "OpenAI Chat Completions API", StringComparison.OrdinalIgnoreCase)) return "OpenAI Chat Completions API";
            if (string.Equals(normalizedType, "OpenAI Responses API", StringComparison.OrdinalIgnoreCase)) return "OpenAI Responses API";
            if (string.Equals(normalizedType, "google", StringComparison.OrdinalIgnoreCase)) return "Google";
            return normalizedType;
        }
    }
}
