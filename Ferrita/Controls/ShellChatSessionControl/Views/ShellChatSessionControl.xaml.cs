using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ferrita.Controls.ShellChatSessionControl.ViewModels;
using Ferrita.Services.ChatSession;

namespace Ferrita.Controls.ShellChatSessionControl.Views
{
    public partial class ShellChatSessionControl : UserControl
    {
        private INotifyCollectionChanged? _trackedMessages;
        private ScrollViewer? _messagesScrollViewer;
        private bool _autoScrollToLatestMessage = true;
        private bool _isScrollingToLatestMessage;
        private bool _isScrollToLatestMessageQueued;

        private List<string>? _historyTexts;
        private int _historyNavIndex = -1;
        private string _tempDraft = string.Empty;
        private bool _isNavigatingHistory;

        public ShellChatSessionControl()
        {
            InitializeComponent();
            Loaded += ShellChatSessionControl_Loaded;
            Unloaded += ShellChatSessionControl_Unloaded;
            DataContextChanged += ShellChatSessionControl_DataContextChanged;
            MessagesListBox.PreviewMouseDown += MessagesListBox_PreviewMouseDown;
        }

        public void FocusComposer()
        {
            InputTextBox.Focus();
            Keyboard.Focus(InputTextBox);
        }

        private void ShellChatSessionControl_Loaded(object sender, RoutedEventArgs e)
        {
            HookMessagesScrollViewer();
            TrackMessagesCollection();
            _autoScrollToLatestMessage = true;
            ScrollToBottom(force: true);
        }

        private void ShellChatSessionControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachMessagesScrollViewer();
            DetachMessagesCollection();
        }

