using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Skyweaver.Commands;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Services.ChatSession;

namespace Skyweaver.Panels.LiveTasks.ViewModels
{
    public sealed class LiveTaskSessionItem : ObservableObject
    {
        private string _sessionTitle = string.Empty;
        private string _flowName = string.Empty;
        private string _statusText = string.Empty;
        private string _currentNodeTitle = string.Empty;
        private string _currentAgentId = string.Empty;
        private string _modelId = string.Empty;
        private string _userTextPreview = string.Empty;
        private DateTime _updatedAtUtc;

        public LiveTaskSessionItem(ActiveChatSessionExecutionSnapshot snapshot)
        {
            SessionId = snapshot.SessionId;
            StartedAtUtc = snapshot.StartedAtUtc;
            Kind = snapshot.Kind;
            Update(snapshot);
        }

        public string SessionId { get; }

        public DateTime StartedAtUtc { get; }

        public ActiveChatSessionExecutionKind Kind { get; }

        public string SessionTitle
        {
            get => _sessionTitle;
            private set
            {
                if (SetProperty(ref _sessionTitle, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(SessionLineText));
                }
            }
        }

        public string FlowName
        {
            get => _flowName;
            private set
            {
                if (SetProperty(ref _flowName, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(FlowLineText));
                    OnPropertyChanged(nameof(FlowCapsuleText));
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetProperty(ref _statusText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(StatusLineText));
                    OnPropertyChanged(nameof(StatusCapsuleText));
                }
            }
        }

        public string CurrentNodeTitle
        {
            get => _currentNodeTitle;
            private set
            {
                if (SetProperty(ref _currentNodeTitle, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(NodeCapsuleText));
                    OnPropertyChanged(nameof(NodeCapsuleVisibility));
                }
            }
        }

        public string CurrentAgentId
        {
            get => _currentAgentId;
            private set
            {
                if (SetProperty(ref _currentAgentId, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(AgentCapsuleText));
                    OnPropertyChanged(nameof(AgentCapsuleVisibility));
                }
            }
        }

        public string ModelId
        {
            get => _modelId;
            private set
            {
                if (SetProperty(ref _modelId, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(ModelCapsuleText));
                    OnPropertyChanged(nameof(ModelCapsuleVisibility));
                }
            }
        }

        public string UserTextPreview
        {
            get => _userTextPreview;
            private set
            {
                if (SetProperty(ref _userTextPreview, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(UserTextLineText));
                    OnPropertyChanged(nameof(UserTextLineVisibility));
                }
            }
        }

        public DateTime UpdatedAtUtc
        {
            get => _updatedAtUtc;
            private set
            {
                if (SetProperty(ref _updatedAtUtc, value))
                {
                    OnPropertyChanged(nameof(UpdatedCapsuleText));
                }
            }
        }

        public string DisplayName => string.IsNullOrWhiteSpace(FlowName) || FlowName == "未绑定会话流"
            ? SessionTitle
            : FlowName;

        public string SessionLineText => $"会话：{SessionTitle}";

        public string FlowLineText => $"关联会话流：{NormalizeFlowName(FlowName)}";

        public string StatusLineText => $"状态：{StatusText}";

        public string UserTextLineText => $"输入：{UserTextPreview}";

        public Visibility UserTextLineVisibility => string.IsNullOrWhiteSpace(UserTextPreview)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string KindLabel => Kind switch
        {
            ActiveChatSessionExecutionKind.Shell => "Shell",
            ActiveChatSessionExecutionKind.Background => "后台",
            _ => "前台"
        };

        public string KindCapsuleText => $"类型：{KindLabel}";

        public string FlowCapsuleText => $"会话流：{NormalizeFlowName(FlowName)}";

        public string StatusCapsuleText => $"状态：{StatusText}";

        public string StartedCapsuleText => $"开始：{StartedAtUtc.ToLocalTime():HH:mm:ss}";

        public string UpdatedCapsuleText => $"更新：{UpdatedAtUtc.ToLocalTime():HH:mm:ss}";

        public string DurationCapsuleText => $"运行：{FormatDuration(DateTime.UtcNow - StartedAtUtc)}";

        public string NodeCapsuleText => $"节点：{CurrentNodeTitle}";

        public Visibility NodeCapsuleVisibility => string.IsNullOrWhiteSpace(CurrentNodeTitle)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string AgentCapsuleText => $"代理：{CurrentAgentId}";

        public Visibility AgentCapsuleVisibility => string.IsNullOrWhiteSpace(CurrentAgentId)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string ModelCapsuleText => $"模型：{ModelId}";

