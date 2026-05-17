using System.Windows;
using System.Windows.Controls;
using Skyweaver.Controls.TextEditorControl.ViewModels;

namespace Skyweaver.Controls.TextEditorControl.Views
{
    public partial class TextEditorControl : UserControl
    {
        public TextEditorControl()
        {
            InitializeComponent();
            Loaded += HandleLoaded;
        }

        private TextEditorControlViewModel? ViewModel => DataContext as TextEditorControlViewModel;

        private void HandleLoaded(object sender, RoutedEventArgs e)
        {
            UpdateCaretPosition(PlainTextEditor);
        }

        private void PlainTextEditor_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                UpdateCaretPosition(textBox);
            }
        }

        private void PlainTextEditor_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
            {
                LineNumberEditor.ScrollToVerticalOffset(e.VerticalOffset);
            }
        }

        private void UpdateCaretPosition(TextBox textBox)
        {
            if (ViewModel == null)
            {
                return;
            }

            var caretIndex = Math.Clamp(textBox.CaretIndex, 0, textBox.Text.Length);
            var lineIndex = Math.Max(0, textBox.GetLineIndexFromCharacterIndex(caretIndex));
            var lineStartIndex = textBox.GetCharacterIndexFromLineIndex(lineIndex);
            var column = Math.Max(0, caretIndex - lineStartIndex);

            ViewModel.UpdateCaretPosition(lineIndex + 1, column + 1);
        }
    }
}