        private void ShellChatSessionControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ShellChatSessionControlViewModel oldViewModel)
            {
                oldViewModel.RequestClose -= OnRequestClose;
            }

            if (e.NewValue is ShellChatSessionControlViewModel newViewModel)
            {
                newViewModel.RequestClose += OnRequestClose;
                _autoScrollToLatestMessage = true;
                ScrollToBottom(force: true);
            }

            TrackMessagesCollection();
        }

        private void TrackMessagesCollection()
        {
            var messageCollection = (DataContext as ShellChatSessionControlViewModel)?.Messages;
            if (ReferenceEquals(_trackedMessages, messageCollection))
            {
                return;
            }

            DetachMessagesCollection();
            _trackedMessages = messageCollection;
            if (_trackedMessages != null)
            {
                _trackedMessages.CollectionChanged += Messages_CollectionChanged;
            }
        }

        private void DetachMessagesCollection()
        {
            if (_trackedMessages == null)
            {
                return;
            }

            _trackedMessages.CollectionChanged -= Messages_CollectionChanged;
            _trackedMessages = null;
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_autoScrollToLatestMessage || IsMessagesScrollViewerAtBottom())
            {
                ScrollToBottom();
            }
        }

        private void MessagesListBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                return;
            }

            _autoScrollToLatestMessage = !_autoScrollToLatestMessage;
            if (_autoScrollToLatestMessage)
            {
                ScrollToBottom();
            }

            e.Handled = true;
        }

        private void OnRequestClose()
        {
            Window.GetWindow(this)?.Close();
        }

        private void HookMessagesScrollViewer()
        {
            if (!IsLoaded || _messagesScrollViewer != null)
            {
                return;
            }

            MessagesListBox.ApplyTemplate();
            _messagesScrollViewer = FindDescendant<ScrollViewer>(MessagesListBox);
            if (_messagesScrollViewer != null)
            {
                _messagesScrollViewer.ScrollChanged += MessagesScrollViewer_ScrollChanged;
                return;
            }

            Dispatcher.BeginInvoke(new Action(HookMessagesScrollViewer), DispatcherPriority.Loaded);
        }

        private void DetachMessagesScrollViewer()
        {
            if (_messagesScrollViewer == null)
            {
                return;
            }

            _messagesScrollViewer.ScrollChanged -= MessagesScrollViewer_ScrollChanged;
            _messagesScrollViewer = null;
        }

        private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (DataContext is ShellChatSessionControlViewModel vm && vm.IsComputerUseMode)
            {
                _autoScrollToLatestMessage = true;
                ScrollToBottom(force: true);
                return;
            }

            if (_isScrollingToLatestMessage)
            {
                return;
            }

            if (e.ExtentHeightChange > 0.0 && _autoScrollToLatestMessage)
            {
                ScrollToBottom();
                return;
            }

            if (Math.Abs(e.VerticalChange) > 0.0 ||
                Math.Abs(e.ViewportHeightChange) > 0.0)
            {
                _autoScrollToLatestMessage = IsMessagesScrollViewerAtBottom();
            }
        }

        private void ScrollToBottom(bool force = false)
        {
            if (DataContext is ShellChatSessionControlViewModel vm && vm.IsComputerUseMode)
            {
                force = true;
            }

            if (!force && !_autoScrollToLatestMessage)
            {
                return;
            }

            if (MessagesListBox.Items.Count == 0)
            {
                return;
            }

            if (_isScrollToLatestMessageQueued)
            {
                return;
            }

            _isScrollToLatestMessageQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isScrollToLatestMessageQueued = false;
                if (DataContext is ShellChatSessionControlViewModel vm && vm.IsComputerUseMode)
                {
                    force = true;
                }

                if (!force && !_autoScrollToLatestMessage)
                {
                    return;
                }

                if (MessagesListBox.Items.Count == 0)
                {
                    return;
                }

                _isScrollingToLatestMessage = true;
                try
                {
                    MessagesListBox.ScrollIntoView(MessagesListBox.Items[^1]);
                    _messagesScrollViewer?.ScrollToEnd();
                    _autoScrollToLatestMessage = true;
                }
                finally
                {
                    _isScrollingToLatestMessage = false;
                }
            }), DispatcherPriority.Background);
        }

        private bool IsMessagesScrollViewerAtBottom()
        {
            if (_messagesScrollViewer == null)
            {
                return true;
            }

            return _messagesScrollViewer.ScrollableHeight <= 0.0 ||
                   _messagesScrollViewer.VerticalOffset >= _messagesScrollViewer.ScrollableHeight - 1.0;
        }

        private static T? FindDescendant<T>(DependencyObject root)
            where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up)
            {
                HandleHistoryNavigation(true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                HandleHistoryNavigation(false);
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Return && e.Key != Key.Enter)
            {
                return;
            }

            var modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            {
                return;
            }

            if (modifiers != ModifierKeys.None)
            {
                return;
            }

            if (DataContext is ShellChatSessionControlViewModel viewModel &&
                viewModel.SendCommand.CanExecute(null))
            {
                viewModel.SendCommand.Execute(null);
            }

            e.Handled = true;
        }

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isNavigatingHistory)
            {
                _historyNavIndex = -1;
                _tempDraft = string.Empty;
                _historyTexts = null;
            }
        }

        private void HandleHistoryNavigation(bool isUp)
        {
            if (_historyTexts == null)
            {
                _historyTexts = PromptHistoryService.Instance.GetHistoryTexts()
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
            }

            if (_historyTexts.Count == 0)
            {
                return;
            }

            if (isUp)
            {
                if (_historyNavIndex == -1)
                {
                    _tempDraft = InputTextBox.Text ?? string.Empty;
                    _historyNavIndex = _historyTexts.Count - 1;
                }
                else if (_historyNavIndex > 0)
                {
                    _historyNavIndex--;
                }
                else
                {
                    return;
                }
            }
            else
            {
                if (_historyNavIndex == -1)
                {
                    return;
                }

                if (_historyNavIndex < _historyTexts.Count - 1)
                {
                    _historyNavIndex++;
                }
                else
                {
                    _historyNavIndex = -1;
                }
            }

            _isNavigatingHistory = true;
            try
            {
                string targetText = _historyNavIndex == -1 ? _tempDraft : _historyTexts[_historyNavIndex];
                InputTextBox.Text = targetText;
                InputTextBox.CaretIndex = targetText.Length;
            }
            finally
            {
                _isNavigatingHistory = false;
            }
        }

        private void InputTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void InputTextBox_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths && filePaths.Length > 0)
            {
                var pathsText = string.Join(" ", filePaths.Select(p => $"\"{p}\""));
                if (sender is TextBox textBox)
                {
                    var selectionStart = textBox.SelectionStart;
                    var text = textBox.Text ?? string.Empty;
                    textBox.Text = text.Insert(selectionStart, pathsText);
                    textBox.SelectionStart = selectionStart + pathsText.Length;
                    textBox.Focus();
                }

                e.Handled = true;
            }
        }
    }
}