        public Visibility ModelCapsuleVisibility => string.IsNullOrWhiteSpace(ModelId)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public void Update(ActiveChatSessionExecutionSnapshot snapshot)
        {
            SessionTitle = string.IsNullOrWhiteSpace(snapshot.SessionTitle)
                ? snapshot.SessionId
                : snapshot.SessionTitle.Trim();
            FlowName = snapshot.FlowName?.Trim() ?? string.Empty;
            StatusText = string.IsNullOrWhiteSpace(snapshot.StatusText)
                ? "正在运行"
                : snapshot.StatusText.Trim();
            CurrentNodeTitle = snapshot.CurrentNodeTitle?.Trim() ?? string.Empty;
            CurrentAgentId = snapshot.CurrentAgentId?.Trim() ?? string.Empty;
            ModelId = snapshot.ModelId?.Trim() ?? string.Empty;
            UserTextPreview = snapshot.UserTextPreview?.Trim() ?? string.Empty;
            UpdatedAtUtc = snapshot.UpdatedAtUtc;
            RefreshDuration();
        }

        public void RefreshDuration()
        {
            OnPropertyChanged(nameof(DurationCapsuleText));
        }

        public bool Matches(string query)
        {
            var normalizedQuery = query.Trim();
            if (normalizedQuery.Length == 0)
            {
                return true;
            }

            return Contains(SessionTitle, normalizedQuery) ||
                   Contains(FlowName, normalizedQuery) ||
                   Contains(KindLabel, normalizedQuery) ||
                   Contains(StatusText, normalizedQuery) ||
                   Contains(CurrentNodeTitle, normalizedQuery) ||
                   Contains(CurrentAgentId, normalizedQuery);
        }

        private static bool Contains(string? value, string query)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        private static string NormalizeFlowName(string? flowName)
        {
            return string.IsNullOrWhiteSpace(flowName) ? "未绑定会话流" : flowName.Trim();
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
        }
    }

    public sealed class LiveTasksPanelViewModel : ObservableObject
    {
        private readonly ActiveChatSessionExecutionRegistry _registry;
        private readonly List<LiveTaskSessionItem> _allSessions = new();
        private readonly DispatcherTimer _durationTimer;
        private string _searchQuery = string.Empty;
        private LiveTaskSessionItem? _selectedSession;

        public LiveTasksPanelViewModel()
        {
            _registry = ActiveChatSessionExecutionRegistry.Instance;
            RefreshCommand = new RelayCommand(() => { });
            AbortSessionCommand = new RelayCommand(AbortSelectedSession, () => SelectedSession != null);

            _registry.Changed += Registry_Changed;

            _durationTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _durationTimer.Tick += (_, _) => RefreshSessionDurations();
            _durationTimer.Start();

            RefreshFromRegistry();
        }

        public ObservableCollection<LiveTaskSessionItem> RunningSessions { get; } = new();

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public LiveTaskSessionItem? SelectedSession
        {
            get => _selectedSession;
            set
            {
                if (SetProperty(ref _selectedSession, value))
                {
                    OnPropertyChanged(nameof(IsSessionSelected));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsSessionSelected => SelectedSession != null;

        public Visibility EmptyStateVisibility => RunningSessions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility RunningSessionsVisibility => RunningSessions.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        public ICommand RefreshCommand { get; }

        public ICommand AbortSessionCommand { get; }

        private void Registry_Changed(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                RefreshFromRegistry();
                return;
            }

            dispatcher.BeginInvoke(new Action(RefreshFromRegistry), DispatcherPriority.Background);
        }

        private void RefreshFromRegistry()
        {
            var selectedSessionId = SelectedSession?.SessionId;
            var snapshots = _registry.GetSnapshot();
            var snapshotIds = snapshots
                .Select(snapshot => snapshot.SessionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var index = _allSessions.Count - 1; index >= 0; index--)
            {
                if (!snapshotIds.Contains(_allSessions[index].SessionId))
                {
                    _allSessions.RemoveAt(index);
                }
            }

            foreach (var snapshot in snapshots)
            {
                var item = _allSessions.FirstOrDefault(candidate =>
                    string.Equals(candidate.SessionId, snapshot.SessionId, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    _allSessions.Add(new LiveTaskSessionItem(snapshot));
                    continue;
                }

                item.Update(snapshot);
            }

            _allSessions.Sort((left, right) => right.StartedAtUtc.CompareTo(left.StartedAtUtc));
            ApplyFilter(selectedSessionId);
        }

        private void ApplyFilter(string? preferredSessionId = null)
        {
            var selectedSessionId = preferredSessionId ?? SelectedSession?.SessionId;
            var filtered = _allSessions
                .Where(item => item.Matches(SearchQuery))
                .ToArray();

            RunningSessions.Clear();
            foreach (var item in filtered)
            {
                RunningSessions.Add(item);
            }

            SelectedSession = RunningSessions.FirstOrDefault(item =>
                string.Equals(item.SessionId, selectedSessionId, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(RunningSessionsVisibility));
            CommandManager.InvalidateRequerySuggested();
        }

        private void AbortSelectedSession()
        {
            if (SelectedSession == null)
            {
                return;
            }

            _registry.Cancel(SelectedSession.SessionId);
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshSessionDurations()
        {
            foreach (var session in _allSessions)
            {
                session.RefreshDuration();
            }
        }
    }
}
