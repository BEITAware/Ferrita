using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Skyweaver.Controls.ScheduledTasksControl.Models;

namespace Skyweaver.Windows
{
    /// <summary>
    /// 添加磁贴的通用对话框，提供“开始实时会话”、所有计划任务的ComboBox以及最近7个计划任务的快速球体按钮
    /// </summary>
    public sealed class AddTileUniversalDialog : UniversalNewDialog
    {
        /// <summary>
        /// 用户选中的计划任务（如果是选择的计划任务）
        /// </summary>
        public ScheduledTask? SelectedTask { get; private set; }

        /// <summary>
        /// 用户是否选择了“开始实时会话”
        /// </summary>
        public bool IsLiveSessionSelected { get; private set; }

        public AddTileUniversalDialog(IReadOnlyList<ScheduledTask> allTasks)
        {
            MainTitle = "添加磁贴";
            MainDescription = "选择要添加到 Live Tiles 页面的任务或实时会话。";
            SetMainIcon("/Skyweaver;component/Resources/SkyweaverLogo.png");

            // 1. 固定存在一个按钮：开始实时会话......
            AddTriggerOption(
                "开始实时会话......",
                "添加一个实时会话控制磁贴",
                "/Skyweaver;component/Resources/NewNodeGraph.png",
                _ =>
                {
                    IsLiveSessionSelected = true;
                    CloseWithResult(true);
                    return true;
                });

            // 2. 固定存在一个按钮：更多任务...... (弹出面板为ComboBox)
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var comboBox = new ComboBox
            {
                MinWidth = 220,
                MinHeight = 32,
                DisplayMemberPath = "Name",
                ItemsSource = allTasks,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            if (allTasks.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }

            var confirmBtn = new Button
            {
                Content = "添加",
                MinWidth = 70,
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            confirmBtn.Click += (s, e) =>
            {
                if (comboBox.SelectedItem is ScheduledTask task)
                {
                    SelectedTask = task;
                    CloseWithResult(true);
                }
            };

            panel.Children.Add(comboBox);
            panel.Children.Add(confirmBtn);

            AddSettingOption(
                "更多任务......",
                "从所有已保存的计划任务中选择并添加磁贴",
                "/Skyweaver;component/Resources/ContextMenuSubMenu.png",
                panel,
                _ =>
                {
                    comboBox.Focus();
                });

            // 3. 其余7个球体为最近创建的计划任务（按创建时间降序）
            var recentTasks = allTasks.Take(7).ToList();
            for (int i = 0; i < recentTasks.Count; i++)
            {
                var task = recentTasks[i];
                var iconName = (i == 0) ? "Scheduled1.png" :
                               (i == 1) ? "Scheduled2.png" :
                               (i == 2) ? "Scheduled3.png" : "ScheduledMany.png";

                AddTriggerOption(
                    task.Name,
                    $"关联会话流：{task.SessionFlowName}",
                    $"/Skyweaver;component/Resources/{iconName}",
                    _ =>
                    {
                        SelectedTask = task;
                        CloseWithResult(true);
                        return true;
                    });
            }
        }
    }
}
