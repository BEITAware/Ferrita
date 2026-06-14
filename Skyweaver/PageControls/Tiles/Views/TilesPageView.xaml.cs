using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Skyweaver.PageControls.Tiles.ViewModels;

namespace Skyweaver.PageControls.Tiles.Views
{
    public partial class TilesPageView : UserControl
    {
        public static readonly DependencyProperty BackgroundTimeProperty =
            DependencyProperty.Register(
                nameof(BackgroundTime),
                typeof(double),
                typeof(TilesPageView),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty BackgroundAspectRatioProperty =
            DependencyProperty.Register(
                nameof(BackgroundAspectRatio),
                typeof(double),
                typeof(TilesPageView),
                new PropertyMetadata(1.0));

        private const double TileCellSize = 132.0;
        private const double DropPadding = 40.0;
        private const double DragAutoScrollEdge = 54.0;
        private const double DragAutoScrollStep = 28.0;
        private const double DragAvoidanceRadius = 600.0;
        private const double DragAvoidanceMaxOffset = 45.0;
        private const double ResizeAvoidanceRadius = 240.0;
        private const double ResizeAvoidanceMaxOffset = 26.0;

        private readonly Stopwatch _backgroundStopwatch = Stopwatch.StartNew();
        private readonly HashSet<ContentPresenter> _dragAvoidancePresenters = new();
        private readonly Dictionary<TileItemViewModel, Rect> _preLayoutTileRects = new();
        private bool _isRenderingHooked;
        private TilesPageViewModel? _subscribedViewModel;

        private int _currentPageIndex;
        private DateTime _lastSwitchTime = DateTime.MinValue;

        private TileItemViewModel? _pendingDragTile;
        private ContentPresenter? _pendingDragPresenter;
        private Point _dragStartMouseInView;
        private double _dragStartHorizontalOffset;

        private TileItemViewModel? _draggedTile;
        private ContentPresenter? _draggedPresenter;
        private bool _isDraggingActive;
        private int _hoverGroupIndex = -1;
        private int _hoverColumn = -1;
        private int _hoverRow = -1;

        public TilesPageView()
        {
            InitializeComponent();
            DataContextChanged += TilesPageView_DataContextChanged;
            Loaded += TilesPageView_Loaded;
            Unloaded += TilesPageView_Unloaded;
        }

        public double BackgroundTime
        {
            get => (double)GetValue(BackgroundTimeProperty);
            set => SetValue(BackgroundTimeProperty, value);
        }

        public double BackgroundAspectRatio
        {
            get => (double)GetValue(BackgroundAspectRatioProperty);
            set => SetValue(BackgroundAspectRatioProperty, value);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateBackgroundAspectRatio();
        }

        private void TilesPageView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeToViewModel(DataContext as TilesPageViewModel);
            UpdateBackgroundAspectRatio();
            HookBackgroundRendering();

            PageLiveTiles.Visibility = Visibility.Visible;
            PageLiveSession.Visibility = Visibility.Collapsed;
            if (PageLiveTiles.RenderTransform is TranslateTransform tileTransform)
            {
                tileTransform.X = 0;
            }

            if (PageLiveSession.RenderTransform is TranslateTransform sessionTransform)
            {
                sessionTransform.X = 0;
            }

            BtnLiveTiles.Tag = "Selected";
            BtnLiveSession.Tag = string.Empty;
            _currentPageIndex = 0;
        }

        private void TilesPageView_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelActiveDrag();
            AnimateDragAvoidanceHome();
            _preLayoutTileRects.Clear();
            SubscribeToViewModel(null);
            UnhookBackgroundRendering();
        }

