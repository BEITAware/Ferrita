using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Skyweaver.ViewModels;

namespace Skyweaver
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _isGuiClosingInProgress;
        private bool _allowGuiClose;

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

        private async void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowGuiClose)
            {
                return;
            }

            e.Cancel = true;

            if (_isGuiClosingInProgress)
            {
                return;
            }

            _isGuiClosingInProgress = true;
            IsEnabled = false;

            try
            {
                await _viewModel.HandleGuiClosingAsync();
            }
            finally
            {
                _allowGuiClose = true;
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(Close));
            }
        }
    }
}
