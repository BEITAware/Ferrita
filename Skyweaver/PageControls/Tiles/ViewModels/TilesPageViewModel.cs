using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using Skyweaver.Commands;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Services.Directories;
using Skyweaver.Windows;

namespace Skyweaver.PageControls.Tiles.ViewModels
{
    public sealed class TileItemViewModel : ObservableObject
    {
        private string _name = string.Empty;
        private string _icon = string.Empty;
        private string _size = "1x1";
        private string? _customImageSource;
        private int _column;
        private int _row;
        private int _columnSpan = 1;
        private int _rowSpan = 1;
        private bool _isDragging;
        private int _groupIndex = -1;
        private bool _isLocked;

        public TileItemViewModel()
        {
            RunCommand = new RelayCommand(() =>
            {
                MessageBox.Show($"Running {Name}...", "Tile system", MessageBoxButton.OK, MessageBoxImage.Information);
            });

            RemoveCommand = new RelayCommand(() =>
            {
                RequestRemove?.Invoke(this, EventArgs.Empty);
            });

            SetSizeCommand = new RelayCommand<string>(size =>
            {
                if (!string.IsNullOrWhiteSpace(size))
                {
                    Size = size;
                }
            });

            CustomImageCommand = new RelayCommand(() =>
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
                    Title = "Choose tile image"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    CustomImageSource = openFileDialog.FileName;
                }
            });

            ToggleLockCommand = new RelayCommand(() =>
            {
                IsLocked = !IsLocked;
            });

