using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Skyweaver.Controls.ScheduledTasksControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Models;
using Skyweaver.Controls.WorkflowEditorControl.Services;

namespace Skyweaver.Windows
{
    public partial class CreateScheduledTaskDialog : Window
    {
        private readonly ScheduledTask? _existingTask;
        private readonly SessionFlowRepository _sessionFlowRepository;

        public ScheduledTask ResultTask { get; private set; } = new();

        public CreateScheduledTaskDialog(ScheduledTask? existingTask = null)
        {
            _existingTask = existingTask;
            _sessionFlowRepository = new SessionFlowRepository(new SessionFlowPathProvider());

            InitializeComponent();
            InitializeComboBoxOptions();

            Loaded += OnLoaded;
        }

        private void InitializeComboBoxOptions()
        {
            // 初始化时间选项
            for (int i = 0; i < 24; i++)
            {
                var val = i.ToString("D2");
                DailyHourComboBox.Items.Add(val);
                WeeklyHourComboBox.Items.Add(val);
                MonthlyHourComboBox.Items.Add(val);
                YearlyHourComboBox.Items.Add(val);
            }
            for (int i = 0; i < 60; i++)
            {
                var val = i.ToString("D2");
                DailyMinuteComboBox.Items.Add(val);
                WeeklyMinuteComboBox.Items.Add(val);
                MonthlyMinuteComboBox.Items.Add(val);
                YearlyMinuteComboBox.Items.Add(val);

                DailySecondComboBox.Items.Add(val);
                WeeklySecondComboBox.Items.Add(val);
                MonthlySecondComboBox.Items.Add(val);
                YearlySecondComboBox.Items.Add(val);
            }

            // 初始化月份 (1-12)
            for (int i = 1; i <= 12; i++)
            {
                YearlyMonthComboBox.Items.Add(i.ToString());
            }

            // 初始化日期 (1-31)
            for (int i = 1; i <= 31; i++)
            {
                YearlyDayComboBox.Items.Add(i.ToString());
                MonthlyDayComboBox.Items.Add(i.ToString());
            }

            // 设默认选中
            DailyHourComboBox.SelectedIndex = 12;
            DailyMinuteComboBox.SelectedIndex = 0;
            DailySecondComboBox.SelectedIndex = 0;

            WeeklyHourComboBox.SelectedIndex = 12;
            WeeklyMinuteComboBox.SelectedIndex = 0;
            WeeklySecondComboBox.SelectedIndex = 0;

            MonthlyHourComboBox.SelectedIndex = 12;
            MonthlyMinuteComboBox.SelectedIndex = 0;
            MonthlySecondComboBox.SelectedIndex = 0;

            YearlyHourComboBox.SelectedIndex = 12;
            YearlyMinuteComboBox.SelectedIndex = 0;
            YearlySecondComboBox.SelectedIndex = 0;

            YearlyMonthComboBox.SelectedIndex = 0;
            YearlyDayComboBox.SelectedIndex = 0;
            MonthlyDayComboBox.SelectedIndex = 0;
            WeeklyDayOfWeekComboBox.SelectedIndex = 0;
        }

