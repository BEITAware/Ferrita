using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.EmbeddingModelConfigurationControl.Models
{
    public abstract class EmbeddingModelInterfaceSettings : ObservableObject
    {
        public abstract string InterfaceType { get; }

        public abstract bool IsFullyConfigured { get; }

        public abstract string SummaryModelId { get; }
    }
}
