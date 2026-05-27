using System;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Panels.DocumentWorkspace.Contracts;
using Skyweaver.Services.Localization;

namespace Skyweaver.Panels.MultiFunctionArea.ViewModels
{
    public sealed class PlaceholderPanelViewModel : ObservableObject, IWorkspaceTabAware
    {
        private IWorkspaceTabController? _controller;
        private readonly string _titleKey;
        private readonly string _titleFallback;
        private readonly string _descriptionKey;
        private readonly string _descriptionFallback;
        private readonly string _hintKey;
        private readonly string _hintFallback;

        public string Title => string.IsNullOrEmpty(_titleKey) 
            ? _titleFallback 
            : LocalizationRuntime.Instance.GetString(_titleKey, _titleFallback);

        public string Description => string.IsNullOrEmpty(_descriptionKey) 
            ? _descriptionFallback 
            : LocalizationRuntime.Instance.GetString(_descriptionKey, _descriptionFallback);

        public string Hint => string.IsNullOrEmpty(_hintKey) 
            ? _hintFallback 
            : LocalizationRuntime.Instance.GetString(_hintKey, _hintFallback);

        public PlaceholderPanelViewModel(string title, string description, string hint)
        {
            _titleKey = string.Empty;
            _titleFallback = title;
            _descriptionKey = string.Empty;
            _descriptionFallback = description;
            _hintKey = string.Empty;
            _hintFallback = hint;
        }

        public PlaceholderPanelViewModel(
            string titleKey, string titleFallback,
            string descriptionKey, string descriptionFallback,
            string hintKey, string hintFallback)
        {
            _titleKey = titleKey;
            _titleFallback = titleFallback;
            _descriptionKey = descriptionKey;
            _descriptionFallback = descriptionFallback;
            _hintKey = hintKey;
            _hintFallback = hintFallback;

            LocalizationRuntime.Instance.LanguageChanged += HandleLanguageChanged;
        }

        private void HandleLanguageChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Hint));
        }

        public void AttachToWorkspaceTab(IWorkspaceTabController controller)
        {
            _controller = controller;
        }

        public void CloseSelf()
        {
            if (!string.IsNullOrEmpty(_titleKey))
            {
                LocalizationRuntime.Instance.LanguageChanged -= HandleLanguageChanged;
            }
            _controller?.CloseSelf();
        }
    }
}
