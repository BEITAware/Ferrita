using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfImage = System.Windows.Controls.Image;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Skyweaver.Services.Daemon
{
    public sealed class TrayIconService : IDisposable
    {
        private static readonly Lazy<TrayIconService> LazyInstance =
            new(() => new TrayIconService());

        public static TrayIconService Instance => LazyInstance.Value;

        private Forms.NotifyIcon? _notifyIcon;
        private Drawing.Icon? _trayIcon;
        private ContextMenu? _trayContextMenu;
        private Action? _onOpenOrFocusRequested;
        private Action? _onShutdownRequested;
        private bool _isInitialized;
        private bool _isDisposed;

        private TrayIconService()
        {
        }

        public void Initialize(Action onOpenOrFocusRequested, Action onShutdownRequested)
        {
            if (_isInitialized)
            {
                return;
            }

            _onOpenOrFocusRequested = onOpenOrFocusRequested;
            _onShutdownRequested = onShutdownRequested;

            _trayIcon = LoadTrayIcon();
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = "Skyweaver",
                Visible = true
            };

            _notifyIcon.MouseUp += OnNotifyIconMouseUp;
            _isInitialized = true;
        }

        private void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                if (_onOpenOrFocusRequested != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(_onOpenOrFocusRequested);
                }
                return;
            }

            if (e.Button == Forms.MouseButtons.Right)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ShowTrayMenu));
            }
        }

        private void ShowTrayMenu()
        {
            _trayContextMenu ??= BuildTrayMenu();
            _trayContextMenu.IsOpen = false;
            _trayContextMenu.Placement = PlacementMode.MousePoint;
            _trayContextMenu.IsOpen = true;
        }

        private ContextMenu BuildTrayMenu()
        {
            var menu = new ContextMenu
            {
                Placement = PlacementMode.MousePoint,
                StaysOpen = false
            };

            if (Application.Current.TryFindResource("AnimatedContextMenuStyle") is Style menuStyle)
            {
                menu.Style = menuStyle;
            }

            menu.Items.Add(CreateMenuItem(
                "打开 Skyweaver",
                "打开或聚焦主窗口",
                "pack://application:,,,/Resources/SkyweaverAppLogo.png",
                (_, _) =>
                {
                    menu.IsOpen = false;
                    _onOpenOrFocusRequested?.Invoke();
                }));

            menu.Items.Add(CreateMenuItem(
                "关闭应用程序",
                "退出并关闭 Skyweaver",
                "pack://application:,,,/Resources/CrossMark.png",
                (_, _) =>
                {
                    menu.IsOpen = false;
                    _onShutdownRequested?.Invoke();
                }));

            return menu;
        }

        private static MenuItem CreateMenuItem(
            string title,
            string description,
            string iconUri,
            RoutedEventHandler clickHandler)
        {
            var item = new MenuItem
            {
                Header = CreateMenuHeader(title, description, iconUri)
            };

            if (Application.Current.TryFindResource("AnimatedMenuItemStyle") is Style itemStyle)
            {
                item.Style = itemStyle;
            }

            item.Click += clickHandler;
            return item;
        }

        private static Grid CreateMenuHeader(string title, string description, string iconUri)
        {
            var grid = new Grid
            {
                Width = 248,
                Margin = new Thickness(0, 2, 0, 2)
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new WpfImage
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2, 0, 8, 0),
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri(iconUri, UriKind.Absolute))
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textStack = new StackPanel
            {
                Orientation = WpfOrientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfBrushes.White
            });
            textStack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            return grid;
        }

        private static Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var resource = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Resources/Skyweaver.ico", UriKind.Absolute));
                if (resource?.Stream != null)
                {
                    using var stream = resource.Stream;
                    return new Drawing.Icon(stream);
                }
            }
            catch
            {
                // Fall back to default
            }

            return Drawing.SystemIcons.Application;
        }

        public void SetVisible(bool visible)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = visible;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _trayContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            _trayIcon?.Dispose();
        }
    }
}
