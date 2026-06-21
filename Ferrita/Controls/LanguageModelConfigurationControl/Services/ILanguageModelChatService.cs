using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    public interface ILanguageModelChatService
    {
        Task<LanguageModelChatResponse> GetResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null);

        Task<int> CountTokensAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null);

        IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? mediaProcessingProgress = null,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null);
    }
}
