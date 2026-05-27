using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Skyweaver.Controls.ScheduledTasksControl.ViewModels;

namespace Skyweaver.Controls.ScheduledTasksControl.Views
{
    public partial class ScheduledTasksControl : UserControl
    {
        private readonly ScheduledTasksControlViewModel _viewModel;

        public ScheduledTasksControl()
        {
            InitializeComponent();

            _viewModel = new ScheduledTasksControlViewModel();
            DataContext = _viewModel;

            _viewModel.CalendarRefreshRequired += OnCalendarRefreshRequired;

            RenderCalendar();
        }

        private void OnCalendarRefreshRequired(object? sender, EventArgs e)
        {
            RenderCalendar();
        }

        private void RenderCalendar()
        {
            if (CalendarDayGrid == null) return;

            CalendarDayGrid.Children.Clear();

            int year = _viewModel.CurrentYear;
            int month = _viewModel.CurrentMonth;

            // 获取当月第一天是星期几
            var firstDayOfMonth = new DateTime(year, month, 1);
            int firstDayOfWeek = (int)firstDayOfMonth.DayOfWeek;

            // 获取当月天数
            int daysInMonth = DateTime.DaysInMonth(year, month);

            // 获取上月天数
            int prevYear = month == 1 ? year - 1 : year;
            int prevMonth = month == 1 ? 12 : month - 1;
            int daysInPrevMonth = DateTime.DaysInMonth(prevYear, prevMonth);

            for (int i = 0; i < 42; i++)
            {
                int row = i / 7;
                int col = i % 7;

                DateTime gridDate;
                bool isCurrentMonth = false;

                if (i < firstDayOfWeek)
                {
                    int day = daysInPrevMonth - (firstDayOfWeek - 1 - i);
                    gridDate = new DateTime(prevYear, prevMonth, day);
                }
                else if (i < firstDayOfWeek + daysInMonth)
                {
                    int day = i - firstDayOfWeek + 1;
                    gridDate = new DateTime(year, month, day);
                    isCurrentMonth = true;
                }
                else
                {
                    int day = i - (firstDayOfWeek + daysInMonth) + 1;
                    int nextYear = month == 12 ? year + 1 : year;
                    int nextMonth = month == 12 ? 1 : month + 1;
                    gridDate = new DateTime(nextYear, nextMonth, day);
                }

                var border = new Border
                {
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(2),
                    Width = 38,
                    Height = 38,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    Tag = gridDate
                };

                border.MouseLeftButtonDown += (s, e) =>
                {
                    if (s is Border b && b.Tag is DateTime dt)
                    {
                        _viewModel.SelectedDate = dt;
                        HighlightSelectedDate();
                    }
                };

                var textBlock = new TextBlock
                {
                    Text = gridDate.Day.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                };

                textBlock.Foreground = isCurrentMonth
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

                var cellGrid = new Grid();

                // 检查这天有几个计划任务被激活
                int activeTasksCount = _viewModel.Tasks.Count(t => t.IsActiveOnDate(gridDate));
                if (activeTasksCount > 0)
                {
                    string imagePath = activeTasksCount switch
                    {
                        1 => "pack://application:,,,/Skyweaver;component/Resources/Scheduled1.png",
                        2 => "pack://application:,,,/Skyweaver;component/Resources/Scheduled2.png",
                        3 => "pack://application:,,,/Skyweaver;component/Resources/Scheduled3.png",
                        _ => "pack://application:,,,/Skyweaver;component/Resources/ScheduledMany.png"
                    };

                    double opacity = activeTasksCount >= 4 ? 0.5 : 0.25;

                    var scheduledImage = new Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(imagePath, UriKind.Absolute)),
                        Opacity = opacity,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false
                    };
                    cellGrid.Children.Add(scheduledImage);
                }

                cellGrid.Children.Add(textBlock);

                border.Child = cellGrid;

                Grid.SetRow(border, row);
                Grid.SetColumn(border, col);
                CalendarDayGrid.Children.Add(border);
            }

            HighlightSelectedDate();
        }

        private void HighlightSelectedDate()
        {
            if (CalendarDayGrid == null) return;

            DateTime selected = _viewModel.SelectedDate;

            foreach (UIElement child in CalendarDayGrid.Children)
            {
                if (child is Border border && border.Tag is DateTime gridDate)
                {
                    bool isSelected = gridDate.Date == selected.Date;

                    if (isSelected)
                    {
                        border.Background = (Brush)FindResource("SelectedDateBGPaint");
                        border.BorderBrush = null;
                        border.BorderThickness = new Thickness(0);
                        border.Effect = null;
                    }
                    else
                    {
                        border.Background = (Brush)FindResource("IdleDateBGPaint");
                        border.BorderBrush = null;
                        border.BorderThickness = new Thickness(0);
                        border.Effect = null;
                    }

                    border.MouseEnter -= OnBorderMouseEnter;
                    border.MouseEnter += OnBorderMouseEnter;
                    border.MouseLeave -= OnBorderMouseLeave;
                    border.MouseLeave += OnBorderMouseLeave;
                }
            }
        }

        private void OnBorderMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border && border.Tag is DateTime gridDate)
            {
                bool isSelected = gridDate.Date == _viewModel.SelectedDate.Date;
                if (!isSelected)
                {
                    border.Background = (Brush)FindResource("IdleDateBGPaint");
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
                    border.BorderThickness = new Thickness(1);
                    border.Effect = null;
                }
            }
        }

        private void OnBorderMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border && border.Tag is DateTime gridDate)
            {
                bool isSelected = gridDate.Date == _viewModel.SelectedDate.Date;
                if (!isSelected)
                {
                    border.Background = (Brush)FindResource("IdleDateBGPaint");
                    border.BorderBrush = null;
                    border.BorderThickness = new Thickness(0);
                    border.Effect = null;
                }
            }
        }

        private void NewTask_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.AddNewTask(Window.GetWindow(this));
        }

        private void EditTask_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.EditSelectedTask(Window.GetWindow(this));
        }

        private void DeleteTask_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.DeleteSelectedTask();
        }

        private void PrevMonth_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.PrevMonth();
        }

        private void NextMonth_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.NextMonth();
        }
    }
}
