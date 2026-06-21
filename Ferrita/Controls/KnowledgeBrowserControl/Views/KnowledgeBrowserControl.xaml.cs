using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ferrita.Controls.KnowledgeBrowserControl.ViewModels;

namespace Ferrita.Controls.KnowledgeBrowserControl.Views
{
    /// <summary>
    /// KnowledgeBrowserControl.xaml 的交互逻辑
    /// </summary>
    public partial class KnowledgeBrowserControl : UserControl
    {
        private KnowledgeBrowserControlViewModel ViewModel => (KnowledgeBrowserControlViewModel)DataContext;

        public KnowledgeBrowserControl()
        {
            InitializeComponent();
            DataContext = new KnowledgeBrowserControlViewModel();

            // 监听选中索引变化以更新标签页高亮
            Loaded += (_, _) => UpdateBrowseTabHighlight();
        }

        /// <summary>
        /// 处理浏览标签页点击事件，切换选中标签页
        /// </summary>
        private void BrowseTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is BrowseTabItem tab)
            {
                int index = ViewModel.BrowseTabs.IndexOf(tab);
                if (index >= 0)
                {
                    ViewModel.SelectedBrowseTabIndex = index;
                    UpdateBrowseTabHighlight();
                }
            }
        }

        /// <summary>
        /// 更新浏览标签页的高亮显示
        /// </summary>
        private void UpdateBrowseTabHighlight()
        {
            if (BrowseTabStrip == null) return;

            // 使用 Dispatcher 确保 UI 已更新
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var itemsControl = BrowseTabStrip;
                for (int i = 0; i < itemsControl.Items.Count; i++)
                {
                    var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                    if (container == null) continue;

                    var border = FindChild<System.Windows.Controls.Border>(container, "TabBorder");
                    if (border != null)
                    {
                        border.Background = i == ViewModel.SelectedBrowseTabIndex
                            ? new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF))
                            : Brushes.Transparent;
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>
        /// 在可视化树中查找指定名称的子元素
        /// </summary>
        private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && t.Name == name)
                    return t;

                var found = FindChild<T>(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
