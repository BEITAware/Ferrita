using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Skyweaver.Controls.ScheduledTasksControl.Models;
using Skyweaver.Services.Directories;

namespace Skyweaver.Controls.ScheduledTasksControl.Services
{
    public sealed class ScheduledTasksRepository
    {
        private static readonly object _syncRoot = new();

        public string ConfigurationDirectoryPath => SkyweaverDirectoryRuntime.Instance.ConfigurationDirectoryPath;

        public string ConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "ScheduledTasks.xml");

        public IReadOnlyList<ScheduledTask> LoadAll()
        {
            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                if (!File.Exists(ConfigurationFilePath))
                {
                    return Array.Empty<ScheduledTask>();
                }

                const int maxRetries = 5;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        var document = XDocument.Load(ConfigurationFilePath);
                        var root = document.Root;
                        if (root == null)
                        {
                            return Array.Empty<ScheduledTask>();
                        }

                        var tasks = new List<ScheduledTask>();
                        foreach (var taskElement in root.Elements("Task"))
                        {
                            try
                            {
                                tasks.Add(ParseTask(taskElement));
                            }
                            catch
                            {
                                // 忽略单个损坏的任务项
                            }
                        }

                        return tasks.OrderByDescending(t => t.CreatedAt).ToList();
                    }
                    catch (Exception ex) when (ex is IOException || ex is System.Xml.XmlException)
                    {
                        if (i == maxRetries - 1)
                        {
                            throw; // 达到最大重试次数，将异常抛出，防止默默返回空数据导致文件被清空
                        }
                        System.Threading.Thread.Sleep(50);
                    }
                }

                return Array.Empty<ScheduledTask>();
            }
        }

        public void SaveAll(IEnumerable<ScheduledTask> tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks);

            lock (_syncRoot)
            {
                EnsureConfigurationDirectory();

                var document = new XDocument(
                    new XElement("ScheduledTasks",
                        tasks.Select(CreateTaskElement)));

                string tempFilePath = ConfigurationFilePath + ".tmp";
                try
                {
                    document.Save(tempFilePath);
                    
                    if (File.Exists(ConfigurationFilePath))
                    {
                        File.Replace(tempFilePath, ConfigurationFilePath, null, true);
                    }
                    else
                    {
                        File.Move(tempFilePath, ConfigurationFilePath);
                    }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                    }
                    catch
                    {
                        // 吞掉清理临时文件失败的异常
                    }
                    throw;
                }
            }
        }

        private void EnsureConfigurationDirectory()
        {
            Directory.CreateDirectory(ConfigurationDirectoryPath);
        }

        private static ScheduledTask ParseTask(XElement element)
        {
            var task = new ScheduledTask
            {
                Id = ((string?)element.Attribute("Id") ?? Guid.NewGuid().ToString("N")).Trim(),
                Name = ((string?)element.Attribute("Name") ?? string.Empty).Trim(),
                SessionFlowPath = ((string?)element.Attribute("SessionFlowPath") ?? string.Empty).Trim(),
                SessionFlowName = ((string?)element.Attribute("SessionFlowName") ?? string.Empty).Trim(),
                CreatedAt = ParseDateTime((string?)element.Attribute("CreatedAt"), DateTime.UtcNow),
                LastRunTime = ParseNullableDateTime((string?)element.Attribute("LastRunTime")),
                AutoApproveTools = ParseBool((string?)element.Attribute("AutoApproveTools"), false),
                Prompt = (string?)element.Element("Prompt") ?? string.Empty
            };

            var triggersElement = element.Element("Triggers");
            if (triggersElement != null)
            {
                foreach (var triggerEl in triggersElement.Elements("Trigger"))
                {
                    task.Triggers.Add(new TaskTrigger
                    {
                        Type = ParseEnum((string?)triggerEl.Attribute("Type"), TriggerType.Daily),
                        Month = ParseInt((string?)triggerEl.Attribute("Month"), 1),
                        Day = ParseInt((string?)triggerEl.Attribute("Day"), 1),
                        DayOfWeek = ParseEnum((string?)triggerEl.Attribute("DayOfWeek"), DayOfWeek.Monday),
                        TimeOfDay = ParseTimeSpan((string?)triggerEl.Attribute("TimeOfDay"), TimeSpan.FromHours(12))
                    });
                }
            }
            else
            {
                var triggerElement = element.Element("Trigger");
                if (triggerElement != null)
                {
                    task.Triggers.Add(new TaskTrigger
                    {
                        Type = ParseEnum((string?)triggerElement.Attribute("Type"), TriggerType.Daily),
                        Month = ParseInt((string?)triggerElement.Attribute("Month"), 1),
                        Day = ParseInt((string?)triggerElement.Attribute("Day"), 1),
                        DayOfWeek = ParseEnum((string?)triggerElement.Attribute("DayOfWeek"), DayOfWeek.Monday),
                        TimeOfDay = ParseTimeSpan((string?)triggerElement.Attribute("TimeOfDay"), TimeSpan.FromHours(12))
                    });
                }
            }



            var preActionElement = element.Element("PreAction");
            if (preActionElement != null)
            {
                task.PreAction = new TaskAction
                {
                    Type = ParseEnum((string?)preActionElement.Attribute("Type"), ActionType.None),
                    Script = (string?)preActionElement.Attribute("Script") ?? string.Empty
                };
            }

            var postActionElement = element.Element("PostAction");
            if (postActionElement != null)
            {
                task.PostAction = new TaskAction
                {
                    Type = ParseEnum((string?)postActionElement.Attribute("Type"), ActionType.None),
                    Script = (string?)postActionElement.Attribute("Script") ?? string.Empty
                };
            }

            return task;
        }

        private static XElement CreateTaskElement(ScheduledTask task)
        {
            return new XElement("Task",
                new XAttribute("Id", task.Id),
                new XAttribute("Name", task.Name),
                new XAttribute("SessionFlowPath", task.SessionFlowPath),
                new XAttribute("SessionFlowName", task.SessionFlowName),
                new XAttribute("CreatedAt", task.CreatedAt.ToString("o", CultureInfo.InvariantCulture)),
                task.LastRunTime.HasValue ? new XAttribute("LastRunTime", task.LastRunTime.Value.ToString("o", CultureInfo.InvariantCulture)) : null,
                new XAttribute("AutoApproveTools", task.AutoApproveTools),
                new XElement("Prompt", task.Prompt),
                new XElement("Triggers",
                    task.Triggers.Select(t => new XElement("Trigger",
                        new XAttribute("Type", t.Type),
                        new XAttribute("Month", t.Month),
                        new XAttribute("Day", t.Day),
                        new XAttribute("DayOfWeek", t.DayOfWeek),
                        new XAttribute("TimeOfDay", t.TimeOfDay.ToString("c"))))),
                new XElement("PreAction",
                    new XAttribute("Type", task.PreAction.Type),
                    new XAttribute("Script", task.PreAction.Script)),
                new XElement("PostAction",
                    new XAttribute("Type", task.PostAction.Type),
                    new XAttribute("Script", task.PostAction.Script)));
        }

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static TimeSpan ParseTimeSpan(string? value, TimeSpan fallback)
        {
            return TimeSpan.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static DateTime ParseDateTime(string? value, DateTime fallback)
        {
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : fallback;
        }

        private static DateTime? ParseNullableDateTime(string? value)
        {
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        }

        private static bool ParseBool(string? value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
