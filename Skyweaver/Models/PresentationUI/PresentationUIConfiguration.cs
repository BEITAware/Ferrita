using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Models.PresentationUI
{
    public sealed class PresentationUIConfiguration : ObservableObject
    {
        private bool _collapseReasoningByDefault = true;

        public bool CollapseReasoningByDefault
        {
            get => _collapseReasoningByDefault;
            set => SetProperty(ref _collapseReasoningByDefault, value);
        }
    }
}
