using System;
using System.Collections.Generic;
using System.Linq;

namespace Skyweaver.Controls.ScheduledTasksControl.Models
{
    public enum TriggerType
    {
        Yearly,
        Monthly,
        Weekly,
        Daily,
        Custom
    }

    public enum ActionType
    {
        None,
        Powershell,
        Shutdown,
        Restart
    }



    public sealed class TaskTrigger
    {
        public TriggerType Type { get; set; } = TriggerType.Daily;
        public int Month { get; set; } = 1;
        public int Day { get; set; } = 1;
        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
        public TimeSpan TimeOfDay { get; set; } = TimeSpan.FromHours(12);

        public string DisplayText
        {
            get
            {
                return Type switch
                {
                    TriggerType.Yearly => $"每年 {Month}月{Day}日 {TimeOfDay:hh\\:mm\\:ss}",
                    TriggerType.Monthly => $"每月 {Day}日 {TimeOfDay:hh\\:mm\\:ss}",
                    TriggerType.Weekly => $"每周 {GetChineseDayOfWeek(DayOfWeek)} {TimeOfDay:hh\\:mm\\:ss}",
                    TriggerType.Daily => $"每天 {TimeOfDay:hh\\:mm\\:ss}",
                    TriggerType.Custom => "自定义触发器",
                    _ => "未知"
                };
            }
        }

        private static string GetChineseDayOfWeek(DayOfWeek dayOfWeek)
        {
            return dayOfWeek switch
            {
                DayOfWeek.Sunday => "星期日",
                DayOfWeek.Monday => "星期一",
                DayOfWeek.Tuesday => "星期二",
                DayOfWeek.Wednesday => "星期三",
                DayOfWeek.Thursday => "星期四",
                DayOfWeek.Friday => "星期五",
                DayOfWeek.Saturday => "星期六",
                _ => dayOfWeek.ToString()
            };
        }
    }

    public sealed class TaskAction
    {
        public ActionType Type { get; set; } = ActionType.None;
        private string? _script;
        public string Script
        {
            get => _script ?? string.Empty;
            set => _script = value;
        }

        public string DisplayText
        {
            get
            {
                return Type switch
                {
                    ActionType.None => "无操作",
                    ActionType.Powershell => $"执行 Powershell: {Script}",
                    ActionType.Shutdown => "系统关机",
                    ActionType.Restart => "系统重启",
                    _ => "未知"
                };
            }
        }
    }

    public sealed class ScheduledTask
    {
        private string? _id;
        public string Id
        {
            get => _id ??= Guid.NewGuid().ToString("N");
            set => _id = value;
        }

        private string? _name;
        public string Name
        {
            get => _name ?? string.Empty;
            set => _name = value;
        }

        private string? _sessionFlowPath;
        public string SessionFlowPath
        {
            get => _sessionFlowPath ?? string.Empty;
            set => _sessionFlowPath = value;
        }

        private string? _sessionFlowName;
        public string SessionFlowName
        {
            get => _sessionFlowName ?? string.Empty;
            set => _sessionFlowName = value;
        }

        private string? _prompt;
        public string Prompt
        {
            get => _prompt ?? string.Empty;
            set => _prompt = value;
        }
        
        private List<TaskTrigger>? _triggers;
        public List<TaskTrigger> Triggers
        {
            get => _triggers ??= new List<TaskTrigger>();
            set => _triggers = value;
        }

        public TaskTrigger Trigger
        {
            get => Triggers.FirstOrDefault() ?? new TaskTrigger();
            set
            {
                if (value != null)
                {
                    if (Triggers.Count == 0) Triggers.Add(value);
                    else Triggers[0] = value;
                }
            }
        }

        public string TriggersDisplayText => Triggers == null ? string.Empty : string.Join(" | ", Triggers.Where(t => t != null).Select(t => t.DisplayText ?? string.Empty));

        private TaskAction? _preAction;
        public TaskAction PreAction
        {
            get => _preAction ??= new TaskAction();
            set => _preAction = value;
        }

        private TaskAction? _postAction;
        public TaskAction PostAction
        {
            get => _postAction ??= new TaskAction();
            set => _postAction = value;
        }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastRunTime { get; set; }

        public bool AutoApproveTools { get; set; }

        public bool IsActiveOnDate(DateTime date)
        {
            if (Triggers == null || Triggers.Count == 0) return false;
            return Triggers.Where(t => t != null).Any(t => t.Type switch
            {
                TriggerType.Daily => true,
                TriggerType.Weekly => date.DayOfWeek == t.DayOfWeek,
                TriggerType.Monthly => date.Day == t.Day,
                TriggerType.Yearly => date.Month == t.Month && date.Day == t.Day,
                TriggerType.Custom => false,
                _ => false
            });
        }
    }
}
