using System.Globalization;
using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Services
{
    public sealed class EmbeddingModelTestService : IEmbeddingModelTestService
    {
        private const string TestText = "Skyweaver embedding model connectivity probe.";

        private readonly EmbeddingModelService _embeddingModelService;

        public EmbeddingModelTestService()
            : this(new EmbeddingModelService())
        {
        }

        public EmbeddingModelTestService(EmbeddingModelService embeddingModelService)
        {
            _embeddingModelService = embeddingModelService ?? throw new ArgumentNullException(nameof(embeddingModelService));
        }

        public async Task<string> TestAsync(
            EmbeddingModelDefinition model,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model);

            var result = await _embeddingModelService.EmbedTextAsync(
                    model,
                    TestText,
                    cancellationToken)
                .ConfigureAwait(false);

            var previewValues = Enumerable.Range(0, Math.Min(8, result.Vector.Dimensions))
                .Select(index => result.Vector[index].ToString("0.####", CultureInfo.InvariantCulture));
            var preview = string.Join(", ", previewValues);

            return $"嵌入测试成功：{result.Vector.Dimensions:N0} 维，范数 {result.Vector.Norm():0.####}，模型 {result.Model}，接口 {result.ApiType}。预览：[{preview}]";
        }
    }
}
