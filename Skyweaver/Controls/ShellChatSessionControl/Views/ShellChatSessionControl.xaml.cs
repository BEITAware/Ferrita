using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Skyweaver.Controls.ShellChatSessionControl.ViewModels;

namespace Skyweaver.Controls.ShellChatSessionControl.Views
{
    /// <summary>
    /// ShellChatSessionControl.xaml 的交互逻辑
    /// </summary>
    public partial class ShellChatSessionControl : UserControl
    {
        public ShellChatSessionControl()
        {
            InitializeComponent();
            DataContextChanged += ShellChatSessionControl_DataContextChanged;
        }

        private void ShellChatSessionControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ShellChatSessionControlViewModel oldVm)
            {
                oldVm.RequestClose -= OnRequestClose;
                if (oldVm.Messages is INotifyCollectionChanged oldCollection)
                {
                    oldCollection.CollectionChanged -= Messages_CollectionChanged;
                }
            }

            if (e.NewValue is ShellChatSessionControlViewModel newVm)
            {
                newVm.RequestClose += OnRequestClose;
                if (newVm.Messages is INotifyCollectionChanged newCollection)
                {
                    newCollection.CollectionChanged += Messages_CollectionChanged;
                }
                
                // 初始数据加载后，滚动到底部
                ScrollToBottom();
            }
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 消息更新时，在 UI 渲染后滚动到底部
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                ScrollToBottom();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnRequestClose()
        {
            // 找到包含当前控件的窗口并将其关闭
            Window parentWindow = Window.GetWindow(this);
            if (parentWindow != null)
            {
                // 如果是对话框模式或标准模式，直接关闭
                parentWindow.Close();
            }
        }

        /// <summary>
        /// 滚动到 ListBox 底部
        /// </summary>
        private void ScrollToBottom()
        {
            if (MessagesListBox.Items.Count > 0)
            {
                var border = VisualTreeHelper.GetChild(MessagesListBox, 0) as Border;
                if (border != null)
                {
                    var scrollViewer = border.Child as ScrollViewer;
                    if (scrollViewer != null)
                    {
                        scrollViewer.ScrollToEnd();
                        return;
                    }
                }
                
                // 备用方法
                MessagesListBox.ScrollIntoView(MessagesListBox.Items[MessagesListBox.Items.Count - 1]);
            }
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // 按 Enter 键发送（不含 Shift）
            if (e.Key == KeyIsValidEnter())
            {
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    if (DataContext is ShellChatSessionControlViewModel vm && vm.SendCommand.CanExecute(null))
                    {
                        vm.SendCommand.Execute(null);
                    }
                }
            }
        }

        private Key KeyIsValidEnter()
        {
            return Key.Enter;
        }
    }
}