        private readonly ObservableCollection<TaskTrigger> _triggers = new();
        private bool _isUpdatingUI;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 加载所有节点图/会话流
            try
            {
                var flows = _sessionFlowRepository.LoadAll();
                SessionFlowComboBox.ItemsSource = flows;
                if (flows.Count > 0)
                {
                    SessionFlowComboBox.SelectedIndex = 0;
                }
            }
            catch
            {
                MessageBox.Show(this, "加载会话流失败，请检查会话流配置。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            TriggersListBox.ItemsSource = _triggers;

            // 如果有已有任务，则加载到UI上
            if (_existingTask != null)
            {
                LoadFromTask(_existingTask);
            }
            else
            {
                _triggers.Add(new TaskTrigger { Type = TriggerType.Daily });
                PreActionTypeComboBox.SelectedIndex = 0;
                PostActionTypeComboBox.SelectedIndex = 0;
                AutoApproveToolsCheckBox.IsChecked = false;
            }

            if (_triggers.Count > 0)
            {
                TriggersListBox.SelectedIndex = 0;
            }

            TaskNameTextBox.Focus();
        }

        private void LoadFromTask(ScheduledTask task)
        {
            TaskNameTextBox.Text = task.Name;
            PromptTextBox.Text = task.Prompt;

            if (!string.IsNullOrEmpty(task.SessionFlowPath))
            {
                SessionFlowComboBox.SelectedValue = task.SessionFlowPath;
            }

            foreach (var t in task.Triggers)
            {
                _triggers.Add(new TaskTrigger
                {
                    Type = t.Type,
                    Month = t.Month,
                    Day = t.Day,
                    DayOfWeek = t.DayOfWeek,
                    TimeOfDay = t.TimeOfDay
                });
            }

            AutoApproveToolsCheckBox.IsChecked = task.AutoApproveTools;

            // 动作
            SetActionUI(PreActionTypeComboBox, PreActionScriptTextBox, task.PreAction);
            SetActionUI(PostActionTypeComboBox, PostActionScriptTextBox, task.PostAction);
        }

        private void SetTimeComboBoxes(ComboBox h, ComboBox m, ComboBox s, TimeSpan time)
        {
            h.SelectedItem = time.Hours.ToString("D2");
            m.SelectedItem = time.Minutes.ToString("D2");
            s.SelectedItem = time.Seconds.ToString("D2");
        }

        private void SetActionUI(ComboBox typeCb, TextBox scriptTb, TaskAction action)
        {
            switch (action.Type)
            {
                case ActionType.None:
                    typeCb.SelectedIndex = 0;
                    break;
                case ActionType.Powershell:
                    typeCb.SelectedIndex = 1;
                    scriptTb.Text = action.Script;
                    break;
                case ActionType.Shutdown:
                    typeCb.SelectedIndex = 2;
                    break;
                case ActionType.Restart:
                    typeCb.SelectedIndex = 3;
                    break;
            }
        }

        private void LoadFromTrigger(TaskTrigger trigger)
        {
            _isUpdatingUI = true;
            try
            {
                switch (trigger.Type)
                {
                    case TriggerType.Daily:
                        TriggerTypeComboBox.SelectedIndex = 0;
                        SetTimeComboBoxes(DailyHourComboBox, DailyMinuteComboBox, DailySecondComboBox, trigger.TimeOfDay);
                        break;
                    case TriggerType.Weekly:
                        TriggerTypeComboBox.SelectedIndex = 1;
                        SetTimeComboBoxes(WeeklyHourComboBox, WeeklyMinuteComboBox, WeeklySecondComboBox, trigger.TimeOfDay);
                        WeeklyDayOfWeekComboBox.SelectedValue = trigger.DayOfWeek.ToString();
                        break;
                    case TriggerType.Monthly:
                        TriggerTypeComboBox.SelectedIndex = 2;
                        SetTimeComboBoxes(MonthlyHourComboBox, MonthlyMinuteComboBox, MonthlySecondComboBox, trigger.TimeOfDay);
                        MonthlyDayComboBox.SelectedItem = trigger.Day.ToString();
                        break;
                    case TriggerType.Yearly:
                        TriggerTypeComboBox.SelectedIndex = 3;
                        SetTimeComboBoxes(YearlyHourComboBox, YearlyMinuteComboBox, YearlySecondComboBox, trigger.TimeOfDay);
                        YearlyMonthComboBox.SelectedItem = trigger.Month.ToString();
                        YearlyDayComboBox.SelectedItem = trigger.Day.ToString();
                        break;
                    case TriggerType.Custom:
                        TriggerTypeComboBox.SelectedIndex = 4;
                        break;
                }
                UpdateTriggerParamsVisibility(trigger.Type);
            }
            finally
            {
                _isUpdatingUI = false;
            }
        }

        private void UpdateTriggerParamsVisibility(TriggerType type)
        {
            if (YearlyParamsGrid == null || MonthlyParamsGrid == null || WeeklyParamsGrid == null || DailyParamsGrid == null || CustomParamsGrid == null)
            {
                return;
            }

            YearlyParamsGrid.Visibility = Visibility.Collapsed;
            MonthlyParamsGrid.Visibility = Visibility.Collapsed;
            WeeklyParamsGrid.Visibility = Visibility.Collapsed;
            DailyParamsGrid.Visibility = Visibility.Collapsed;
            CustomParamsGrid.Visibility = Visibility.Collapsed;

            switch (type)
            {
                case TriggerType.Daily:
                    DailyParamsGrid.Visibility = Visibility.Visible;
                    break;
                case TriggerType.Weekly:
                    WeeklyParamsGrid.Visibility = Visibility.Visible;
                    break;
                case TriggerType.Monthly:
                    MonthlyParamsGrid.Visibility = Visibility.Visible;
                    break;
                case TriggerType.Yearly:
                    YearlyParamsGrid.Visibility = Visibility.Visible;
                    break;
                case TriggerType.Custom:
                    CustomParamsGrid.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void TriggerTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearlyParamsGrid == null || MonthlyParamsGrid == null || WeeklyParamsGrid == null || DailyParamsGrid == null || CustomParamsGrid == null)
            {
                return;
            }

            if (!_isUpdatingUI)
            {
                SaveCurrentTriggerParams();
            }

            if (TriggerTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var tag = selectedItem.Tag?.ToString();
                var type = Enum.TryParse<TriggerType>(tag, true, out var parsed) ? parsed : TriggerType.Daily;
                UpdateTriggerParamsVisibility(type);
            }
        }

        private void TriggersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TriggersListBox.SelectedItem is TaskTrigger trigger)
            {
                TriggerDetailGrid.IsEnabled = true;
                LoadFromTrigger(trigger);
            }
            else
            {
                TriggerDetailGrid.IsEnabled = false;
            }
        }

