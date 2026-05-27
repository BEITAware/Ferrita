using Skyweaver.Controls.ChatSessionControl.ViewModels;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Services.ChatSession;
using Skyweaver.Panels.DocumentWorkspace.ViewModels;
using Skyweaver.Panels.FileExplorer.ViewModels;
using Skyweaver.Panels.Filmstrip.ViewModels;
using Skyweaver.Panels.MultiFunctionArea.ViewModels;
using Skyweaver.Panels.SessionList.ViewModels;
using Skyweaver.Services.Daemon;

namespace Skyweaver.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly ChatSessionRepository _chatSessionRepository;

        public Skyweaver.Services.Notifications.NotificationService NotificationService => Skyweaver.Services.Notifications.NotificationService.Instance;

        public SessionListPanelViewModel SessionListPanel { get; }

        public FileExplorerPanelViewModel FileExplorerPanel { get; }

        public DocumentWorkspacePanelViewModel DocumentWorkspacePanel { get; }

        public FilmstripPanelViewModel FilmstripPanel { get; }

        public MultiFunctionAreaPanelViewModel MultiFunctionAreaPanel { get; }

        public SessionWorkspaceBridgeViewModel SessionWorkspaceBridge { get; }

        public MainViewModel()
        {
            _chatSessionRepository = new ChatSessionRepository();
            DocumentWorkspacePanel = new DocumentWorkspacePanelViewModel();
            SessionWorkspaceBridge = new SessionWorkspaceBridgeViewModel(DocumentWorkspacePanel, _chatSessionRepository);

            SessionListPanel = new SessionListPanelViewModel(SessionWorkspaceBridge.OpenSession, _chatSessionRepository);
            FileExplorerPanel = new FileExplorerPanelViewModel();
            FilmstripPanel = new FilmstripPanelViewModel();
            MultiFunctionAreaPanel = new MultiFunctionAreaPanelViewModel();
        }

        public async Task HandleGuiClosingAsync()
        {
            var openSessionViewModels = DocumentWorkspacePanel.OpenedDocuments
                .Select(document => document.ContentViewModel)
                .OfType<ChatSessionControlViewModel>()
                .Where(viewModel => !string.IsNullOrWhiteSpace(viewModel.SessionId))
                .ToArray();

            if (openSessionViewModels.Length == 0)
            {
                return;
            }

            foreach (var sessionViewModel in openSessionViewModels)
            {
                sessionViewModel.SaveSessionSnapshot();
            }

            var sessionIds = openSessionViewModels
                .Select(viewModel => viewModel.SessionId)
                .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
                .Select(sessionId => sessionId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            BackgroundMemoryQueue.Instance.Enqueue(sessionIds);
            await Task.CompletedTask;
        }
    }
}
