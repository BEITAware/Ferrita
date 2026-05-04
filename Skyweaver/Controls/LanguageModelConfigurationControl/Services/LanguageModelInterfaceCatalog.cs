using Skyweaver.Controls.LanguageModelConfigurationControl.Models;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    internal interface ILanguageModelInterfaceAdapter
    {
        string InterfaceType { get; }

        LanguageModelInterfaceSettings CreateInterfaceSettings();

        void Validate(LanguageModelDefinition model);

        Task<LanguageModelChatResponse> GetResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default);

        IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default);
    }

    public static class LanguageModelInterfaceCatalog
    {
        public const string DefaultInterfaceType = "MEAI";

        private static readonly IReadOnlyDictionary<string, Func<ILanguageModelInterfaceAdapter>> s_adapterFactories =
            new Dictionary<string, Func<ILanguageModelInterfaceAdapter>>(StringComparer.OrdinalIgnoreCase)
            {
                ["GOOGLE"] = static () => new GoogleLanguageModelInterfaceAdapter(),
                ["MEAI"] = static () => new MeaiLanguageModelInterfaceAdapter()
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
            return normalizedType.Length == 0 ? DefaultInterfaceType : normalizedType;
        }
    }
}
