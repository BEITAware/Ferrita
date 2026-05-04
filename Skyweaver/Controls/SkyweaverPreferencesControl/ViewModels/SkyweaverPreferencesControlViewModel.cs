using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using Skyweaver.Commands;
using Skyweaver.Controls.SkyweaverPreferencesControl.Models;
using Skyweaver.Controls.SkyweaverPreferencesControl.Services;
using Skyweaver.Infrastructure.Mvvm;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public sealed class SkyweaverPreferencesControlViewModel : ObservableObject
    {
        private readonly Dictionary<string, UserControl> _viewCache = new(StringComparer.Ordinal);
        private SelectablePreferencePageViewModel? _selectedPageViewModel;
        private UserControl? _currentView;
        private string? _currentPageName;
        private string? _currentPageDescription;

        public SkyweaverPreferencesControlViewModel()
        {
            SkyweaverPreferencesRegistration.EnsureRegistered();

            var modelGroups = PreferenceRegistry.Instance.Groups;
            Groups = new ObservableCollection<PreferenceGroupViewModel>(
                modelGroups.Select(group => new PreferenceGroupViewModel(group)));

            modelGroups.CollectionChanged += (_, args) =>
            {
                if (args.NewItems != null)
                {
                    foreach (PreferenceGroup group in args.NewItems)
                    {
                        Groups.Add(new PreferenceGroupViewModel(group));
                    }
                }

                if (args.OldItems == null)
                {
                    return;
                }

                foreach (PreferenceGroup group in args.OldItems)
                {
                    var existing = Groups.FirstOrDefault(candidate => ReferenceEquals(candidate.Group, group));
                    if (existing != null)
                    {
                        Groups.Remove(existing);
                    }
                }
            };

            SelectPageCommand = new RelayCommand<SelectablePreferencePageViewModel>(
                page => SelectedPageViewModel = page,
                page => page is not null);
            ReservedPrimaryActionCommand = new RelayCommand(() => { }, () => false);
            ReservedSecondaryActionCommand = new RelayCommand(() => { }, () => false);
        }

        public string Title { get; } = "Skyweaver Preferences";

        public string Description { get; } = "A Cascade-style registration shell for global preferences, workspace layout, and system defaults.";

        public string Hint { get; } = "This panel currently copies architecture and styling only. Real settings repositories, validation, and save flows can be plugged in page by page later.";

        public ObservableCollection<PreferenceGroupViewModel> Groups { get; }

        public ICommand SelectPageCommand { get; }

        public ICommand ReservedPrimaryActionCommand { get; }

        public ICommand ReservedSecondaryActionCommand { get; }

        public SelectablePreferencePageViewModel? SelectedPageViewModel
        {
            get => _selectedPageViewModel;
            set
            {
                if (!SetProperty(ref _selectedPageViewModel, value))
                {
                    return;
                }

                foreach (var group in Groups)
                {
                    foreach (var page in group.Pages)
                    {
                        page.IsSelected = ReferenceEquals(page, value);
                    }
                }

                if (value == null)
                {
                    CurrentView = null;
                    CurrentPageName = null;
                    CurrentPageDescription = null;
                    return;
                }

                ActivatePage(value.Id);
            }
        }

        public UserControl? CurrentView
        {
            get => _currentView;
            private set => SetProperty(ref _currentView, value);
        }

        public string? CurrentPageName
        {
            get => _currentPageName;
            private set => SetProperty(ref _currentPageName, value);
        }

        public string? CurrentPageDescription
        {
            get => _currentPageDescription;
            private set => SetProperty(ref _currentPageDescription, value);
        }

        private void ActivatePage(string pageId)
        {
            var pageInfo = PreferenceRegistry.Instance.GetPageInfo(pageId);
            if (pageInfo == null)
            {
                CurrentView = null;
                CurrentPageName = null;
                CurrentPageDescription = null;
                return;
            }

            CurrentPageName = pageInfo.DisplayName;
            CurrentPageDescription = pageInfo.Description;

            if (_viewCache.TryGetValue(pageId, out var cachedView))
            {
                CurrentView = cachedView;
                return;
            }

            var view = CreateView(pageInfo);
            if (view == null)
            {
                CurrentView = null;
                return;
            }

            _viewCache[pageId] = view;
            CurrentView = view;
        }

        private static UserControl? CreateView(PreferencePageInfo pageInfo)
        {
            if (pageInfo.ViewType == null)
            {
                return null;
            }

            try
            {
                if (Activator.CreateInstance(pageInfo.ViewType) is not UserControl view)
                {
                    return null;
                }

                if (pageInfo.ViewModelType != null)
                {
                    view.DataContext = Activator.CreateInstance(pageInfo.ViewModelType);
                }

                return view;
            }
            catch
            {
                return null;
            }
        }
    }
}
