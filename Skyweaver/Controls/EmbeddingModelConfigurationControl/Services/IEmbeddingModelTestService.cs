using Skyweaver.Controls.EmbeddingModelConfigurationControl.Models;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Services
{
    public interface IEmbeddingModelTestService
    {
        Task<string> TestAsync(
            EmbeddingModelDefinition model,
            CancellationToken cancellationToken = default);
    }
}
