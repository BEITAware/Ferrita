namespace Skyweaver.Controls.ChatSessionControl.Models
{
    public enum ChatToolOutputDiffLineKind
    {
        Anchor = 0,
        Added = 1,
        Removed = 2,
        Separator = 3
    }

    public sealed class ChatToolOutputDiffLineModel
    {
        public ChatToolOutputDiffLineModel(
            string lineNumberText,
            string marker,
            string text,
            ChatToolOutputDiffLineKind kind)
        {
            LineNumberText = lineNumberText ?? string.Empty;
            Marker = marker ?? string.Empty;
            Text = text ?? string.Empty;
            Kind = kind;
        }

        public string LineNumberText { get; }

        public string Marker { get; }

        public string Text { get; }

        public ChatToolOutputDiffLineKind Kind { get; }

        public string KindName => Kind.ToString();

        public bool IsSeparator => Kind == ChatToolOutputDiffLineKind.Separator;
    }
}
