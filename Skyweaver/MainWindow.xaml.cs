using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Skyweaver.ViewModels;

namespace Skyweaver
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            Closing += MainWindow_Closing;
            KeyDown += MainWindow_KeyDown;
        }

        private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // 按 Ctrl + Shift + C 快捷键直接弹出 Shell 聊天窗口
            if (e.Key == System.Windows.Input.Key.C && 
                (System.Windows.Input.Keyboard.Modifiers & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
            {
                var shellWindow = new Skyweaver.Windows.ShellChatWindow();
                shellWindow.Owner = this;
                shellWindow.Show();
            }
        }

        private void SessionListPanelView_Loaded()
        {

        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            var app = Application.Current as App;
            if (app != null && app.IsShuttingDown)
            {
                return;
            }

            e.Cancel = true;
            Hide();

            _ = Task.Run(async () =>
            {
                try
                {
                    await _viewModel.HandleGuiClosingAsync();
                }
                catch
                {
                    // Protection against unhandled exceptions on background thread
                }
            });
        }

        private void ChatFullViewSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            // 获取当前两列的实际宽度
            double chatWidth = ColChatArea.ActualWidth;
            double fullViewWidth = ColFullView.ActualWidth;

            // 设定折叠吸附阈值（120像素）
            const double threshold = 120;
            double total = chatWidth + fullViewWidth;

            // 向左拖拽 (e.HorizontalChange < 0)，聊天区域（左侧）变小，全视图（右侧）变大
            if (e.HorizontalChange < 0)
            {
                if (chatWidth < threshold)
                {
                    ColChatArea.Width = new GridLength(0);
                    ColFullView.Width = new GridLength(total);
                }
            }
            // 向右拖拽 (e.HorizontalChange > 0)，聊天区域（左侧）变大，全视图（右侧）变小
            else if (e.HorizontalChange > 0)
            {
                if (fullViewWidth < threshold)
                {
                    ColFullView.Width = new GridLength(0);
                    ColChatArea.Width = new GridLength(total);
                }
            }
        }

        private void GridSplitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            // 在拖拽开始时，将所有受影响的可调整列宽度转换为绝对像素（Pixel），防止拖拽非相邻列时互相干扰
            ColSessionList.Width = new GridLength(ColSessionList.ActualWidth, GridUnitType.Pixel);
            ColChatArea.Width = new GridLength(ColChatArea.ActualWidth, GridUnitType.Pixel);
            ColFullView.Width = new GridLength(ColFullView.ActualWidth, GridUnitType.Pixel);
        }

        private void GridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            // 拖拽结束时，确保两列不会停留在小于阈值的尴尬宽度
            double w0 = ColSessionList.ActualWidth;
            double w2 = ColChatArea.ActualWidth;
            double w4 = ColFullView.ActualWidth;

            const double threshold = 120;

            if (w2 < threshold)
            {
                // 聊天区域折叠为 0。多出来的空间分配给全视图
                double total = w2 + w4;
                w2 = 0;
                w4 = total;
            }
            else if (w4 < threshold)
            {
                // 全视图折叠为 0。多出来的空间分配给聊天区域
                double total = w2 + w4;
                w4 = 0;
                w2 = total;
            }

            // 重新应用为 Star 比例，保持窗口自适应能力
            ColSessionList.Width = w0 > 0 ? new GridLength(w0, GridUnitType.Star) : new GridLength(0);
            ColChatArea.Width = w2 > 0 ? new GridLength(w2, GridUnitType.Star) : new GridLength(0);
            ColFullView.Width = w4 > 0 ? new GridLength(w4, GridUnitType.Star) : new GridLength(0);
        }
    }
}
