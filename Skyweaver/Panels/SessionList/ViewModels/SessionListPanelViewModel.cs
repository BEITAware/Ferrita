using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Models.ChatSession;
using Skyweaver.Panels.SessionList.Models;
using Skyweaver.Services.ChatSession;
using Skyweaver.Services.Localization;
using Skyweaver.Windows;

namespace Skyweaver.Panels.SessionList.ViewModels
{
    public sealed class SessionListPanelViewModel : ObservableObject
    {
        private const string LegacyDefaultSessionName = "新建会话";

        private readonly Action<SessionListItem> _openSession;
        private readonly ChatSessionRepository _chatSessionRepository;
        private readonly ChatSessionFlowBindingService _flowBindingService;
        private readonly List<SessionListItem> _allSessions = new();
        private SessionListItem? _selectedSession;
        private string _searchQuery = string.Empty;

        public ObservableCollection<SessionListItem> Sessions { get; } = new();

        public SessionListItem? SelectedSession
        {
            get => _selectedSession;
            set
            {
                if (SetProperty(ref _selectedSession, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    ApplySessionFilter();
                }
            }
        }

        public ICommand OpenSessionCommand { get; }

        public ICommand CreateSessionCommand { get; }

        public ICommand DeleteSessionCommand { get; }

        public ICommand RefreshSessionCommand { get; }

        public SessionListPanelViewModel(Action<SessionListItem> openSession, ChatSessionRepository chatSessionRepository)
        {
            _openSession = openSession;
            _chatSessionRepository = chatSessionRepository;
            _flowBindingService = new ChatSessionFlowBindingService();
            OpenSessionCommand = new RelayCommand<SessionListItem>(OpenSession, session => session != null);
            CreateSessionCommand = new RelayCommand(CreateSession);
            DeleteSessionCommand = new RelayCommand(DeleteSelectedSession, () => SelectedSession != null);
            RefreshSessionCommand = new RelayCommand(LoadSessions);

            LoadSessions();
        }

        private void OpenSession(SessionListItem? session)
        {
            if (session == null)
            {
                return;
            }

            _openSession(session);
        }

        private void LoadSessions()
        {
            var selectedId = SelectedSession?.Id;
            _allSessions.Clear();

            foreach (var session in _chatSessionRepository.LoadAll())
            {
                _allSessions.Add(CreateSessionListItem(session));
            }

            var preferredSelection = _allSessions.FirstOrDefault(s => s.Id == selectedId);
            ApplySessionFilter(preferredSelection);
        }

        private void CreateSession()
        {
            var owner = Application.Current?.MainWindow;
            var dialog = new CreateChatSessionDialog(
                _flowBindingService.GetAvailableBindings(ensureDefaultGraph: true),
                GetNextAvailableDefaultSessionName());

            if (owner != null && owner != dialog)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var createdSession = CreateSessionWithDefaultNameFallback(dialog.SessionName, dialog.SelectedFlowBinding);
                var listItem = CreateSessionListItem(createdSession);
                _allSessions.Insert(0, listItem);
                ApplySessionFilter(listItem);
                _openSession(listItem);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, L("SessionList.Create", "创建会话"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DeleteSelectedSession()
        {
            if (SelectedSession == null)
            {
                return;
            }

            var deletedSession = SelectedSession;
            if (deletedSession.Session != null)
            {
                try
                {
                    _chatSessionRepository.Delete(deletedSession.Session);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Application.Current?.MainWindow, ex.Message, L("SessionList.Delete.MessageBoxTitle", "删除会话"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _allSessions.Remove(deletedSession);
            ApplySessionFilter();
        }

        private static SessionListItem CreateSessionListItem(ChatSessionModel session)
        {
            return new SessionListItem
            {
                Id = session.SessionId,
                Title = session.Name,
                TimeLabel = FormatSessionTimeLabel(session.UpdatedAt),
                IconPath = string.IsNullOrWhiteSpace(session.IconPath) ? "pack://application:,,,/Resources/NewNodeGraphAlt.png" : session.IconPath,
                Session = session
            };
        }

        private static string FormatSessionTimeLabel(DateTime updatedAt)
        {
            var format = L("SessionList.TimeFormat", "HH:mm");
            return updatedAt.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);
        }

        private void ApplySessionFilter()
        {
            ApplySessionFilter(SelectedSession);
        }

        private void ApplySessionFilter(SessionListItem? preferredSelection)
        {
            var filteredSessions = string.IsNullOrWhiteSpace(SearchQuery)
                ? _allSessions
                : _allSessions
                    .Where(session => session.Title.Contains(SearchQuery.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    .ToList();

            Sessions.Clear();

            foreach (var session in filteredSessions)
            {
                Sessions.Add(session);
            }

            SelectedSession = preferredSelection != null && Sessions.Contains(preferredSelection)
                ? preferredSelection
                : Sessions.FirstOrDefault();
        }

        private ChatSessionModel CreateSessionWithDefaultNameFallback(string sessionName, ChatSessionFlowBinding? flowBinding)
        {
            if (!TryGetDefaultSessionNameNextIndex(sessionName, out _))
            {
                return _chatSessionRepository.Create(sessionName, flowBinding);
            }

            var candidateName = GetNextAvailableDefaultSessionName(sessionName);

            while (true)
            {
                try
                {
                    return _chatSessionRepository.Create(candidateName, flowBinding);
                }
                catch (InvalidOperationException) when (SessionNameExists(candidateName))
                {
                    candidateName = GetNextAvailableDefaultSessionName(candidateName);
                }
            }
        }

        private string GetNextAvailableDefaultSessionName(string? preferredName = null)
        {
            var existingNames = GetExistingSessionNames();

            var candidateName = string.IsNullOrWhiteSpace(preferredName)
                ? GetDefaultSessionName()
                : preferredName.Trim();

            if (!TryGetDefaultSessionNameNextIndex(candidateName, out var nextIndex))
            {
                return candidateName;
            }

            if (!existingNames.Contains(candidateName))
            {
                return candidateName;
            }

            while (true)
            {
                var numberedCandidate = $"{GetDefaultSessionName()} {nextIndex}";
                if (!existingNames.Contains(numberedCandidate))
                {
                    return numberedCandidate;
                }

                nextIndex++;
            }
        }

        private bool SessionNameExists(string sessionName)
        {
            var trimmedName = sessionName.Trim();
            return GetExistingSessionNames().Contains(trimmedName);
        }

        private HashSet<string> GetExistingSessionNames()
        {
            return _allSessions
                .Select(session => session.Session?.Name ?? session.Title)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        }

        private static bool TryGetDefaultSessionNameNextIndex(string sessionName, out int nextIndex)
        {
            nextIndex = 2;

            var trimmedName = sessionName.Trim();
            if (string.Equals(trimmedName, GetDefaultSessionName(), StringComparison.CurrentCulture) ||
                string.Equals(trimmedName, LegacyDefaultSessionName, StringComparison.CurrentCulture))
            {
                return true;
            }

            var prefix = $"{GetDefaultSessionName()} ";
            var legacyPrefix = $"{LegacyDefaultSessionName} ";
            if (!trimmedName.StartsWith(prefix, StringComparison.CurrentCulture) &&
                !trimmedName.StartsWith(legacyPrefix, StringComparison.CurrentCulture))
            {
                return false;
            }

            var suffix = trimmedName.StartsWith(prefix, StringComparison.CurrentCulture)
                ? trimmedName[prefix.Length..]
                : trimmedName[legacyPrefix.Length..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex) || parsedIndex < 1)
            {
                return false;
            }

            nextIndex = parsedIndex + 1;
            return true;
        }

        private static string GetDefaultSessionName()
        {
            return L("SessionList.DefaultSessionName", LegacyDefaultSessionName);
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }
    }
}
