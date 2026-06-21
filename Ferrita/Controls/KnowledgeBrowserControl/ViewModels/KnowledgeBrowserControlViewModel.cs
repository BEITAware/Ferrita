using System.Collections.ObjectModel;
using System.Windows.Input;
using Ferrita.Commands;
using Ferrita.Infrastructure.Mvvm;

namespace Ferrita.Controls.KnowledgeBrowserControl.ViewModels
{
    /// <summary>
    /// 知识浏览器控件的视图模型
    /// </summary>
    public sealed class KnowledgeBrowserControlViewModel : ObservableObject
    {
        private int _selectedSideTabIndex;
        private int _selectedBrowseTabIndex;
        private int _browseTabCounter = 1;

        public KnowledgeBrowserControlViewModel()
        {
            // 默认添加一个 Home 标签页
            BrowseTabs = new ObservableCollection<BrowseTabItem>
            {
                new BrowseTabItem { Title = "Home", Id = _browseTabCounter }
            };

            AddBrowseTabCommand = new RelayCommand(AddBrowseTab);
            CloseBrowseTabCommand = new RelayCommand<BrowseTabItem>(CloseBrowseTab);
        }

        /// <summary>
        /// 侧面 Tab 选中索引 (0=打开..., 1=文件, 2=浏览, 3=检索)
        /// </summary>
        public int SelectedSideTabIndex
        {
            get => _selectedSideTabIndex;
            set => SetProperty(ref _selectedSideTabIndex, value);
        }

        /// <summary>
        /// 浏览页面内的标签页集合
        /// </summary>
        public ObservableCollection<BrowseTabItem> BrowseTabs { get; }

        /// <summary>
        /// 浏览页面内选中的标签页索引
        /// </summary>
        public int SelectedBrowseTabIndex
        {
            get => _selectedBrowseTabIndex;
            set => SetProperty(ref _selectedBrowseTabIndex, value);
        }

        /// <summary>
        /// 添加浏览标签页命令
        /// </summary>
        public ICommand AddBrowseTabCommand { get; }

        /// <summary>
        /// 关闭浏览标签页命令
        /// </summary>
        public ICommand CloseBrowseTabCommand { get; }

        private void AddBrowseTab()
        {
            _browseTabCounter++;
            var newTab = new BrowseTabItem
            {
                Title = $"新标签页 {_browseTabCounter}",
                Id = _browseTabCounter
            };
            BrowseTabs.Add(newTab);
            SelectedBrowseTabIndex = BrowseTabs.Count - 1;
        }

        private void CloseBrowseTab(BrowseTabItem? tab)
        {
            if (tab == null || BrowseTabs.Count <= 1)
                return;

            int index = BrowseTabs.IndexOf(tab);
            BrowseTabs.Remove(tab);

            // 调整选中索引
            if (index >= BrowseTabs.Count)
                SelectedBrowseTabIndex = BrowseTabs.Count - 1;
            else if (index <= SelectedBrowseTabIndex && SelectedBrowseTabIndex > 0)
                SelectedBrowseTabIndex = SelectedBrowseTabIndex - 1;
        }
    }

    /// <summary>
    /// 浏览标签页数据项
    /// </summary>
    public sealed class BrowseTabItem : ObservableObject
    {
        private string _title = string.Empty;

        /// <summary>
        /// 标签页标题
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// 标签页唯一标识
        /// </summary>
        public int Id { get; set; }
    }
}