        private void AddTriggerButton_Click(object sender, RoutedEventArgs e)
        {
            var newTrigger = new TaskTrigger { Type = TriggerType.Daily };
            _triggers.Add(newTrigger);
            TriggersListBox.SelectedItem = newTrigger;
        }

        private void DeleteTriggerButton_Click(object sender, RoutedEventArgs e)
        {
            if (TriggersListBox.SelectedItem is TaskTrigger trigger)
            {
                _triggers.Remove(trigger);
                if (_triggers.Count > 0)
                {
                    TriggersListBox.SelectedIndex = 0;
                }
            }
        }

        private void SaveCurrentTriggerParams()
        {
            if (_isUpdatingUI) return;
            if (TriggersListBox.SelectedItem is not TaskTrigger selectedTrigger) return;

            if (TriggerTypeComboBox.SelectedItem is ComboBoxItem typeItem)
            {
                var typeTag = typeItem.Tag?.ToString();
                selectedTrigger.Type = Enum.TryParse<TriggerType>(typeTag, true, out var parsedType) ? parsedType : TriggerType.Daily;

                switch (selectedTrigger.Type)
                {
                    case TriggerType.Daily:
                        selectedTrigger.TimeOfDay = GetTimeSpan(DailyHourComboBox, DailyMinuteComboBox, DailySecondComboBox);
                        break;
                    case TriggerType.Weekly:
                        selectedTrigger.TimeOfDay = GetTimeSpan(WeeklyHourComboBox, WeeklyMinuteComboBox, WeeklySecondComboBox);
                        if (WeeklyDayOfWeekComboBox.SelectedItem is ComboBoxItem dowItem)
                        {
                            selectedTrigger.DayOfWeek = Enum.TryParse<DayOfWeek>(dowItem.Tag?.ToString(), true, out var parsedDow) ? parsedDow : DayOfWeek.Monday;
                        }
                        break;
                    case TriggerType.Monthly:
                        selectedTrigger.TimeOfDay = GetTimeSpan(MonthlyHourComboBox, MonthlyMinuteComboBox, MonthlySecondComboBox);
                        selectedTrigger.Day = int.TryParse(MonthlyDayComboBox.SelectedItem?.ToString(), out var mDay) ? mDay : 1;
                        break;
                    case TriggerType.Yearly:
                        selectedTrigger.TimeOfDay = GetTimeSpan(YearlyHourComboBox, YearlyMinuteComboBox, YearlySecondComboBox);
                        selectedTrigger.Month = int.TryParse(YearlyMonthComboBox.SelectedItem?.ToString(), out var yMonth) ? yMonth : 1;
                        selectedTrigger.Day = int.TryParse(YearlyDayComboBox.SelectedItem?.ToString(), out var yDay) ? yDay : 1;
                        break;
                    case TriggerType.Custom:
                        break;
                }

                TriggersListBox.Items.Refresh();
            }
        }

