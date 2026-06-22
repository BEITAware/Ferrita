using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ferrita.Controls.ShellChatSessionControl.ViewModels;
using Ferrita.Models.ChatSession;

namespace Ferrita.Windows
{
    public partial class ComputerUseWindow : Window
    {
        private readonly ShellChatSessionControlViewModel _viewModel;

        public ComputerUseWindow(ChatSessionModel session)
        {
            InitializeComponent();

            _viewModel = new ShellChatSessionControlViewModel(session, isComputerUseMode: true);
            ChatControl.DataContext = _viewModel;
            _viewModel.RequestClose += () => CloseWithAnimation();

            Loaded += ComputerUseWindow_Loaded;
        }

        private void ComputerUseWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PositionWindowOnScreenRightBottom();
        }

        private void PositionWindowOnScreenRightBottom()
        {
            try
            {
                var screenWidth = SystemParameters.WorkArea.Width;
                var screenHeight = SystemParameters.WorkArea.Height;
                Left = screenWidth - Width - 30;
                Top = screenHeight - Height - 30;
            }
            catch (Exception)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed &&
                e.OriginalSource is DependencyObject source &&
                !IsInteractiveChild(source))
            {
                DragMove();
            }
        }

        private static bool IsInteractiveChild(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.ListBoxItem or System.Windows.Controls.Primitives.ScrollBar)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private bool _isClosing = false;
        public void CloseWithAnimation()
        {
            if (_isClosing) return;
            _isClosing = true;

            var sb = new Storyboard();
            
            var opacityAnim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromSeconds(0.2),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityAnim, this);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(Window.OpacityProperty));
            sb.Children.Add(opacityAnim);

            var scaleXAnim = new DoubleAnimation
            {
                To = 0.75,
                Duration = TimeSpan.FromSeconds(0.25),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.4 }
            };
            Storyboard.SetTargetName(scaleXAnim, "RootScale");
            Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath(ScaleTransform.ScaleXProperty));
            sb.Children.Add(scaleXAnim);

            var scaleYAnim = new DoubleAnimation
            {
                To = 0.75,
                Duration = TimeSpan.FromSeconds(0.25),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.4 }
            };
            Storyboard.SetTargetName(scaleYAnim, "RootScale");
            Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath(ScaleTransform.ScaleYProperty));
            sb.Children.Add(scaleYAnim);

            var translateYAnim = new DoubleAnimation
            {
                To = 60,
                Duration = TimeSpan.FromSeconds(0.25),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTargetName(translateYAnim, "RootTranslate");
            Storyboard.SetTargetProperty(translateYAnim, new PropertyPath(TranslateTransform.YProperty));
            sb.Children.Add(translateYAnim);

            sb.Completed += (s, e) => Close();
            sb.Begin(this);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _viewModel.Cleanup();
        }

        private void BackgroundChrome_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BackgroundChrome.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
        }
    }
}
