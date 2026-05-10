namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    public enum LanguageModelChatRole
    {
        System = 0,
        User = 1,
        Assistant = 2,
        Tool = 3
    }

    public enum LanguageModelChatContentBlockKind
    {
        Text = 0,
        Image = 1,
        Audio = 2,
        HostPreservedContent = 3
    }

    public sealed class LanguageModelChatContentBlock
    {
        public LanguageModelChatContentBlock(
            LanguageModelChatContentBlockKind kind,
            string content,
            string? mediaType = null,
            string? resourcePath = null,
            byte[]? data = null)
        {
            Kind = kind;
            Content = content ?? string.Empty;
            MediaType = string.IsNullOrWhiteSpace(mediaType) ? null : mediaType.Trim();
            ResourcePath = string.IsNullOrWhiteSpace(resourcePath) ? null : resourcePath.Trim();
            Data = data == null || data.Length == 0 ? null : data.ToArray();
        }

        public LanguageModelChatContentBlockKind Kind { get; }

        public string Content { get; }

        public string? MediaType { get; }

        public string? ResourcePath { get; }

        public byte[]? Data { get; }

        public bool IsTextLike => Kind is LanguageModelChatContentBlockKind.Text
            or LanguageModelChatContentBlockKind.HostPreservedContent;

        public LanguageModelChatContentBlock Clone()
        {
            return new LanguageModelChatContentBlock(
                Kind,
                Content,
                MediaType,
                ResourcePath,
                Data);
        }

        public static LanguageModelChatContentBlock CreateText(string text)
        {
            return new LanguageModelChatContentBlock(LanguageModelChatContentBlockKind.Text, text ?? string.Empty);
        }

        public static LanguageModelChatContentBlock CreateImage(
            string path,
            string? mediaType = null,
            byte[]? data = null)
        {
            return new LanguageModelChatContentBlock(
                LanguageModelChatContentBlockKind.Image,
                path ?? string.Empty,
                mediaType,
                path,
                data);
        }

        public static LanguageModelChatContentBlock CreateAudio(
            string path,
            string? mediaType = null,
            byte[]? data = null)
        {
            return new LanguageModelChatContentBlock(
                LanguageModelChatContentBlockKind.Audio,
                path ?? string.Empty,
                mediaType,
                path,
                data);
        }

        public static LanguageModelChatContentBlock CreateHostPreservedContent(string content)
        {
            return new LanguageModelChatContentBlock(
                LanguageModelChatContentBlockKind.HostPreservedContent,
                content ?? string.Empty);
        }
    }

    public sealed class LanguageModelChatMessage
    {
        public LanguageModelChatMessage(LanguageModelChatRole role, string content)
            : this(role, CreateTextBlocks(content))
        {
        }

        public LanguageModelChatMessage(
            LanguageModelChatRole role,
            IEnumerable<LanguageModelChatContentBlock> contentBlocks)
        {
            Role = role;
            ContentBlocks = NormalizeContentBlocks(contentBlocks);
            Content = BuildTextProjection(ContentBlocks);
        }

        public LanguageModelChatRole Role { get; }

        public string Content { get; }

        public IReadOnlyList<LanguageModelChatContentBlock> ContentBlocks { get; }

        public string? AuthorName { get; init; }

        public bool IsHostInjectedTail { get; init; }

        public LanguageModelChatMessage Clone()
        {
            return new LanguageModelChatMessage(
                Role,
                ContentBlocks.Select(block => block.Clone()).ToArray())
            {
                AuthorName = AuthorName,
                IsHostInjectedTail = IsHostInjectedTail
            };
        }

        private static IReadOnlyList<LanguageModelChatContentBlock> CreateTextBlocks(string? content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? Array.Empty<LanguageModelChatContentBlock>()
                : [LanguageModelChatContentBlock.CreateText(content)];
        }

        private static IReadOnlyList<LanguageModelChatContentBlock> NormalizeContentBlocks(
            IEnumerable<LanguageModelChatContentBlock>? contentBlocks)
        {
            if (contentBlocks == null)
            {
                return Array.Empty<LanguageModelChatContentBlock>();
            }

            return contentBlocks
                .Where(block => block != null)
                .Select(block => block.Clone())
                .Where(block =>
                    !string.IsNullOrWhiteSpace(block.Content) ||
                    !string.IsNullOrWhiteSpace(block.ResourcePath) ||
                    block.Data?.Length > 0)
                .ToArray();
        }

        private static string BuildTextProjection(IReadOnlyList<LanguageModelChatContentBlock> contentBlocks)
        {
            if (contentBlocks.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                    Environment.NewLine + Environment.NewLine,
                    contentBlocks
                        .Select(BuildBlockTextProjection)
                        .Where(text => !string.IsNullOrWhiteSpace(text)))
                .Trim();
        }

        private static string BuildBlockTextProjection(LanguageModelChatContentBlock block)
        {
            return block.Kind switch
            {
                LanguageModelChatContentBlockKind.Text => block.Content,
                LanguageModelChatContentBlockKind.Image => BuildPreservedResourceXml("Image", block.ResourcePath ?? block.Content),
                LanguageModelChatContentBlockKind.Audio => BuildPreservedResourceXml("Audio", block.ResourcePath ?? block.Content),
                LanguageModelChatContentBlockKind.HostPreservedContent => block.Content,
                _ => block.Content
            };
        }

        private static string BuildPreservedResourceXml(string elementName, string? path)
        {
            var normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
            if (normalizedPath.Length == 0)
            {
                return string.Empty;
            }

            return $"<SkyweaverPreservedContent><{elementName} Path=\"{System.Security.SecurityElement.Escape(normalizedPath)}\" /></SkyweaverPreservedContent>";
        }
    }

    public sealed class LanguageModelChatResponse
    {
        public string Text { get; init; } = string.Empty;

        public string ReasoningText { get; init; } = string.Empty;

        public string? ModelId { get; init; }
    }

    public sealed class LanguageModelStreamingContentDebugItem
    {
        public string ContentType { get; init; } = string.Empty;

        public string? Text { get; init; }

        public string? Summary { get; init; }

        public string? RawRepresentationType { get; init; }

        public string? RawRepresentationSummary { get; init; }

        public IReadOnlyDictionary<string, object?> AdditionalProperties { get; init; } =
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class LanguageModelStreamingChatUpdate
    {
        public string TextDelta { get; init; } = string.Empty;

        public string ReasoningTextDelta { get; init; } = string.Empty;

        public string? ModelId { get; init; }

        public string? RawText { get; init; }

        public bool WasTextSanitized { get; init; }

        public string? Role { get; init; }

        public string? AuthorName { get; init; }

        public string? FinishReason { get; init; }

        public string? ResponseId { get; init; }

        public string? MessageId { get; init; }

        public string? ConversationId { get; init; }

        public DateTimeOffset? CreatedAt { get; init; }

        public string? ContinuationToken { get; init; }

        public string? RawRepresentationType { get; init; }

        public string? RawRepresentationSummary { get; init; }

        public IReadOnlyDictionary<string, object?> AdditionalProperties { get; init; } =
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<LanguageModelStreamingContentDebugItem> Contents { get; init; } =
            Array.Empty<LanguageModelStreamingContentDebugItem>();
    }
}