        private void DailyParam_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrentTriggerParams();
        private void WeeklyParam_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrentTriggerParams();
        private void MonthlyParam_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrentTriggerParams();
        private void YearlyParam_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrentTriggerParams();



        private void PreActionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PreActionScriptPanel == null) return;

            if (PreActionTypeComboBox.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "Powershell")
            {
                PreActionScriptPanel.Visibility = Visibility.Visible;
            }
            else
            {
                PreActionScriptPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void PostActionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PostActionScriptPanel == null) return;

            if (PostActionTypeComboBox.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "Powershell")
            {
                PostActionScriptPanel.Visibility = Visibility.Visible;
            }
            else
            {
                PostActionScriptPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            var taskName = TaskNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(taskName))
            {
                MessageBox.Show(this, "请输入计划任务名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SessionFlowComboBox.SelectedValue == null)
            {
                MessageBox.Show(this, "请选择关联的会话流。如果没有会话流，请先在会话流编辑器中创建。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var prompt = PromptTextBox.Text.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                MessageBox.Show(this, "请输入任务提示词内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存到 ResultTask
            ResultTask = new ScheduledTask
            {
                Id = _existingTask?.Id ?? Guid.NewGuid().ToString("N"),
                Name = taskName,
                SessionFlowPath = SessionFlowComboBox.SelectedValue?.ToString() ?? string.Empty,
                SessionFlowName = SessionFlowComboBox.Text ?? string.Empty,
                Prompt = prompt,
                CreatedAt = _existingTask?.CreatedAt ?? DateTime.UtcNow,
                LastRunTime = _existingTask?.LastRunTime ?? DateTime.Now,
                AutoApproveTools = AutoApproveToolsCheckBox.IsChecked == true
            };

            // 获取 SessionFlow 的 DisplayName
            if (SessionFlowComboBox.SelectedItem is SessionFlowGraphDocumentModel doc)
            {
                ResultTask.SessionFlowName = doc.Name;
            }

            // 保存多触发器
            ResultTask.Triggers.AddRange(_triggers);

            // 保存 PreAction
            if (PreActionTypeComboBox.SelectedItem is ComboBoxItem preItem)
            {
                ResultTask.PreAction.Type = Enum.TryParse<ActionType>(preItem.Tag?.ToString(), true, out var preType) ? preType : ActionType.None;
                ResultTask.PreAction.Script = (PreActionScriptTextBox.Text ?? string.Empty).Trim();
            }

            // 保存 PostAction
            if (PostActionTypeComboBox.SelectedItem is ComboBoxItem postItem)
            {
                ResultTask.PostAction.Type = Enum.TryParse<ActionType>(postItem.Tag?.ToString(), true, out var postType) ? postType : ActionType.None;
                ResultTask.PostAction.Script = (PostActionScriptTextBox.Text ?? string.Empty).Trim();
            }

            DialogResult = true;
            Close();
        }

        private TimeSpan GetTimeSpan(ComboBox h, ComboBox m, ComboBox s)
        {
            int.TryParse(h.SelectedItem?.ToString(), out int hours);
            int.TryParse(m.SelectedItem?.ToString(), out int minutes);
            int.TryParse(s.SelectedItem?.ToString(), out int seconds);
            return new TimeSpan(hours, minutes, seconds);
        }
    }
}