            SetIconCommand = new RelayCommand<string>(iconPath =>
            {
                if (!string.IsNullOrWhiteSpace(iconPath))
                {
                    Icon = iconPath;
                }
            });
        }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    RequestLayoutUpdate?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Icon
        {
            get => _icon;
            set
            {
                if (SetProperty(ref _icon, value))
                {
                    OnPropertyChanged(nameof(IsImageIcon));
                    RequestLayoutUpdate?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Size
        {
            get => _size;
            set
            {
                string normalizedSize = NormalizeSize(value);
                int newColSpan = 1, newRowSpan = 1;
                switch (normalizedSize)
                {
                    case "1x2":
                        newColSpan = 2;
                        newRowSpan = 1;
                        break;
                    case "2x2":
                        newColSpan = 2;
                        newRowSpan = 2;
                        break;
                }

                if (Column + newColSpan > TilesPageViewModel.GroupColumns ||
                    Row + newRowSpan > TilesPageViewModel.GroupRows)
                {
                    return;
                }

                if (SetProperty(ref _size, normalizedSize))
                {
                    ApplySize();
                    RequestLayoutUpdate?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string? CustomImageSource
        {
            get => _customImageSource;
            set
            {
                if (SetProperty(ref _customImageSource, value))
                {
                    OnPropertyChanged(nameof(HasCustomImage));
                    RequestLayoutUpdate?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public int Column
        {
            get => _column;
            set => SetProperty(ref _column, value);
        }

        public int Row
        {
            get => _row;
            set => SetProperty(ref _row, value);
        }

        public int ColumnSpan
        {
            get => _columnSpan;
            private set => SetProperty(ref _columnSpan, value);
        }

        public int RowSpan
        {
            get => _rowSpan;
            private set => SetProperty(ref _rowSpan, value);
        }

        public bool IsDragging
        {
            get => _isDragging;
            set => SetProperty(ref _isDragging, value);
        }

        public int GroupIndex
        {
            get => _groupIndex;
            set => SetProperty(ref _groupIndex, value);
        }

        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (SetProperty(ref _isLocked, value))
                {
                    RequestLayoutUpdate?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool IsLarge => Size == "2x2";

        public bool IsNon2x2 => Size != "2x2";

        public bool HasCustomImage => IsLarge && !string.IsNullOrEmpty(CustomImageSource);

        public bool IsImageIcon
        {
            get
            {
                if (string.IsNullOrEmpty(Icon)) return false;
                return Icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       Icon.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       Icon.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       Icon.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) ||
                       Icon.Contains("/") || Icon.Contains("\\");
            }
        }

        public ICommand RunCommand { get; }

        public ICommand RemoveCommand { get; }

        public ICommand SetSizeCommand { get; }

        public ICommand CustomImageCommand { get; }

        public ICommand ToggleLockCommand { get; }

        public ICommand SetIconCommand { get; }

        public event EventHandler? RequestLayoutUpdate;

        public event EventHandler? RequestRemove;

        private static string NormalizeSize(string? size)
        {
            return size is "1x2" or "2x2" ? size : "1x1";
        }

        private void ApplySize()
        {
            switch (Size)
            {
                case "1x2":
                    ColumnSpan = 2;
                    RowSpan = 1;
                    break;
                case "2x2":
                    ColumnSpan = 2;
                    RowSpan = 2;
                    break;
                default:
                    ColumnSpan = 1;
                    RowSpan = 1;
                    break;
            }

            OnPropertyChanged(nameof(IsLarge));
            OnPropertyChanged(nameof(IsNon2x2));
            OnPropertyChanged(nameof(HasCustomImage));
        }
    }

    public sealed class TileGroupViewModel : ObservableObject
    {
        private string _name = string.Empty;
        private int _index;
        private int _dropColumn;
        private int _dropRow;
        private int _dropColumnSpan = 1;
        private int _dropRowSpan = 1;
        private bool _isDropPreviewVisible;

        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? DefaultName(Index) : value.Trim());
        }

        public int DropColumn
        {
            get => _dropColumn;
            private set => SetProperty(ref _dropColumn, value);
        }

        public int DropRow
        {
            get => _dropRow;
            private set => SetProperty(ref _dropRow, value);
        }

        public int DropColumnSpan
        {
            get => _dropColumnSpan;
            private set => SetProperty(ref _dropColumnSpan, value);
        }

        public int DropRowSpan
        {
            get => _dropRowSpan;
            private set => SetProperty(ref _dropRowSpan, value);
        }

        public bool IsDropPreviewVisible
        {
            get => _isDropPreviewVisible;
            private set => SetProperty(ref _isDropPreviewVisible, value);
        }

        public ObservableCollection<TileItemViewModel> Tiles { get; } = new ObservableCollection<TileItemViewModel>();

        public void ShowDropPreview(int column, int row, int columnSpan, int rowSpan)
        {
            DropColumn = Math.Clamp(column, 0, TilesPageViewModel.GroupColumns - columnSpan);
            DropRow = Math.Clamp(row, 0, TilesPageViewModel.GroupRows - rowSpan);
            DropColumnSpan = Math.Clamp(columnSpan, 1, TilesPageViewModel.GroupColumns);
            DropRowSpan = Math.Clamp(rowSpan, 1, TilesPageViewModel.GroupRows);
            IsDropPreviewVisible = true;
        }

        public void ClearDropPreview()
        {
            IsDropPreviewVisible = false;
        }

        internal static string DefaultName(int index)
        {
            string[] names = { "Essentials", "Productivity", "System Tools", "Entertainment", "Applications" };
            return index >= 0 && index < names.Length ? names[index] : $"Group {index + 1}";
        }
    }

    public sealed class TilesPageViewModel : ObservableObject
    {
        public const int GroupColumns = 6;
        public const int GroupRows = 4;
        public const int GroupCellCount = GroupColumns * GroupRows;

        private readonly List<string> _rememberedGroupNames = new();
        private bool _isPacking;
        private bool _isAnyTileDragging;

        public TilesPageViewModel()
        {
            InitializeDefaultTiles();
        }

        public ObservableCollection<TileItemViewModel> Tiles { get; } = new ObservableCollection<TileItemViewModel>();

        public ObservableCollection<TileGroupViewModel> TileGroups { get; } = new ObservableCollection<TileGroupViewModel>();

        public event EventHandler<TileLayoutTransitionEventArgs>? TileLayoutChanging;

        public event EventHandler<TileLayoutTransitionEventArgs>? TileLayoutChanged;

        public bool IsAnyTileDragging
        {
            get => _isAnyTileDragging;
            set => SetProperty(ref _isAnyTileDragging, value);
        }

        public void MoveTileToCell(TileItemViewModel tile, int targetGroupIndex, int targetColumn, int targetRow)
        {
            if (!Tiles.Contains(tile))
            {
                return;
            }

            Tiles.Remove(tile);
            Tiles.Add(tile);

            if (targetGroupIndex < 0 || targetGroupIndex >= TileGroups.Count)
            {
                targetGroupIndex = TileGroups.Count;
            }

            tile.GroupIndex = targetGroupIndex;
            tile.Column = Math.Clamp(targetColumn, 0, GroupColumns - tile.ColumnSpan);
            tile.Row = Math.Clamp(targetRow, 0, GroupRows - tile.RowSpan);

            PackTiles();
        }

        public int GetTileGroupIndex(TileItemViewModel tile)
        {
            for (int i = 0; i < TileGroups.Count; i++)
            {
                if (TileGroups[i].Tiles.Contains(tile))
                {
                    return i;
                }
            }

            return -1;
        }

        public void ShowDropPreview(TileItemViewModel tile, int targetGroupIndex, int targetColumn, int targetRow)
        {
            ClearDropPreview();

            if (targetGroupIndex < 0 || targetGroupIndex >= TileGroups.Count)
            {
                return;
            }

            TileGroups[targetGroupIndex].ShowDropPreview(
                targetColumn,
                targetRow,
                tile.ColumnSpan,
                tile.RowSpan);
        }

        public void ClearDropPreview()
        {
            foreach (var group in TileGroups)
            {
                group.ClearDropPreview();
            }
        }

        public void PackTiles()
        {
            if (_isPacking)
            {
                return;
            }

            try
            {
                _isPacking = true;
                SaveCurrentGroupNames();

                if (Tiles.Count == 0)
                {
                    TileGroups.Clear();
                    return;
                }

                var occupied = new Dictionary<int, bool[,]>();
                var unresolved = new List<TileItemViewModel>();
                var resolved = new List<TileItemViewModel>();

                // 第一遍遍历：处理已锁定的磁贴，确保它们保持当前位置
                for (int i = 0; i < Tiles.Count; i++)
                {
                    var tile = Tiles[i];
                    if (tile.IsLocked && tile.GroupIndex >= 0)
                    {
                        if (!occupied.ContainsKey(tile.GroupIndex))
                            occupied[tile.GroupIndex] = new bool[GroupColumns, GroupRows];

                        if (IsFree(occupied[tile.GroupIndex], tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan))
                        {
                            MarkOccupied(occupied[tile.GroupIndex], tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan);
                            resolved.Add(tile);
                        }
                        else
                        {
                            // 如果锁定的磁贴重叠，这在正常情况下不应发生。
                            // 但如果发生了，为了避免崩溃，我们将其视为未解决状态。
                            // 理想情况下，它应该保持有效的原始位置。
                            unresolved.Add(tile);
                        }
                    }
                }

                // 第二遍遍历：处理未锁定的磁贴
                for (int i = Tiles.Count - 1; i >= 0; i--)
                {
                    var tile = Tiles[i];
                    if (tile.IsLocked && resolved.Contains(tile))
                    {
                        continue;
                    }

                    if (tile.GroupIndex >= 0 && !tile.IsLocked)
                    {
                        if (!occupied.ContainsKey(tile.GroupIndex))
                            occupied[tile.GroupIndex] = new bool[GroupColumns, GroupRows];

                        if (IsFree(occupied[tile.GroupIndex], tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan))
                        {
                            MarkOccupied(occupied[tile.GroupIndex], tile.Column, tile.Row, tile.ColumnSpan, tile.RowSpan);
                            resolved.Add(tile);
                            continue;
                        }
                    }
                    if (!tile.IsLocked)
                    {
                        unresolved.Add(tile);
                    }
                }

                unresolved.Reverse();

                foreach (var tile in unresolved)
                {
                    int gIndex = Math.Max(0, tile.GroupIndex);
                    while (true)
                    {
                        if (!occupied.ContainsKey(gIndex))
                            occupied[gIndex] = new bool[GroupColumns, GroupRows];

                        if (TryFindSlot(occupied[gIndex], tile.ColumnSpan, tile.RowSpan, out int col, out int row))
                        {
                            tile.GroupIndex = gIndex;
                            tile.Column = col;
                            tile.Row = row;
                            MarkOccupied(occupied[gIndex], col, row, tile.ColumnSpan, tile.RowSpan);
                            break;
                        }
                        gIndex++;
                    }
                }

                int requiredGroupCount = 0;
                foreach (var tile in Tiles)
                {
                    if (tile.GroupIndex >= requiredGroupCount)
                    {
                        requiredGroupCount = tile.GroupIndex + 1;
                    }
                }

                EnsureGroupCount(requiredGroupCount);
                
                var groupedTiles = new List<TileItemViewModel>[requiredGroupCount];
                for (int i = 0; i < groupedTiles.Length; i++)
                {
                    groupedTiles[i] = new List<TileItemViewModel>();
                }

                foreach (var tile in Tiles)
                {
                    if (tile.GroupIndex >= 0 && tile.GroupIndex < requiredGroupCount)
                    {
                        groupedTiles[tile.GroupIndex].Add(tile);
                    }
                }

                for (int i = 0; i < requiredGroupCount; i++)
                {
                    var group = TileGroups[i];
                    group.Index = i;
                    SyncCollection(group.Tiles, groupedTiles[i]);
                }
            }
            finally
            {
                _isPacking = false;
                SaveTiles();
            }
        }

        private void InitializeDefaultTiles()
        {
            LoadTiles();
        }

        private void AddTile(TileItemViewModel tile)
        {
            tile.RequestLayoutUpdate += OnTileRequestLayoutUpdate;
            tile.RequestRemove += OnTileRequestRemove;
            Tiles.Add(tile);
        }

        private void OnTileRequestLayoutUpdate(object? sender, EventArgs e)
        {
            if (sender is TileItemViewModel tile)
            {
                TileLayoutChanging?.Invoke(this, new TileLayoutTransitionEventArgs(tile));
                
                if (Tiles.Contains(tile))
                {
                    Tiles.Remove(tile);
                    Tiles.Add(tile);
                }
                
                PackTiles();
                TileLayoutChanged?.Invoke(this, new TileLayoutTransitionEventArgs(tile));
                return;
            }

            PackTiles();
        }

        private void OnTileRequestRemove(object? sender, EventArgs e)
        {
            if (sender is not TileItemViewModel tile)
            {
                return;
            }

            tile.RequestLayoutUpdate -= OnTileRequestLayoutUpdate;
            tile.RequestRemove -= OnTileRequestRemove;
            Tiles.Remove(tile);
            PackTiles();
        }

        private string GetTilesXmlPath()
        {
            var tilesDir = Path.Combine(SkyweaverDirectoryRuntime.Instance.ConfigurationDirectoryPath, "Tiles");
            return Path.Combine(tilesDir, "Tile.xml");
        }

        public void LoadTiles()
        {
            try
            {
                var filePath = GetTilesXmlPath();
                if (!File.Exists(filePath))
                {
                    Tiles.Clear();
                    PackTiles();
                    return;
                }

                var doc = XDocument.Load(filePath);
                var root = doc.Root;
                if (root == null) return;

                Tiles.Clear();
                foreach (var tileEl in root.Elements("Tile"))
                {
                    var name = (string?)tileEl.Attribute("Name") ?? string.Empty;
                    var icon = (string?)tileEl.Attribute("Icon") ?? string.Empty;
                    var size = (string?)tileEl.Attribute("Size") ?? "1x1";
                    var col = int.Parse((string?)tileEl.Attribute("Column") ?? "0");
                    var row = int.Parse((string?)tileEl.Attribute("Row") ?? "0");
                    var groupIndex = int.Parse((string?)tileEl.Attribute("GroupIndex") ?? "-1");
                    var isLocked = bool.Parse((string?)tileEl.Attribute("IsLocked") ?? "false");
                    var customImageSource = (string?)tileEl.Attribute("CustomImageSource");

                    var tile = new TileItemViewModel
                    {
                        Name = name,
                        Icon = icon,
                        Size = size,
                        Column = col,
                        Row = row,
                        GroupIndex = groupIndex,
                        IsLocked = isLocked,
                        CustomImageSource = customImageSource
                    };

                    AddTile(tile);
                }

                PackTiles();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading tiles: {ex.Message}");
            }
        }

        private bool _isSaving;

        public void SaveTiles()
        {
            if (_isSaving) return;
            try
            {
                _isSaving = true;
                var filePath = GetTilesXmlPath();
                var tilesDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(tilesDir))
                {
                    Directory.CreateDirectory(tilesDir);
                }

                // 在持久化之前，如果自定义的图片不在Tiles文件夹，将其复制过去并更新路径
                if (!string.IsNullOrEmpty(tilesDir))
                {
                    foreach (var tile in Tiles)
                    {
                        if (!string.IsNullOrEmpty(tile.CustomImageSource))
                        {
                            try
                            {
                                var fileFullPath = Path.GetFullPath(tile.CustomImageSource);
                                var destFullPath = Path.GetFullPath(Path.Combine(tilesDir, Path.GetFileName(tile.CustomImageSource)));

                                if (!(Path.GetDirectoryName(fileFullPath)?.Equals(tilesDir, StringComparison.OrdinalIgnoreCase) ?? false))
                                {
                                    var baseName = Path.GetFileNameWithoutExtension(tile.CustomImageSource);
                                    var ext = Path.GetExtension(tile.CustomImageSource);
                                    var finalDest = destFullPath;
                                    int counter = 1;
                                    while (File.Exists(finalDest))
                                    {
                                        finalDest = Path.Combine(tilesDir, $"{baseName}_{counter}{ext}");
                                        counter++;
                                    }

                                    File.Copy(tile.CustomImageSource, finalDest, true);
                                    tile.CustomImageSource = finalDest;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error copying image: {ex.Message}");
                            }
                        }
                    }
                }

                var doc = new XDocument(
                    new XElement("Tiles",
                        Tiles.Select(t => new XElement("Tile",
                            new XAttribute("Name", t.Name),
                            new XAttribute("Icon", t.Icon ?? string.Empty),
                            new XAttribute("Size", t.Size ?? "1x1"),
                            new XAttribute("Column", t.Column),
                            new XAttribute("Row", t.Row),
                            new XAttribute("GroupIndex", t.GroupIndex),
                            new XAttribute("IsLocked", t.IsLocked),
                            t.CustomImageSource != null ? new XAttribute("CustomImageSource", t.CustomImageSource) : null
                        ))
                    )
                );

                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving tiles: {ex.Message}");
            }
            finally
            {
                _isSaving = false;
            }
        }

        private ICommand? _addTileCommand;
        public ICommand AddTileCommand => _addTileCommand ??= new RelayCommand(() =>
        {
            var owner = Application.Current?.MainWindow;
            IReadOnlyList<Skyweaver.Controls.ScheduledTasksControl.Models.ScheduledTask> allTasks;
            try
            {
                allTasks = new Skyweaver.Controls.ScheduledTasksControl.Services.ScheduledTasksRepository().LoadAll();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load scheduled tasks for tiles view: {ex.Message}");
                allTasks = Array.Empty<Skyweaver.Controls.ScheduledTasksControl.Models.ScheduledTask>();
            }
            var dialog = new AddTileUniversalDialog(allTasks);

            if (owner != null && owner != dialog)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() == true)
            {
                TileItemViewModel? newTile = null;
                if (dialog.IsLiveSessionSelected)
                {
                    newTile = new TileItemViewModel
                    {
                        Name = "Live Session",
                        Size = "1x2",
                        Icon = "pack://application:,,,/Resources/NewNodeGraphAlt.png",
                        GroupIndex = -1
                    };
                }
                else if (dialog.SelectedTask != null)
                {
                    newTile = new TileItemViewModel
                    {
                        Name = dialog.SelectedTask.Name,
                        Size = "1x1",
                        Icon = "pack://application:,,,/Resources/Default.png",
                        GroupIndex = -1
                    };
                }

                if (newTile != null)
                {
                    AddTile(newTile);
                    PackTiles();
                }
            }
        });



        private static bool TryFindSlot(bool[,] occupied, int width, int height, out int column, out int row)
        {
            for (int r = 0; r <= GroupRows - height; r++)
            {
                for (int c = 0; c <= GroupColumns - width; c++)
                {
                    if (IsFree(occupied, c, r, width, height))
                    {
                        column = c;
                        row = r;
                        return true;
                    }
                }
            }

            column = 0;
            row = 0;
            return false;
        }

        private static bool IsFree(bool[,] occupied, int column, int row, int width, int height)
        {
            if (column < 0 || row < 0 || column + width > GroupColumns || row + height > GroupRows)
            {
                return false;
            }

            for (int r = row; r < row + height; r++)
            {
                for (int c = column; c < column + width; c++)
                {
                    if (occupied[c, r])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void MarkOccupied(bool[,] occupied, int column, int row, int width, int height)
        {
            if (column < 0 || row < 0 || column + width > GroupColumns || row + height > GroupRows)
            {
                return;
            }

            for (int r = row; r < row + height; r++)
            {
                for (int c = column; c < column + width; c++)
                {
                    occupied[c, r] = true;
                }
            }
        }

        private void SaveCurrentGroupNames()
        {
            for (int i = 0; i < TileGroups.Count; i++)
            {
                RememberGroupName(i, TileGroups[i].Name);
            }
        }

        private void EnsureGroupCount(int requiredGroupCount)
        {
            while (TileGroups.Count < requiredGroupCount)
            {
                int index = TileGroups.Count;
                var group = new TileGroupViewModel
                {
                    Index = index,
                    Name = GetRememberedGroupName(index)
                };

                group.PropertyChanged += OnGroupPropertyChanged;
                TileGroups.Add(group);
            }

            while (TileGroups.Count > requiredGroupCount)
            {
                var group = TileGroups[^1];
                RememberGroupName(TileGroups.Count - 1, group.Name);
                group.PropertyChanged -= OnGroupPropertyChanged;
                TileGroups.RemoveAt(TileGroups.Count - 1);
            }

            for (int i = 0; i < TileGroups.Count; i++)
            {
                TileGroups[i].Index = i;
            }
        }

        private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TileGroupViewModel.Name) && sender is TileGroupViewModel group)
            {
                RememberGroupName(group.Index, group.Name);
            }
        }

        private string GetRememberedGroupName(int index)
        {
            return index >= 0 && index < _rememberedGroupNames.Count && !string.IsNullOrWhiteSpace(_rememberedGroupNames[index])
                ? _rememberedGroupNames[index]
                : TileGroupViewModel.DefaultName(index);
        }

        private void RememberGroupName(int index, string name)
        {
            if (index < 0)
            {
                return;
            }

            while (_rememberedGroupNames.Count <= index)
            {
                _rememberedGroupNames.Add(TileGroupViewModel.DefaultName(_rememberedGroupNames.Count));
            }

            _rememberedGroupNames[index] = string.IsNullOrWhiteSpace(name)
                ? TileGroupViewModel.DefaultName(index)
                : name.Trim();
        }

        private static void SyncCollection(ObservableCollection<TileItemViewModel> collection, IReadOnlyList<TileItemViewModel> desired)
        {
            var desiredSet = new HashSet<TileItemViewModel>(desired);
            for (int i = collection.Count - 1; i >= 0; i--)
            {
                if (!desiredSet.Contains(collection[i]))
                {
                    collection.RemoveAt(i);
                }
            }

            for (int i = 0; i < desired.Count; i++)
            {
                if (i < collection.Count && ReferenceEquals(collection[i], desired[i]))
                {
                    continue;
                }

                int existingIndex = collection.IndexOf(desired[i]);
                if (existingIndex >= 0)
                {
                    collection.Move(existingIndex, i);
                }
                else if (i < collection.Count)
                {
                    collection.Insert(i, desired[i]);
                }
                else
                {
                    collection.Add(desired[i]);
                }
            }

            while (collection.Count > desired.Count)
            {
                collection.RemoveAt(collection.Count - 1);
            }
        }

    }

    public sealed class TileLayoutTransitionEventArgs : EventArgs
    {
        public TileLayoutTransitionEventArgs(TileItemViewModel tile)
        {
            Tile = tile;
        }

        public TileItemViewModel Tile { get; }
    }
}
