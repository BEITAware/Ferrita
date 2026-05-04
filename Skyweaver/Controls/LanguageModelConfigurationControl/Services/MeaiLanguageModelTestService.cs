using Skyweaver.Controls.LanguageModelConfigurationControl.Models;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    public sealed class LanguageModelTestService : ILanguageModelTestService
    {
        private const string TestPrompt = "写一句话欢迎用户使用 Skyweaver 人工智能代理应用程序。";

        private readonly LanguageModelChatService _chatService;

        public LanguageModelTestService()
            : this(new LanguageModelChatService())
        {
        }

        public LanguageModelTestService(LanguageModelChatService chatService)
        {
            _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        }

        public async Task StreamTestAsync(
            LanguageModelDefinition model,
            Action<string> onTextReceived,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(onTextReceived);

            var messages = new[]
            {
                new LanguageModelChatMessage(LanguageModelChatRole.User, TestPrompt)
            };

            await foreach (var update in _chatService.GetStreamingResponseAsync(
                               model,
                               messages,
                               cancellationToken).ConfigureAwait(false))
            {
                var text = update.TextDelta;
                if (!string.IsNullOrEmpty(text))
                {
                    onTextReceived(text);
                }
            }
        }
    }
}
