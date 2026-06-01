using Skyweaver.Controls.LanguageModelConfigurationControl.Models;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;

namespace Skyweaver.Services.AgentLoop
{
    public sealed class AgentLoopTokenCounter
    {
        private readonly ILanguageModelChatService _chatService;
        private readonly AgentLoopCompactionStore _compactionStore;

        public AgentLoopTokenCounter()
            : this(new LanguageModelChatService(), new AgentLoopCompactionStore())
        {
        }

        public AgentLoopTokenCounter(
            ILanguageModelChatService chatService,
            AgentLoopCompactionStore compactionStore)
        {
            _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
            _compactionStore = compactionStore ?? throw new ArgumentNullException(nameof(compactionStore));
        }

        public async Task<AgentLoopTokenCountResult> CountAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            string? compactionFilePath,
            Func<LanguageModelMediaProcessingProgress, CancellationToken, ValueTask>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(messages);

            var modelKey = BuildModelKey(model);
            var hash = AgentLoopCompactionStore.CreateTokenCountHash(messages);
            if (_compactionStore.TryGetTokenCount(compactionFilePath, modelKey, hash, out var cachedTokenCount))
            {
                return new AgentLoopTokenCountResult(cachedTokenCount, modelKey, hash, "Compaction.xml cache");
            }

            var tokenCount = await _chatService.CountTokensAsync(
                model,
                messages.Select(message => message.Clone()).ToArray(),
                cancellationToken,
                progressCallback).ConfigureAwait(false);
            _compactionStore.SaveTokenCount(
                compactionFilePath,
                modelKey,
                hash,
                tokenCount,
                "LLM API");

            return new AgentLoopTokenCountResult(tokenCount, modelKey, hash, "LLM API");
        }

        public static int EstimateMessages(IReadOnlyList<LanguageModelChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            if (messages.Count == 0)
            {
                return 0;
            }

            var total = 0;
            foreach (var message in messages)
            {
                total += 4;
                total += EstimateText(message.Role.ToString());
                total += EstimateText(message.AuthorName);
                foreach (var block in message.ContentBlocks)
                {
                    total += EstimateText(block.Content);
                    total += EstimateText(block.ResourcePath);
                    total += EstimateText(block.MediaType);
                    if (block.Data?.Length > 0)
                    {
                        total += Math.Max(1, block.Data.Length / 1024);
                    }
                }
            }

            return Math.Max(1, total);
        }

        public static int EstimateText(string? content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? 0
                : Math.Max(1, (int)Math.Ceiling(content.Length / 4.0d));
        }

        private static string BuildModelKey(LanguageModelDefinition model)
        {
            var interfaceType = string.IsNullOrWhiteSpace(model.InterfaceType) ? "unknown-interface" : model.InterfaceType.Trim();
            var modelId = string.IsNullOrWhiteSpace(model.SummaryModelId)
                ? string.IsNullOrWhiteSpace(model.Key) ? "unknown-model" : model.Key.Trim()
                : model.SummaryModelId.Trim();
            return $"{interfaceType}:{modelId}";
        }
    }

    public sealed record AgentLoopTokenCountResult(
        int TokenCount,
        string ModelKey,
        string Hash,
        string Source);
}
