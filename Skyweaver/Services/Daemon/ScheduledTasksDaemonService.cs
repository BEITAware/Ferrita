using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Skyweaver.Controls.ScheduledTasksControl.Models;
using Skyweaver.Controls.ScheduledTasksControl.Services;
using Skyweaver.Controls.LanguageModelConfigurationControl.Services;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.ChatSession;
using Skyweaver.Services.Notifications;
using Skyweaver.Services.AgentLoop;

namespace Skyweaver.Services.Daemon
{
    public sealed class ScheduledTasksDaemonService : IDisposable
    {
        private static readonly Lazy<ScheduledTasksDaemonService> LazyInstance =
            new(() => new ScheduledTasksDaemonService());

        public static ScheduledTasksDaemonService Instance => LazyInstance.Value;

        private readonly ScheduledTasksRepository _repository = new();
        private readonly ChatSessionRepository _sessionRepository = new();
        private readonly ChatSessionRuntimeService _runtimeService = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly object _gate = new();
        private Task? _workerTask;
        private bool _isDisposed;
        private readonly HashSet<string> _runningTaskIds = new(StringComparer.OrdinalIgnoreCase);

        private ScheduledTasksDaemonService()
        {
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_workerTask == null)
                {
                    _workerTask = Task.Run(() => CheckLoopAsync(_cancellationTokenSource.Token));
                }
            }
        }

        private async Task CheckLoopAsync(CancellationToken cancellationToken)
        {
            // 等待一段时间让应用完全启动
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CheckTasksAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略后台异常，防止应用崩溃
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private Task CheckTasksAsync(CancellationToken cancellationToken)
        {
            var tasks = _repository.LoadAll();
            var now = DateTime.Now;
            bool anySaved = false;

            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.SessionFlowPath))
                {
                    continue;
                }

                // 检查是否有任何触发器满足条件
                DateTime? mostRecentDue = null;
                foreach (var trigger in task.Triggers)
                {
                    DateTime occurrence = GetMostRecentOccurrence(trigger, now);
                    if (occurrence > (task.LastRunTime ?? DateTime.MinValue))
                    {
                        if (mostRecentDue == null || occurrence > mostRecentDue.Value)
                        {
                            mostRecentDue = occurrence;
                        }
                    }
                }

                if (mostRecentDue != null)
                {
                    lock (_gate)
                    {
                        if (_runningTaskIds.Contains(task.Id))
                        {
                            continue;
                        }
                        _runningTaskIds.Add(task.Id);
                    }

                    // 立即更新 LastRunTime 并保存，防止在长任务执行期间被重复触发
                    task.LastRunTime = mostRecentDue.Value;
                    anySaved = true;

                    // 在后台执行会话流
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var binding = new ChatSessionFlowBinding
                            {
                                FilePath = task.SessionFlowPath,
                                GraphName = task.SessionFlowName
                            };
                            var sessionName = $"{task.Name}_{now:yyyyMMdd_HHmmss}";
                            var session = _sessionRepository.Create(sessionName, binding);
                            session.IsScheduledTaskSession = true;
                            _sessionRepository.Save(session);

                            var request = new ChatSessionRuntimeRequest
                            {
                                Session = session,
                                UserText = task.Prompt,
                                UserContentBlocks = Array.Empty<LanguageModelChatContentBlock>(),
                                ToolConfirmationCallback = task.AutoApproveTools
                                    ? (confirmationRequest, ct) => Task.FromResult(AgentToolConfirmationResult.Approve())
                                    : null
                            };

                            await _runtimeService.ExecuteTurnAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

                            NotificationService.Instance.ShowTransient(string.Format("计划任务 {0} 已完成！", task.Name));
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to run scheduled task: {ex.Message}");
                        }
                        finally
                        {
                            lock (_gate)
                            {
                                _runningTaskIds.Remove(task.Id);
                            }
                        }
                    }, cancellationToken);
                }
            }

            if (anySaved)
            {
                _repository.SaveAll(tasks);
            }
            return Task.CompletedTask;
        }

        public static DateTime GetMostRecentOccurrence(TaskTrigger t, DateTime now)
        {
            DateTime today = now.Date;
            switch (t.Type)
            {
                case TriggerType.Daily:
                    {
                        DateTime occurrence = today.Add(t.TimeOfDay);
                        if (now >= occurrence)
                        {
                            return occurrence;
                        }
                        else
                        {
                            return today.AddDays(-1).Add(t.TimeOfDay);
                        }
                    }
                case TriggerType.Weekly:
                    {
                        int diff = (7 + (today.DayOfWeek - t.DayOfWeek)) % 7;
                        DateTime candidate = today.AddDays(-diff);
                        DateTime occurrence = candidate.Add(t.TimeOfDay);
                        if (now >= occurrence)
                        {
                            return occurrence;
                        }
                        else
                        {
                            return candidate.AddDays(-7).Add(t.TimeOfDay);
                        }
                    }
                case TriggerType.Monthly:
                    {
                        DateTime occurrence;
                        try
                        {
                            int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
                            int targetDay = Math.Min(t.Day, daysInMonth);
                            occurrence = new DateTime(today.Year, today.Month, targetDay).Add(t.TimeOfDay);
                        }
                        catch
                        {
                            occurrence = today.Add(t.TimeOfDay);
                        }

                        if (now >= occurrence)
                        {
                            return occurrence;
                        }
                        else
                        {
                            DateTime prevMonth = today.AddMonths(-1);
                            int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
                            int targetDay = Math.Min(t.Day, daysInPrevMonth);
                            return new DateTime(prevMonth.Year, prevMonth.Month, targetDay).Add(t.TimeOfDay);
                        }
                    }
                case TriggerType.Yearly:
                    {
                        DateTime occurrence;
                        try
                        {
                            int daysInMonth = DateTime.DaysInMonth(today.Year, t.Month);
                            int targetDay = Math.Min(t.Day, daysInMonth);
                            occurrence = new DateTime(today.Year, t.Month, targetDay).Add(t.TimeOfDay);
                        }
                        catch
                        {
                            occurrence = today.Add(t.TimeOfDay);
                        }

                        if (now >= occurrence)
                        {
                            return occurrence;
                        }
                        else
                        {
                            DateTime prevYear = today.AddYears(-1);
                            int daysInMonth = DateTime.DaysInMonth(prevYear.Year, t.Month);
                            int targetDay = Math.Min(t.Day, daysInMonth);
                            return new DateTime(prevYear.Year, t.Month, targetDay).Add(t.TimeOfDay);
                        }
                    }
                default:
                    return DateTime.MinValue;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            try
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
            catch
            {
            }
        }
    }
}