        private void TilesPageView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SubscribeToViewModel(e.NewValue as TilesPageViewModel);
        }

        private void SubscribeToViewModel(TilesPageViewModel? viewModel)
        {
            if (ReferenceEquals(_subscribedViewModel, viewModel))
            {
                return;
            }

            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.TileLayoutChanging -= OnTileLayoutChanging;
                _subscribedViewModel.TileLayoutChanged -= OnTileLayoutChanged;
            }

            _subscribedViewModel = viewModel;

            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.TileLayoutChanging += OnTileLayoutChanging;
                _subscribedViewModel.TileLayoutChanged += OnTileLayoutChanged;
            }
        }

        private void OnTileLayoutChanging(object? sender, TileLayoutTransitionEventArgs e)
        {
            if (_isDraggingActive)
            {
                return;
            }

            _preLayoutTileRects.Clear();
            CaptureVisibleTileLayoutRects(_preLayoutTileRects);
        }

        private void OnTileLayoutChanged(object? sender, TileLayoutTransitionEventArgs e)
        {
            if (_isDraggingActive || _preLayoutTileRects.Count == 0)
            {
                _preLayoutTileRects.Clear();
                return;
            }

            var beforeRects = new Dictionary<TileItemViewModel, Rect>(_preLayoutTileRects);
            _preLayoutTileRects.Clear();

            Dispatcher.BeginInvoke(
                new Action(() => AnimateResizeAvoidance(e.Tile, beforeRects)),
                DispatcherPriority.Loaded);
        }

        private void HookBackgroundRendering()
        {
            if (_isRenderingHooked)
            {
                return;
            }

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _isRenderingHooked = true;
        }

        private void UnhookBackgroundRendering()
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
            BackgroundTime = _backgroundStopwatch.Elapsed.TotalSeconds;
        }

        private void UpdateBackgroundAspectRatio()
        {
            BackgroundAspectRatio = ActualHeight > 0
                ? Math.Max(ActualWidth / ActualHeight, 0.0001)
                : 1.0;
        }

        private void SwitchToPage(int targetIndex)
        {
            if (targetIndex == _currentPageIndex || targetIndex < 0 || targetIndex > 1)
            {
                return;
            }

            double width = ActualWidth > 0 ? ActualWidth : 1000;
            Grid oldPage = _currentPageIndex == 0 ? PageLiveTiles : PageLiveSession;
            Grid newPage = targetIndex == 0 ? PageLiveTiles : PageLiveSession;

            bool movingForward = targetIndex > _currentPageIndex;
            double oldTargetX = movingForward ? -width : width;
            double newStartX = movingForward ? width : -width;

            var oldTransform = EnsureTranslateTransform(oldPage);
            var newTransform = EnsureTranslateTransform(newPage);
            newTransform.X = newStartX;
            newPage.Visibility = Visibility.Visible;

            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromSeconds(0.65);

            var oldAnimation = new DoubleAnimation
            {
                To = oldTargetX,
                Duration = duration,
                EasingFunction = ease
            };

            var newAnimation = new DoubleAnimation
            {
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };

            int localPageIndex = targetIndex;
            oldAnimation.Completed += (_, _) =>
            {
                if (_currentPageIndex == localPageIndex)
                {
                    oldPage.Visibility = Visibility.Collapsed;
                    oldTransform.X = oldTargetX;
                    newTransform.X = 0;
                }
            };

            oldTransform.BeginAnimation(TranslateTransform.XProperty, oldAnimation);
            newTransform.BeginAnimation(TranslateTransform.XProperty, newAnimation);

            _currentPageIndex = targetIndex;
            BtnLiveTiles.Tag = _currentPageIndex == 0 ? "Selected" : string.Empty;
            BtnLiveSession.Tag = _currentPageIndex == 1 ? "Selected" : string.Empty;
        }

        private static TranslateTransform EnsureTranslateTransform(UIElement element)
        {
            if (element.RenderTransform is TranslateTransform transform)
            {
                return transform;
            }

            transform = new TranslateTransform();
            element.RenderTransform = transform;
            return transform;
        }

        private void BtnLiveTiles_Click(object sender, RoutedEventArgs e)
        {
            SwitchToPage(0);
        }

        private void BtnLiveSession_Click(object sender, RoutedEventArgs e)
        {
            SwitchToPage(1);
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isDraggingActive)
            {
                e.Handled = true;
                return;
            }

            if ((DateTime.Now - _lastSwitchTime).TotalMilliseconds < 400)
            {
                e.Handled = true;
                return;
            }

            if (e.Delta < 0 && _currentPageIndex == 0)
            {
                SwitchToPage(1);
                _lastSwitchTime = DateTime.Now;
                e.Handled = true;
            }
            else if (e.Delta > 0 && _currentPageIndex == 1)
            {
                SwitchToPage(0);
                _lastSwitchTime = DateTime.Now;
                e.Handled = true;
            }
        }

        private void TileScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
            {
                return;
            }

            double direction = e.Delta < 0 ? 1 : -1;
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + direction * TileCellSize);
            e.Handled = true;
        }

        private void OnTilePresenterLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ContentPresenter presenter && presenter.RenderTransform is not TranslateTransform)
            {
                presenter.RenderTransform = new TranslateTransform();
            }
        }

        private void OnTilePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                sender is not Button button ||
                button.DataContext is not TileItemViewModel tile)
            {
                return;
            }

            var presenter = FindVisualParent<ContentPresenter>(button);
            if (presenter == null)
            {
                return;
            }

            _pendingDragTile = tile;
            _pendingDragPresenter = presenter;
            _dragStartMouseInView = e.GetPosition(this);
            _dragStartHorizontalOffset = TileScrollViewer.HorizontalOffset;
        }

        private void OnPageViewPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingActive)
            {
                UpdateActiveDrag(e);
                e.Handled = true;
                return;
            }

            if (_pendingDragTile == null || _pendingDragPresenter == null)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ClearPendingDrag();
                return;
            }

            Point currentMouse = e.GetPosition(this);
            if (Math.Abs(currentMouse.X - _dragStartMouseInView.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentMouse.Y - _dragStartMouseInView.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            StartDrag();
            UpdateActiveDrag(e);
            e.Handled = true;
        }

        private void OnPageViewPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingActive)
            {
                ClearPendingDrag();
                return;
            }

            FinishDrag();
            e.Handled = true;
        }

        private void StartDrag()
        {
            if (_pendingDragTile == null || _pendingDragPresenter == null)
            {
                return;
            }

            _draggedTile = _pendingDragTile;
            _draggedPresenter = _pendingDragPresenter;
            _isDraggingActive = true;
            _hoverGroupIndex = -1;
            _hoverColumn = -1;
            _hoverRow = -1;

            ResetPresenterTransform(_draggedPresenter);
            CaptureMouse();
            _draggedTile.IsDragging = true;
            if (DataContext is TilesPageViewModel vm)
            {
                vm.IsAnyTileDragging = true;
            }
            UpdateDragAvoidance();
            ClearPendingDrag();
        }

        private void UpdateActiveDrag(MouseEventArgs e)
        {
            if (_draggedTile == null || _draggedPresenter == null)
            {
                return;
            }

            Point currentMouse = e.GetPosition(this);
            var transform = EnsurePresenterTransform(_draggedPresenter);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = currentMouse.X - _dragStartMouseInView.X + TileScrollViewer.HorizontalOffset - _dragStartHorizontalOffset;
            transform.Y = currentMouse.Y - _dragStartMouseInView.Y;

            AutoScrollWhileDragging(e);
            UpdateDragAvoidance();

            if (TryGetDropCell(e, _draggedTile, out int groupIndex, out int column, out int row))
            {
                if (groupIndex != _hoverGroupIndex || column != _hoverColumn || row != _hoverRow)
                {
                    _hoverGroupIndex = groupIndex;
                    _hoverColumn = column;
                    _hoverRow = row;

                    if (DataContext is TilesPageViewModel vm)
                    {
                        vm.ShowDropPreview(_draggedTile, groupIndex, column, row);
                    }
                }
            }
            else
            {
                _hoverGroupIndex = -1;
                _hoverColumn = -1;
                _hoverRow = -1;

                if (DataContext is TilesPageViewModel vm)
                {
                    vm.ClearDropPreview();
                }
            }
        }

        private void FinishDrag()
        {
            if (_draggedTile == null || _draggedPresenter == null)
            {
                CancelActiveDrag();
                return;
            }

            ReleaseMouseCapture();

            if (DataContext is TilesPageViewModel vm)
            {
                vm.ClearDropPreview();
                if (_hoverGroupIndex >= 0)
                {
                    AnimateDragAvoidanceHome();
                    ResetPresenterTransform(_draggedPresenter);
                    _draggedTile.IsDragging = false;
                    vm.MoveTileToCell(_draggedTile, _hoverGroupIndex, _hoverColumn, _hoverRow);
                    ClearActiveDrag();
                    return;
                }
            }

            _draggedTile.IsDragging = false;
            AnimatePresenterHome(_draggedPresenter);
            AnimateDragAvoidanceHome();
            ClearActiveDrag();
        }

        private void CancelActiveDrag()
        {
            if (DataContext is TilesPageViewModel vm)
            {
                vm.ClearDropPreview();
            }

            if (_draggedTile != null)
            {
                _draggedTile.IsDragging = false;
            }

            if (_draggedPresenter != null)
            {
                AnimatePresenterHome(_draggedPresenter);
            }

            AnimateDragAvoidanceHome();
            ReleaseMouseCapture();
            ClearPendingDrag();
            ClearActiveDrag();
        }

        private void ClearPendingDrag()
        {
            _pendingDragTile = null;
            _pendingDragPresenter = null;
        }

        private void ClearActiveDrag()
        {
            _draggedTile = null;
            _draggedPresenter = null;
            _isDraggingActive = false;
            if (DataContext is TilesPageViewModel vm)
            {
                vm.IsAnyTileDragging = false;
            }
            _hoverGroupIndex = -1;
            _hoverColumn = -1;
            _hoverRow = -1;
        }

        private static TranslateTransform EnsurePresenterTransform(ContentPresenter presenter)
        {
            if (presenter.RenderTransform is TranslateTransform transform)
            {
                return transform;
            }

            transform = new TranslateTransform();
            presenter.RenderTransform = transform;
            return transform;
        }

        private static void ResetPresenterTransform(ContentPresenter presenter)
        {
            var transform = EnsurePresenterTransform(presenter);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0;
            transform.Y = 0;
        }

        private static void AnimatePresenterHome(ContentPresenter presenter)
        {
            AnimatePresenterOffset(
                presenter,
                0,
                0,
                TimeSpan.FromMilliseconds(260),
                () => new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        private void UpdateDragAvoidance()
        {
            if (_draggedTile == null ||
                _draggedPresenter == null ||
                !TryGetTileLayoutRectInView(_draggedPresenter, _draggedTile, out Rect draggedRect))
            {
                AnimateDragAvoidanceHome();
                return;
            }

            var dragTransform = EnsurePresenterTransform(_draggedPresenter);
            draggedRect.Offset(dragTransform.X, dragTransform.Y);

            ApplyAvoidanceFromRect(
                draggedRect,
                _draggedTile,
                DragAvoidanceRadius,
                DragAvoidanceMaxOffset,
                TimeSpan.FromMilliseconds(110),
                trackForDrag: true);
        }

        private HashSet<ContentPresenter> ApplyAvoidanceFromRect(
            Rect sourceRect,
            TileItemViewModel excludedTile,
            double radius,
            double maxOffset,
            TimeSpan duration,
            bool trackForDrag)
        {
            var affectedPresenters = new HashSet<ContentPresenter>();

            foreach (var presenter in EnumerateTilePresenters())
            {
                if (presenter.DataContext is not TileItemViewModel tile ||
                    ReferenceEquals(tile, excludedTile) ||
                    tile.IsDragging ||
                    !TryGetTileLayoutRectInView(presenter, tile, out Rect tileRect))
                {
                    continue;
                }

                Vector offset = CalculateAvoidanceOffset(sourceRect, tileRect, radius, maxOffset);
                if (Math.Abs(offset.X) <= 0.1 && Math.Abs(offset.Y) <= 0.1)
                {
                    continue;
                }

                AnimatePresenterOffset(presenter, offset.X, offset.Y, duration);
                affectedPresenters.Add(presenter);
            }

            if (trackForDrag)
            {
                var stalePresenters = new List<ContentPresenter>(_dragAvoidancePresenters);
                foreach (var presenter in stalePresenters)
                {
                    if (!affectedPresenters.Contains(presenter))
                    {
                        AnimatePresenterOffset(presenter, 0, 0, duration);
                    }
                }

                _dragAvoidancePresenters.Clear();
                foreach (var presenter in affectedPresenters)
                {
                    _dragAvoidancePresenters.Add(presenter);
                }
            }

            return affectedPresenters;
        }

        private void AnimateDragAvoidanceHome()
        {
            if (_dragAvoidancePresenters.Count == 0)
            {
                return;
            }

            foreach (var presenter in _dragAvoidancePresenters)
            {
                AnimatePresenterOffset(presenter, 0, 0, TimeSpan.FromMilliseconds(230));
            }

            _dragAvoidancePresenters.Clear();
        }

        private void AnimateResizeAvoidance(TileItemViewModel resizedTile, IReadOnlyDictionary<TileItemViewModel, Rect> beforeRects)
        {
            if (_isDraggingActive ||
                beforeRects.Count == 0 ||
                FindTilePresenter(resizedTile) is not ContentPresenter resizedPresenter ||
                !TryGetTileLayoutRectInView(resizedPresenter, resizedTile, out Rect resizedRect))
            {
                return;
            }

            bool hasPreviousResizedRect = beforeRects.TryGetValue(resizedTile, out Rect previousResizedRect);

            foreach (var presenter in EnumerateTilePresenters())
            {
                if (presenter.DataContext is not TileItemViewModel tile ||
                    ReferenceEquals(tile, resizedTile) ||
                    tile.IsDragging ||
                    !TryGetTileLayoutRectInView(presenter, tile, out Rect currentRect))
                {
                    continue;
                }

                bool wasNearby = false;
                Vector layoutDelta = new();
                if (beforeRects.TryGetValue(tile, out Rect previousRect))
                {
                    layoutDelta = previousRect.TopLeft - currentRect.TopLeft;
                    wasNearby = hasPreviousResizedRect &&
                        DistanceBetweenRects(previousResizedRect, previousRect) <= ResizeAvoidanceRadius;
                }

                bool isNearby = DistanceBetweenRects(resizedRect, currentRect) <= ResizeAvoidanceRadius;
                if (!wasNearby && !isNearby)
                {
                    continue;
                }

                Vector push = CalculateAvoidanceOffset(resizedRect, currentRect, ResizeAvoidanceRadius, ResizeAvoidanceMaxOffset);
                Vector initialOffset = new(
                    layoutDelta.X + push.X * 0.55,
                    layoutDelta.Y + push.Y * 0.55);

                if (Math.Abs(initialOffset.X) <= 0.1 && Math.Abs(initialOffset.Y) <= 0.1)
                {
                    continue;
                }

                SetPresenterOffset(presenter, initialOffset.X, initialOffset.Y);
                AnimatePresenterOffset(
                    presenter,
                    0,
                    0,
                    TimeSpan.FromMilliseconds(380),
                    () => new QuinticEase { EasingMode = EasingMode.EaseOut });
            }
        }

        private void CaptureVisibleTileLayoutRects(IDictionary<TileItemViewModel, Rect> target)
        {
            foreach (var presenter in EnumerateTilePresenters())
            {
                if (presenter.DataContext is TileItemViewModel tile &&
                    TryGetTileLayoutRectInView(presenter, tile, out Rect rect))
                {
                    target[tile] = rect;
                }
            }
        }

        private IEnumerable<ContentPresenter> EnumerateTilePresenters()
        {
            foreach (var presenter in FindVisualChildren<ContentPresenter>(TileGroupsItemsControl))
            {
                if (presenter.DataContext is TileItemViewModel)
                {
                    yield return presenter;
                }
            }
        }

        private ContentPresenter? FindTilePresenter(TileItemViewModel tile)
        {
            foreach (var presenter in EnumerateTilePresenters())
            {
                if (ReferenceEquals(presenter.DataContext, tile))
                {
                    return presenter;
                }
            }

            return null;
        }

        private bool TryGetTileLayoutRectInView(ContentPresenter presenter, TileItemViewModel tile, out Rect rect)
        {
            rect = Rect.Empty;

            var groupGrid = FindVisualAncestorByName<FrameworkElement>(presenter, "GroupGrid");
            if (groupGrid == null || groupGrid.ActualWidth <= 0 || groupGrid.ActualHeight <= 0)
            {
                return false;
            }

            Point groupOrigin;
            try
            {
                groupOrigin = groupGrid.TranslatePoint(new Point(0, 0), this);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            rect = new Rect(
                groupOrigin.X + tile.Column * TileCellSize,
                groupOrigin.Y + tile.Row * TileCellSize,
                Math.Max(TileCellSize, tile.ColumnSpan * TileCellSize),
                Math.Max(TileCellSize, tile.RowSpan * TileCellSize));

            return true;
        }

        private static void SetPresenterOffset(ContentPresenter presenter, double x, double y)
        {
            var transform = EnsurePresenterTransform(presenter);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = x;
            transform.Y = y;

            bool isAvoiding = Math.Abs(x) > 0.01 || Math.Abs(y) > 0.01;
            UpdateLockVisualState(presenter, isAvoiding);
        }

        private static void AnimatePresenterOffset(ContentPresenter presenter, double targetX, double targetY, TimeSpan duration)
        {
            AnimatePresenterOffset(
                presenter,
                targetX,
                targetY,
                duration,
                () => new QuinticEase { EasingMode = EasingMode.EaseOut });
        }

        private static void AnimatePresenterOffset(
            ContentPresenter presenter,
            double targetX,
            double targetY,
            TimeSpan duration,
            Func<EasingFunctionBase> createEasingFunction)
        {
            var transform = EnsurePresenterTransform(presenter);
            double fromX = transform.X;
            double fromY = transform.Y;

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = targetX;
            transform.Y = targetY;

            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation
                {
                    From = fromX,
                    To = targetX,
                    Duration = duration,
                    EasingFunction = createEasingFunction(),
                    FillBehavior = FillBehavior.Stop
                });

            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation
                {
                    From = fromY,
                    To = targetY,
                    EasingFunction = createEasingFunction(),
                    FillBehavior = FillBehavior.Stop
                });

            bool isAvoiding = Math.Abs(targetX) > 0.01 || Math.Abs(targetY) > 0.01;
            UpdateLockVisualState(presenter, isAvoiding);
        }

        private static void UpdateLockVisualState(ContentPresenter presenter, bool isAvoiding)
        {
            if (presenter.DataContext is TileItemViewModel tile)
            {
                var lockVisual = FindVisualChildByName<FrameworkElement>(presenter, "LockVisual");
                if (lockVisual != null)
                {
                    double targetOpacity = (tile.IsLocked && isAvoiding) ? 1.0 : 0.0;
                    if (Math.Abs(targetOpacity - lockVisual.Opacity) > 0.01 || lockVisual.HasAnimatedProperties)
                    {
                        lockVisual.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
                        {
                            To = targetOpacity,
                            Duration = TimeSpan.FromMilliseconds(isAvoiding ? 150 : 250)
                        });
                    }
                }
            }
        }

        private static Vector CalculateAvoidanceOffset(Rect sourceRect, Rect targetRect, double radius, double maxOffset)
        {
            double distance = DistanceBetweenRects(sourceRect, targetRect);
            if (distance >= radius)
            {
                return new Vector();
            }

            Point sourceCenter = GetRectCenter(sourceRect);
            Point targetCenter = GetRectCenter(targetRect);
            Vector direction = targetCenter - sourceCenter;
            if (direction.Length < 0.01)
            {
                direction = Math.Abs(targetRect.Left - sourceRect.Left) >= Math.Abs(targetRect.Top - sourceRect.Top)
                    ? new Vector(Math.Sign(targetRect.Left - sourceRect.Left), 0)
                    : new Vector(0, Math.Sign(targetRect.Top - sourceRect.Top));

                if (direction.Length < 0.01)
                {
                    direction = new Vector(0, 1);
                }
            }

            direction.Normalize();
            double closeness = Math.Clamp(1 - distance / radius, 0, 1);
            double strength = 1 - Math.Pow(1 - closeness, 3);
            double offset = maxOffset * strength;
            return new Vector(direction.X * offset, direction.Y * offset);
        }

        private static double DistanceBetweenRects(Rect sourceRect, Rect targetRect)
        {
            double gapX = Math.Max(0, Math.Max(sourceRect.Left - targetRect.Right, targetRect.Left - sourceRect.Right));
            double gapY = Math.Max(0, Math.Max(sourceRect.Top - targetRect.Bottom, targetRect.Top - sourceRect.Bottom));
            return Math.Sqrt(gapX * gapX + gapY * gapY);
        }

        private static Point GetRectCenter(Rect rect)
        {
            return new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        }

        private void AutoScrollWhileDragging(MouseEventArgs e)
        {
            Point mouseInViewer = e.GetPosition(TileScrollViewer);
            if (mouseInViewer.X < DragAutoScrollEdge)
            {
                TileScrollViewer.ScrollToHorizontalOffset(Math.Max(0, TileScrollViewer.HorizontalOffset - DragAutoScrollStep));
            }
            else if (mouseInViewer.X > TileScrollViewer.ActualWidth - DragAutoScrollEdge)
            {
                TileScrollViewer.ScrollToHorizontalOffset(Math.Min(TileScrollViewer.ScrollableWidth, TileScrollViewer.HorizontalOffset + DragAutoScrollStep));
            }
        }

        private bool TryGetDropCell(MouseEventArgs e, TileItemViewModel tile, out int groupIndex, out int column, out int row)
        {
            Point mouseInViewer = e.GetPosition(TileScrollViewer);
            var groupGrid = FindHoveredGroupGrid(mouseInViewer, out groupIndex);
            if (groupGrid == null)
            {
                if (DataContext is TilesPageViewModel vm)
                {
                    groupIndex = vm.TileGroups.Count;
                    column = 0;
                    row = 0;
                    return true;
                }
                
                column = -1;
                row = -1;
                return false;
            }

            Point mouseInGrid = TileScrollViewer.TranslatePoint(mouseInViewer, groupGrid);
            column = (int)Math.Floor(mouseInGrid.X / TileCellSize);
            row = (int)Math.Floor(mouseInGrid.Y / TileCellSize);
            column = Math.Clamp(column, 0, TilesPageViewModel.GroupColumns - tile.ColumnSpan);
            row = Math.Clamp(row, 0, TilesPageViewModel.GroupRows - tile.RowSpan);
            return true;
        }

        private FrameworkElement? FindHoveredGroupGrid(Point mousePosInViewer, out int groupIndex)
        {
            groupIndex = -1;

            for (int i = 0; i < TileGroupsItemsControl.Items.Count; i++)
            {
                if (TileGroupsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                {
                    continue;
                }

                Point relativeToContainer = TileScrollViewer.TranslatePoint(mousePosInViewer, container);
                if (relativeToContainer.X < -DropPadding ||
                    relativeToContainer.X > container.ActualWidth + DropPadding ||
                    relativeToContainer.Y < -DropPadding ||
                    relativeToContainer.Y > container.ActualHeight + DropPadding)
                {
                    continue;
                }

                var groupGrid = FindVisualChildByName<FrameworkElement>(container, "GroupGrid");
                if (groupGrid == null)
                {
                    continue;
                }

                Point relativeToGrid = TileScrollViewer.TranslatePoint(mousePosInViewer, groupGrid);
                if (relativeToGrid.X >= -DropPadding &&
                    relativeToGrid.X <= groupGrid.ActualWidth + DropPadding &&
                    relativeToGrid.Y >= -DropPadding &&
                    relativeToGrid.Y <= groupGrid.ActualHeight + DropPadding)
                {
                    groupIndex = i;
                    return groupGrid;
                }
            }

            return null;
        }

        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T namedChild && namedChild.Name == name)
                {
                    return namedChild;
                }

                var descendant = FindVisualChildByName<T>(child, name);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private static T? FindVisualAncestorByName<T>(DependencyObject child, string name) where T : FrameworkElement
        {
            DependencyObject? current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T element && element.Name == name)
                {
                    return element;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? parent = VisualTreeHelper.GetParent(child);
            if (parent == null)
            {
                return null;
            }

            return parent is T typedParent ? typedParent : FindVisualParent<T>(parent);
        }
    }

    public sealed class SizeToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is string size && parameter is string parameterSize && size == parameterSize;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool isChecked && isChecked && parameter is string parameterSize
                ? parameterSize
                : Binding.DoNothing;
        }
    }

    public sealed class TileImageBrushConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var imageSource = CreateImageSource(value);
            if (imageSource == null)
            {
                return null;
            }

            var brush = new ImageBrush(imageSource)
            {
                Stretch = Stretch.UniformToFill
            };
            brush.Freeze();
            return brush;
        }

        private static ImageSource? CreateImageSource(object value)
        {
            if (value is ImageSource imageSource)
            {
                return imageSource;
            }

            if (value is not string path || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var uri = Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri)
                    ? absoluteUri
                    : new Uri(Path.GetFullPath(path), UriKind.Absolute);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = uri;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
