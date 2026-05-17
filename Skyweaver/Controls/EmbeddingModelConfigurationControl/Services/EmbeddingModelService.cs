using AerialCity.Core.Primitives;
using AerialCity.Embedding;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Services
{
    public sealed class EmbeddingModelService
    {
        private readonly ApiEmbeddingService _apiEmbeddingService;

        public EmbeddingModelService()
            : this(new ApiEmbeddingService())
        {
        }

        public EmbeddingModelService(ApiEmbeddingService apiEmbeddingService)
        {
            _apiEmbeddingService = apiEmbeddingService ?? throw new ArgumentNullException(nameof(apiEmbeddingService));
        }

        public Task<EmbeddingModelResult> EmbedTextAsync(
            EmbeddingModelDefinition model,
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            return EmbedAsync(model, EmbeddingInput.FromText(text), cancellationToken);
        }

        public async Task<EmbeddingModelResult> EmbedAsync(
            EmbeddingModelDefinition model,
            EmbeddingInput input,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(input);

            var request = CreateRequest(model, input);
            var result = await _apiEmbeddingService.EmbedAsync(request, cancellationToken).ConfigureAwait(false);
            return new EmbeddingModelResult
            {
                Vector = result.Vector,
                Model = result.Model,
                ApiType = result.ApiType
            };
        }

        public ApiEmbeddingRequest CreateRequest(
            EmbeddingModelDefinition model,
            EmbeddingInput input)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(input);

            if (!model.SupportsMultimodalEmbedding && ContainsBinaryContent(input))
            {
                throw new InvalidOperationException("当前嵌入模型未配置为支持多模态嵌入。");
            }

            var settings = model.InterfaceSettings;
            var parameters = BuildParameters(settings);
            var request = new ApiEmbeddingRequest
            {
                ApiKey = GetApiKey(settings),
                BaseUrl = GetBaseUrl(settings),
                ApiType = EmbeddingModelInterfaceCatalog.ToApiType(model.InterfaceType),
                Model = GetModelId(settings),
                Content = input,
                Parameters = parameters,
                Dimensions = model.Dimensions > 0 ? model.Dimensions : null,
                Normalize = model.Normalize,
                IncludeBinaryDataInTextProjection = model.IncludeBinaryDataInTextProjection
            };

            return request;
        }

        private static bool ContainsBinaryContent(EmbeddingInput input)
        {
            return input.Parts.Any(part => part.Binary.HasValue && !part.Binary.Value.IsEmpty);
        }

        private static Dictionary<string, object?> BuildParameters(EmbeddingModelInterfaceSettings settings)
        {
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

            switch (settings)
            {
                case OpenAiEmbeddingModelSettings openAi:
                    if (!string.IsNullOrWhiteSpace(openAi.User))
                    {
                        parameters["user"] = openAi.User;
                    }

                    break;

                case GoogleEmbeddingModelSettings google:
                    if (google.UseTaskType && !string.IsNullOrWhiteSpace(google.TaskType))
                    {
                        parameters["taskType"] = google.TaskType;
                    }

                    if (google.SendInlineData)
                    {
                        parameters["sendInlineData"] = true;
                    }

                    break;
            }

            return parameters;
        }

        private static string GetApiKey(EmbeddingModelInterfaceSettings settings)
        {
            return settings switch
            {
                OpenAiEmbeddingModelSettings openAi => RequireText(openAi.ApiKey, "API Key cannot be empty."),
                GoogleEmbeddingModelSettings google => RequireText(google.ApiKey, "API Key cannot be empty."),
                _ => throw new InvalidOperationException($"Unsupported embedding interface settings: {settings.InterfaceType}")
            };
        }

        private static string GetBaseUrl(EmbeddingModelInterfaceSettings settings)
        {
            return settings switch
            {
                OpenAiEmbeddingModelSettings openAi => RequireText(openAi.BaseUrl, "Base URL cannot be empty."),
                GoogleEmbeddingModelSettings google => RequireText(google.BaseUrl, "Base URL cannot be empty."),
                _ => throw new InvalidOperationException($"Unsupported embedding interface settings: {settings.InterfaceType}")
            };
        }

        private static string GetModelId(EmbeddingModelInterfaceSettings settings)
        {
            return settings switch
            {
                OpenAiEmbeddingModelSettings openAi => RequireText(openAi.ModelId, "Model ID cannot be empty."),
                GoogleEmbeddingModelSettings google => RequireText(google.ModelId, "Model ID cannot be empty."),
                _ => throw new InvalidOperationException($"Unsupported embedding interface settings: {settings.InterfaceType}")
            };
        }

        private static string RequireText(string value, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(message);
            }

            return value.Trim();
        }
    }

    public sealed class EmbeddingModelResult
    {
        public required EmbeddingVector Vector { get; init; }

        public required string Model { get; init; }

        public EmbeddingApiType ApiType { get; init; }
    }
}
