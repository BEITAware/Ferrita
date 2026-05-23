using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Skyweaver.Controls.ShellChatSessionControl.ViewModels;

namespace Skyweaver.Windows
{
    /// <summary>
    /// ShellChatWindow.xaml 的交互逻辑
    /// </summary>
    public partial class ShellChatWindow : Window
    {
        private readonly ShellChatSessionControlViewModel _viewModel;

        public ShellChatWindow()
        {
            InitializeComponent();

            // 实例化 ViewModel 并进行 DataContext 绑定
            _viewModel = new ShellChatSessionControlViewModel();
            ChatControl.DataContext = _viewModel;

            // 订阅 ViewModel 中请求关闭的事件
            _viewModel.RequestClose += ViewModel_RequestClose;

            Loaded += ShellChatWindow_Loaded;
        }

        private void ShellChatWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 将窗口智能定位在屏幕的右侧偏下位置（避开系统任务栏）
            PositionWindowOnScreenRightBottom();
        }

        /// <summary>
        /// 将窗口定位在屏幕的右下角
        /// </summary>
        private void PositionWindowOnScreenRightBottom()
        {
            try
            {
                // 获取主屏幕工作区（不包含任务栏）
                double screenWidth = SystemParameters.WorkArea.Width;
                double screenHeight = SystemParameters.WorkArea.Height;

                // 设置窗口位置，距离右边缘 30 像素，下边缘 30 像素
                this.Left = screenWidth - this.Width - 30;
                this.Top = screenHeight - this.Height - 30;
            }
            catch (Exception)
            {
                // 降级使用屏幕中心
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 允许通过拖拽窗口的任意背景区域来移动窗口
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // 当窗口失去焦点（例如用户点击了桌面的其它应用），直接关闭窗口，表现为轻量级 Shell 菜单呼出行为
            try
            {
                this.Close();
            }
            catch
            {
                // 忽略已关闭或正在关闭的异常
            }
        }

        private void ViewModel_RequestClose()
        {
            this.Close();
        }

        private void BackgroundChrome_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BackgroundChrome.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
        }
    }
}
