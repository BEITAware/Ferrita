using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Skyweaver.Controls.ChatSessionControl.ViewModels;
using Skyweaver.Services;

namespace Skyweaver.Controls.ChatSessionControl.Views
{
    public partial class ChatSessionControl : UserControl
    {
        public static readonly DependencyProperty RibbonTimeProperty =
            DependencyProperty.Register(
                nameof(RibbonTime),
                typeof(double),
                typeof(ChatSessionControl),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty RibbonAspectRatioProperty =
            DependencyProperty.Register(
                nameof(RibbonAspectRatio),
                typeof(double),
                typeof(ChatSessionControl),
                new PropertyMetadata(1.0));

        private readonly Stopwatch _ribbonStopwatch = Stopwatch.StartNew();
        private INotifyCollectionChanged? _trackedMessages;
        private ScrollViewer? _messagesScrollViewer;
        private bool _isRenderingHooked;
        private bool _isPinnedToLatestMessage;
        private bool _isScrollingToLatestMessage;
        private bool _isScrollToLatestMessageQueued;

        public ChatSessionControl()
        {
            InitializeComponent();
            Loaded += ChatSessionControl_Loaded;
            Unloaded += ChatSessionControl_Unloaded;
            DataContextChanged += ChatSessionControl_DataContextChanged;
            MessagesList.PreviewMouseDown += MessagesList_PreviewMouseDown;
        }

        private void ChatSessionControl_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateRibbonAspectRatio();
            HookRibbonRendering();
            HookMessagesScrollViewer();
            TrackMessagesCollection();
            ScrollToLatestMessage(force: true);
        }

        private void ChatSessionControl_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookRibbonRendering();
            DetachMessagesScrollViewer();
            DetachMessagesCollection();
        }

        private void ChatSessionControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            TrackMessagesCollection();
        }

        private void TrackMessagesCollection()
        {
            var messageCollection = (DataContext as ChatSessionControlViewModel)?.Messages;
            if (ReferenceEquals(_trackedMessages, messageCollection))
            {
                return;
            }

            DetachMessagesCollection();
            _trackedMessages = messageCollection;

            if (_trackedMessages != null)
            {
                _trackedMessages.CollectionChanged += MessagesCollection_CollectionChanged;
            }
        }

        private void DetachMessagesCollection()
        {
            if (_trackedMessages == null)
            {
                return;
            }

            _trackedMessages.CollectionChanged -= MessagesCollection_CollectionChanged;
            _trackedMessages = null;
        }

        private void MessagesCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isPinnedToLatestMessage)
            {
                ScrollToLatestMessage();
            }
        }

        private void MessagesList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                return;
            }

            _isPinnedToLatestMessage = !_isPinnedToLatestMessage;
            if (_isPinnedToLatestMessage)
            {
                ScrollToLatestMessage();
            }

            e.Handled = true;
        }

        private void MessageListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem listBoxItem)
            {
                return;
            }

            listBoxItem.IsSelected = true;
            listBoxItem.Focus();
        }

        private void HookMessagesScrollViewer()
        {
            if (!IsLoaded || _messagesScrollViewer != null)
            {
                return;
            }

            MessagesList.ApplyTemplate();
            _messagesScrollViewer = FindVisualChild<ScrollViewer>(MessagesList);
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
            if (!_isPinnedToLatestMessage || _isScrollingToLatestMessage)
            {
                return;
            }

            if (IsMessagesScrollViewerAtBottom())
            {
                return;
            }

            ScrollToLatestMessage();
        }

        private void ScrollToLatestMessage(bool force = false)
        {
            if (!force && !_isPinnedToLatestMessage)
            {
                return;
            }

            if (MessagesList.Items.Count == 0)
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
                if (!force && !_isPinnedToLatestMessage)
                {
                    return;
                }

                if (MessagesList.Items.Count == 0)
                {
                    return;
                }

                _isScrollingToLatestMessage = true;
                try
                {
                    var latestMessage = MessagesList.Items[MessagesList.Items.Count - 1];
                    MessagesList.ScrollIntoView(latestMessage);
                    _messagesScrollViewer?.ScrollToEnd();
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
                   _messagesScrollViewer.VerticalOffset >= _messagesScrollViewer.ScrollableHeight - 0.5;
        }

        private static T? FindVisualChild<T>(DependencyObject parent)
            where T : DependencyObject
        {
            var childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (var index = 0; index < childrenCount; index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void ComposerTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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

            if (DataContext is ChatSessionControlViewModel viewModel &&
                viewModel.SendMessageCommand.CanExecute(null))
            {
                viewModel.SendMessageCommand.Execute(null);
            }

            e.Handled = true;
        }

        private void ComposerTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!ReferenceEquals(sender, ComposerTextBox) ||
                !ComposerTextBox.IsKeyboardFocusWithin)
            {
                return;
            }

            var image = TryGetPastedImage(e);
            if (image == null)
            {
                return;
            }

            if (DataContext is ChatSessionControlViewModel viewModel &&
                viewModel.AddPastedImage(image))
            {
                e.CancelCommand();
                e.Handled = true;
            }
        }

        private static BitmapSource? TryGetPastedImage(DataObjectPastingEventArgs e)
        {
            if (e.SourceDataObject.GetDataPresent(DataFormats.Bitmap, autoConvert: true))
            {
                return e.SourceDataObject.GetData(DataFormats.Bitmap, autoConvert: true) as BitmapSource;
            }

            return ClipboardAccessService.TryGetImage(out var image, out _)
                ? image
                : null;
        }

        public double RibbonTime
        {
            get => (double)GetValue(RibbonTimeProperty);
            set => SetValue(RibbonTimeProperty, value);
        }

        public double RibbonAspectRatio
        {
            get => (double)GetValue(RibbonAspectRatioProperty);
            set => SetValue(RibbonAspectRatioProperty, value);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateRibbonAspectRatio();
        }

        private void HookRibbonRendering()
        {
            if (_isRenderingHooked)
            {
                return;
            }

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _isRenderingHooked = true;
        }

        private void UnhookRibbonRendering()
        {
            if (!_isRenderingHooked)
            {
                return;
            }

            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _isRenderingHooked = false;
        }

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            RibbonTime = _ribbonStopwatch.Elapsed.TotalSeconds;
        }

        private void UpdateRibbonAspectRatio()
        {
            var height = ActualHeight;
            RibbonAspectRatio = height > 0 ? Math.Max(ActualWidth / height, 0.0001) : 1.0;
        }
    }
}
