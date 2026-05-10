using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Skyweaver.Controls.ChatSessionControl.Views
{
    public sealed class MarkdownContentControl : ContentControl
    {
        private const double TableMaxHeight = 360.0;
        private const double TableMinColumnWidth = 72.0;
        private const int StreamingRefreshThrottleMilliseconds = 60;

        private static readonly Brush HeadingForegroundBrush = CreateFrozenBrush(Color.FromRgb(0xBD, 0xEB, 0xFF));
        private static readonly Brush QuoteForegroundBrush = CreateFrozenBrush(Color.FromArgb(0xE0, 0xEA, 0xFD, 0xFF));
        private static readonly Brush QuoteBorderBrush = CreateFrozenBrush(Color.FromArgb(0x88, 0x7A, 0xD8, 0xF1));
        private static readonly Brush LinkForegroundBrush = CreateFrozenBrush(Color.FromRgb(0x8F, 0xD8, 0xFF));
        private static readonly Brush InlineCodeForegroundBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xF2, 0xCF));
        private static readonly Brush InlineCodeBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00));
        private static readonly Brush CodeBorderBrush = CreateFrozenBrush(Color.FromArgb(0x44, 0x96, 0xFC, 0xFF));
        private static readonly Brush CodeBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
        private static readonly Brush CodeForegroundBrush = CreateFrozenBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
        private static readonly Brush BadgeBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        private static readonly Brush BadgeBorderBrush = CreateFrozenBrush(Color.FromArgb(0x33, 0x88, 0xD7, 0xE8));
        private static readonly Brush MathBorderBrush = CreateFrozenBrush(Color.FromArgb(0x44, 0xCF, 0xB9, 0x7A));
        private static readonly Brush MathBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x18, 0x22, 0x1A, 0x10));
        private static readonly Brush MathForegroundBrush = CreateFrozenBrush(Color.FromRgb(0xF4, 0xDF, 0xB5));
        private static readonly Style TableCellTextStyle = CreateTableCellTextStyle();

        public static readonly DependencyProperty MarkdownTextProperty =
            DependencyProperty.Register(
                nameof(MarkdownText),
                typeof(string),
                typeof(MarkdownContentControl),
                new PropertyMetadata(string.Empty, OnMarkdownTextChanged));

        public static readonly DependencyProperty IsStreamingProperty =
            DependencyProperty.Register(
                nameof(IsStreaming),
                typeof(bool),
                typeof(MarkdownContentControl),
                new PropertyMetadata(false, OnIsStreamingChanged));

        public string MarkdownText
        {
            get => (string)GetValue(MarkdownTextProperty);
            set => SetValue(MarkdownTextProperty, value);
        }

        public bool IsStreaming
        {
            get => (bool)GetValue(IsStreamingProperty);
            set => SetValue(IsStreamingProperty, value);
        }

        private DateTime _lastStreamingRefreshUtc = DateTime.MinValue;
        private bool _refreshPending;

        public MarkdownContentControl()
        {
            Focusable = false;
            IsTabStop = false;
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            RefreshContent();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.Property == ForegroundProperty ||
                e.Property == FontSizeProperty ||
                e.Property == FontFamilyProperty ||
                e.Property == FontWeightProperty ||
                e.Property == FontStyleProperty)
            {
                RefreshContent();
            }
        }

        private static void OnMarkdownTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownContentControl)dependencyObject).ScheduleRefresh();
        }

        private static void OnIsStreamingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownContentControl)dependencyObject).ScheduleRefresh(force: true);
        }

        private void ScheduleRefresh(bool force = false)
        {
            if (!IsLoaded)
            {
                RefreshContent();
                return;
            }

            if (!IsStreaming)
            {
                RefreshContent();
                return;
            }

            var now = DateTime.UtcNow;
            var elapsedMilliseconds = (now - _lastStreamingRefreshUtc).TotalMilliseconds;
            if (force || elapsedMilliseconds >= StreamingRefreshThrottleMilliseconds)
            {
                _lastStreamingRefreshUtc = now;
                RefreshContent();
                return;
            }

            if (_refreshPending)
            {
                return;
            }

            _refreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshPending = false;
                _lastStreamingRefreshUtc = DateTime.UtcNow;
                RefreshContent();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshContent()
        {
            var blocks = MarkdownDocumentParser.Parse(MarkdownText, IsStreaming);
            if (blocks.Count == 0)
            {
                Content = null;
                return;
            }

            var root = new StackPanel();
            for (var index = 0; index < blocks.Count; index++)
            {
                var element = CreateBlockElement(blocks[index]);
                if (index < blocks.Count - 1)
                {
                    element.Margin = AppendBottomMargin(element.Margin, 8.0);
                }

                root.Children.Add(element);
            }

            Content = root;
        }

        private FrameworkElement CreateBlockElement(MarkdownBlock block)
        {
            return block switch
            {
                MarkdownHeadingBlock heading => CreateHeadingBlock(heading),
                MarkdownCodeBlock code => CreateCodeBlock(code),
                MarkdownQuoteBlock quote => CreateQuoteBlock(quote),
                MarkdownListBlock list => CreateListBlock(list),
                MarkdownMathBlock math => CreateMathBlock(math),
                MarkdownTableBlock table => CreateTableBlock(table),
                MarkdownParagraphBlock paragraph => CreateParagraphBlock(paragraph),
                _ => CreateParagraphBlock(new MarkdownParagraphBlock(Array.Empty<MarkdownInline>()))
            };
        }

        private TextBlock CreateParagraphBlock(MarkdownParagraphBlock block)
        {
            var textBlock = CreateBaseTextBlock();
            AddInlines(textBlock.Inlines, block.Inlines);
            return textBlock;
        }

        private TextBlock CreateHeadingBlock(MarkdownHeadingBlock block)
        {
            var textBlock = CreateBaseTextBlock();
            textBlock.Foreground = HeadingForegroundBrush;
            textBlock.FontWeight = FontWeights.SemiBold;
            textBlock.FontSize = ResolveHeadingFontSize(block.Level);
            AddInlines(textBlock.Inlines, block.Inlines);
            return textBlock;
        }

        private Border CreateQuoteBlock(MarkdownQuoteBlock block)
        {
            var textBlock = CreateBaseTextBlock();
            textBlock.Foreground = QuoteForegroundBrush;
            AddInlines(textBlock.Inlines, block.Inlines);

            return new Border
            {
                BorderBrush = QuoteBorderBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 2, 0, 2),
                Child = textBlock
            };
        }

        private FrameworkElement CreateListBlock(MarkdownListBlock block)
        {
            var root = new StackPanel();
            for (var index = 0; index < block.Items.Count; index++)
            {
                var item = block.Items[index];
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (index > 0)
                {
                    row.Margin = new Thickness(0, 4, 0, 0);
                }

                var marker = CreateBaseTextBlock();
                marker.Margin = new Thickness(0, 0, 10, 0);
                marker.FontWeight = FontWeights.SemiBold;
                marker.Text = item.Marker;

                var content = CreateBaseTextBlock();
                AddInlines(content.Inlines, item.Inlines);
                Grid.SetColumn(content, 1);

                row.Children.Add(marker);
                row.Children.Add(content);
                root.Children.Add(row);
            }

            return root;
        }

        private Border CreateCodeBlock(MarkdownCodeBlock block)
        {
            var stackPanel = new StackPanel();
            if (!string.IsNullOrWhiteSpace(block.Language))
            {
                var header = new DockPanel
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    LastChildFill = false
                };

                var badgeBorder = new Border
                {
                    Background = BadgeBackgroundBrush,
                    BorderBrush = BadgeBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = new TextBlock
                    {
                        Text = block.Language,
                        Foreground = CodeForegroundBrush,
                        FontSize = Math.Max(11.0, ResolveBaseFontSize() - 2.0)
                    }
                };

                DockPanel.SetDock(badgeBorder, Dock.Right);
                header.Children.Add(badgeBorder);
                stackPanel.Children.Add(header);
            }

            stackPanel.Children.Add(new TextBlock
            {
                Text = block.Content,
                Foreground = CodeForegroundBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(12.0, ResolveBaseFontSize() - 1.0),
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = CodeBackgroundBrush,
                BorderBrush = CodeBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = stackPanel
            };
        }

        private Border CreateMathBlock(MarkdownMathBlock block)
        {
            return new Border
            {
                Background = MathBackgroundBrush,
                BorderBrush = MathBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new TextBlock
                {
                    Text = $"\\[{block.Content}\\]",
                    Foreground = MathForegroundBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = Math.Max(12.0, ResolveBaseFontSize() - 0.5),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        private FrameworkElement CreateTableBlock(MarkdownTableBlock block)
        {
            var dataGrid = new DataGrid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                MaxHeight = TableMaxHeight,
                ColumnWidth = new DataGridLength(1.0, DataGridLengthUnitType.Star)
            };

            if (TryFindResource("TwilightBlue_DataGridStyle") is Style dataGridStyle)
            {
                dataGrid.Style = dataGridStyle;
            }

            var columnLookup = new Dictionary<DataGridColumn, int>();
            for (var columnIndex = 0; columnIndex < block.Columns.Count; columnIndex++)
            {
                var headerText = block.Columns[columnIndex].Header;
                if (string.IsNullOrWhiteSpace(headerText))
                {
                    headerText = $"Column {columnIndex + 1}";
                }

                var column = new DataGridTextColumn
                {
                    Header = headerText,
                    Binding = new Binding($"Cells[{columnIndex}]"),
                    ClipboardContentBinding = new Binding($"Cells[{columnIndex}]"),
                    ElementStyle = TableCellTextStyle,
                    MinWidth = TableMinColumnWidth
                };

                dataGrid.Columns.Add(column);
                columnLookup[column] = columnIndex;
            }

            dataGrid.ItemsSource = block.Rows.ToList();
            dataGrid.Sorting += (_, e) => ApplyTableSort(dataGrid, e, columnLookup);
            return dataGrid;
        }

        private void AddInlines(InlineCollection collection, IEnumerable<MarkdownInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case MarkdownTextInline text:
                        collection.Add(new Run(text.Text));
                        break;

                    case MarkdownStrongInline strong:
                    {
                        var bold = new Bold();
                        AddInlines(bold.Inlines, strong.Children);
                        collection.Add(bold);
                        break;
                    }

                    case MarkdownEmphasisInline emphasis:
                    {
                        var italic = new Italic();
                        AddInlines(italic.Inlines, emphasis.Children);
                        collection.Add(italic);
                        break;
                    }

                    case MarkdownCodeInline code:
                        collection.Add(CreateInlineCode(code.Content));
                        break;

                    case MarkdownLinkInline link:
                        collection.Add(CreateHyperlink(link));
                        break;

                    case MarkdownMathInline math:
                        collection.Add(CreateMathInline(math));
                        break;

                    case MarkdownLineBreakInline:
                        collection.Add(new LineBreak());
                        break;
                }
            }
        }

        private Inline CreateInlineCode(string code)
        {
            return new Run(code)
            {
                Background = InlineCodeBackgroundBrush,
                Foreground = InlineCodeForegroundBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(12.0, ResolveBaseFontSize() - 1.0)
            };
        }

        private Hyperlink CreateHyperlink(MarkdownLinkInline link)
        {
            var hyperlink = new Hyperlink
            {
                Foreground = LinkForegroundBrush,
                TextDecorations = TextDecorations.Underline
            };

            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var navigateUri))
            {
                hyperlink.NavigateUri = navigateUri;
            }

            ToolTipService.SetToolTip(hyperlink, link.Url);
            hyperlink.Click += (_, _) => TryOpenLink(link.Url);
            AddInlines(hyperlink.Inlines, link.Label);
            return hyperlink;
        }

        private Inline CreateMathInline(MarkdownMathInline math)
        {
            var delimiter = math.IsDisplayStyle ? ('[', ']') : ('(', ')');
            return new Run($"\\{delimiter.Item1}{math.Content}\\{delimiter.Item2}")
            {
                Foreground = MathForegroundBrush,
                FontFamily = new FontFamily("Consolas")
            };
        }

        private TextBlock CreateBaseTextBlock()
        {
            return new TextBlock
            {
                Foreground = ResolveForeground(),
                FontFamily = ResolveFontFamily(),
                FontSize = ResolveBaseFontSize(),
                FontWeight = FontWeight,
                FontStyle = FontStyle,
                TextWrapping = TextWrapping.Wrap
            };
        }

        private double ResolveBaseFontSize()
        {
            return FontSize > 0 ? FontSize : 14.0;
        }

        private double ResolveHeadingFontSize(int level)
        {
            var baseSize = ResolveBaseFontSize();
            return level switch
            {
                1 => baseSize + 8.0,
                2 => baseSize + 6.0,
                3 => baseSize + 4.0,
                4 => baseSize + 2.0,
                5 => baseSize + 1.0,
                _ => baseSize
            };
        }

        private Brush ResolveForeground()
        {
            return Foreground ?? Brushes.White;
        }

        private FontFamily ResolveFontFamily()
        {
            return FontFamily ?? new FontFamily("Segoe UI");
        }

        private static Thickness AppendBottomMargin(Thickness margin, double bottom)
        {
            return new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + bottom);
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static void TryOpenLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore navigation failures and keep the content readable.
            }
        }

        private static void ApplyTableSort(
            DataGrid dataGrid,
            DataGridSortingEventArgs e,
            IReadOnlyDictionary<DataGridColumn, int> columnLookup)
        {
            e.Handled = true;
            if (!columnLookup.TryGetValue(e.Column, out var columnIndex) ||
                dataGrid.ItemsSource is null)
            {
                return;
            }

            var direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            foreach (var column in dataGrid.Columns)
            {
                if (!ReferenceEquals(column, e.Column))
                {
                    column.SortDirection = null;
                }
            }

            e.Column.SortDirection = direction;
            var rows = dataGrid.ItemsSource.Cast<MarkdownTableRow>();
            dataGrid.ItemsSource = direction == ListSortDirection.Ascending
                ? rows.OrderBy(row => row.GetCell(columnIndex), StringComparer.CurrentCultureIgnoreCase).ToList()
                : rows.OrderByDescending(row => row.GetCell(columnIndex), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static Style CreateTableCellTextStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            return style;
        }
    }
}
