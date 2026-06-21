using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    public sealed class LanguageModelChatService : ILanguageModelChatService
    {
        public IReadOnlyList<string> AvailableInterfaceTypes => LanguageModelInterfaceCatalog.AvailableInterfaceTypes;

        public LanguageModelInterfaceSettings CreateInterfaceSettings(string? interfaceType)
        {
            return LanguageModelInterfaceCatalog.CreateInterfaceSettings(interfaceType);
        }

        public void Validate(LanguageModelDefinition model)
        {
            ResolveAdapter(model).Validate(model);
        }

        public async Task<LanguageModelChatResponse> GetResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(messages);

            var projectedMessages = await LanguageModelChatTransportProjection.ProjectMessagesAsync(
                    messages,
                    model,
                    mediaProcessingProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResolveAdapter(model).GetResponseAsync(model, projectedMessages, cancellationToken, tools).ConfigureAwait(false);
        }

        public async Task<int> CountTokensAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(messages);

            var projectedMessages = await LanguageModelChatTransportProjection.ProjectMessagesAsync(
                    messages,
                    model,
                    mediaProcessingProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResolveAdapter(model).CountTokensAsync(model, projectedMessages, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(messages);

            var projectedMessages = await LanguageModelChatTransportProjection.ProjectMessagesAsync(
                    messages,
                    model,
                    mediaProcessingProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            await foreach (var update in ResolveAdapter(model)
                               .GetStreamingResponseAsync(model, projectedMessages, cancellationToken, tools)
                               .ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private static ILanguageModelInterfaceAdapter ResolveAdapter(LanguageModelDefinition model)
        {
            ArgumentNullException.ThrowIfNull(model);
            return LanguageModelInterfaceCatalog.CreateAdapter(model.InterfaceType);
        }
    }
}
