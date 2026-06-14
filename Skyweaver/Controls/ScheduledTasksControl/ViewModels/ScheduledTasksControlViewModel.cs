using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Skyweaver.Controls.ScheduledTasksControl.Models;
using Skyweaver.Controls.ScheduledTasksControl.Services;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Windows;

namespace Skyweaver.Controls.ScheduledTasksControl.ViewModels
{
    public sealed class ScheduledTasksControlViewModel : ObservableObject
    {
        private readonly ScheduledTasksRepository _repository;
        private ObservableCollection<ScheduledTask> _tasks;
        private ScheduledTask? _selectedTask;
        private int _currentYear;
        private int _currentMonth;
        private DateTime _selectedDate;
        private ObservableCollection<ScheduledTask> _selectedDateTasks;
        private bool _loadFailed;

        public ScheduledTasksControlViewModel()
        {
            _repository = new ScheduledTasksRepository();
            _tasks = new ObservableCollection<ScheduledTask>();
            _selectedDateTasks = new ObservableCollection<ScheduledTask>();
            
            var today = DateTime.Today;
            _currentYear = today.Year;
            _currentMonth = today.Month;
            _selectedDate = today;

            LoadTasks();
        }

        public ObservableCollection<ScheduledTask> Tasks
        {
            get => _tasks;
            set => SetProperty(ref _tasks, value);
        }

        public ScheduledTask? SelectedTask
        {
            get => _selectedTask;
            set
            {
                if (SetProperty(ref _selectedTask, value))
                {
                    OnPropertyChanged(nameof(IsTaskSelected));
                }
            }
        }

        public bool IsTaskSelected => SelectedTask != null;

        public int CurrentYear
        {
            get => _currentYear;
            set
            {
                if (SetProperty(ref _currentYear, value))
                {
                    OnPropertyChanged(nameof(CurrentMonthYearText));
                    RaiseCalendarRefreshRequired();
                }
            }
        }

        public int CurrentMonth
        {
            get => _currentMonth;
            set
            {
                if (SetProperty(ref _currentMonth, value))
                {
                    OnPropertyChanged(nameof(CurrentMonthYearText));
                    RaiseCalendarRefreshRequired();
                }
            }
        }

        public string CurrentMonthYearText => $"{_currentYear}年{_currentMonth}月";

        public DateTime SelectedDate
        {
            get => _selectedDate;
            set
            {
                if (SetProperty(ref _selectedDate, value))
                {
                    OnPropertyChanged(nameof(SelectedDateText));
                    RefreshSelectedDateTasks();
                }
            }
        }

        public string SelectedDateText => _selectedDate.ToString("yyyy年MM月dd日");

        public ObservableCollection<ScheduledTask> SelectedDateTasks
        {
            get => _selectedDateTasks;
            set
            {
                if (SetProperty(ref _selectedDateTasks, value))
                {
                    OnPropertyChanged(nameof(NoTasksPlaceholderVisibility));
                    OnPropertyChanged(nameof(TasksListVisibility));
                }
            }
        }

        public Visibility NoTasksPlaceholderVisibility => SelectedDateTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TasksListVisibility => SelectedDateTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public event EventHandler? CalendarRefreshRequired;

        public void LoadTasks()
        {
            try
            {
                var loaded = _repository.LoadAll();
                Tasks = new ObservableCollection<ScheduledTask>(loaded);
                _loadFailed = false;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                MessageBox.Show($"加载计划任务失败: {ex.Message}。为了防止覆写或损坏现有任务，保存与编辑功能已被禁用。请重启程序或刷新重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Tasks = new ObservableCollection<ScheduledTask>();
            }
            
            RaiseCalendarRefreshRequired();
            RefreshSelectedDateTasks();
        }

        public void SaveTasks()
        {
            if (_loadFailed)
            {
                MessageBox.Show("由于加载计划任务失败，保存操作已被阻止，防止丢失数据。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _repository.SaveAll(Tasks);
            RaiseCalendarRefreshRequired();
            RefreshSelectedDateTasks();
        }

        public void AddNewTask(Window owner)
        {
            if (_loadFailed)
            {
                MessageBox.Show("由于之前加载计划任务失败，已禁用添加操作以防止数据丢失。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new CreateScheduledTaskDialog
            {
                Owner = owner
            };

            if (dialog.ShowDialog() == true)
            {
                Tasks.Insert(0, dialog.ResultTask);
                SaveTasks();
                SelectedTask = dialog.ResultTask;
            }
        }

        public void EditSelectedTask(Window owner)
        {
            if (_loadFailed)
            {
                MessageBox.Show("由于之前加载计划任务失败，已禁用编辑操作以防止数据丢失。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedTask == null) return;

            var dialog = new CreateScheduledTaskDialog(SelectedTask)
            {
                Owner = owner
            };

            if (dialog.ShowDialog() == true)
            {
                var idx = Tasks.IndexOf(SelectedTask);
                if (idx >= 0)
                {
                    Tasks[idx] = dialog.ResultTask;
                    SaveTasks();
                    SelectedTask = dialog.ResultTask;
                }
            }
        }

        public void DeleteSelectedTask()
        {
            if (_loadFailed)
            {
                MessageBox.Show("由于之前加载计划任务失败，已禁用删除操作以防止数据丢失。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedTask == null) return;

            var res = MessageBox.Show(
                $"确定要删除计划任务「{SelectedTask.Name}」吗？",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                Tasks.Remove(SelectedTask);
                SaveTasks();
                SelectedTask = null;
            }
        }

        public void PrevMonth()
        {
            if (CurrentMonth == 1)
            {
                CurrentMonth = 12;
                CurrentYear--;
            }
            else
            {
                CurrentMonth--;
            }
        }

        public void NextMonth()
        {
            if (CurrentMonth == 12)
            {
                CurrentMonth = 1;
                CurrentYear++;
            }
            else
            {
                CurrentMonth++;
            }
        }

        private void RefreshSelectedDateTasks()
        {
            var active = Tasks.Where(t => t.IsActiveOnDate(SelectedDate)).ToList();
            SelectedDateTasks = new ObservableCollection<ScheduledTask>(active);
        }

        private void RaiseCalendarRefreshRequired()
        {
            CalendarRefreshRequired?.Invoke(this, EventArgs.Empty);
        }
    }
}
