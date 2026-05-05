using System.Collections.ObjectModel;
using System.Windows;
using System.Xml.Linq;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Controls.ChatSessionControl.Models
{
    public class ChatMessagePartModel : ObservableObject
    {
        private ChatMessagePartType _partType;
        private string _content;
        private string? _title;
        private string? _language;
        private string? _badgeText;
        private string? _toolCallId;
        private string? _callerAgentId;
        private string? _resourcePath;
        private string? _presentationKind;
        private string _toolResultContent;
        private string? _toolResultPresentationKind;
        private bool _isStreaming;
        private bool _isUserVisible;
        private bool _isCollapsible;
        private bool _isExpanded;
        private SkyweaverToolInvocationPresentationState? _toolPresentationState;
        private FrameworkElement? _toolPresentationView;

        public ChatMessagePartType PartType
        {
            get => _partType;
            set
            {
                if (SetProperty(ref _partType, value))
                {
                    RebuildDerivedPresentationState();
                }
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (SetProperty(ref _content, value))
                {
                    if (!IsStreaming)
                    {
                        RebuildDerivedPresentationState();
                    }
                }
            }
        }

        public string? BadgeText
        {
            get => _badgeText;
            set => SetProperty(ref _badgeText, value);
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (SetProperty(ref _isStreaming, value) && !value)
                {
                    RebuildDerivedPresentationState();
                }
            }
        }

        public bool IsUserVisible
        {
            get => _isUserVisible;
            set => SetProperty(ref _isUserVisible, value);
        }

        public bool IsCollapsible
        {
            get => _isCollapsible;
            set => SetProperty(ref _isCollapsible, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public string? Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string? Language
        {
            get => _language;
            set => SetProperty(ref _language, value);
        }

        public string? ToolCallId
        {
            get => _toolCallId;
            set => SetProperty(ref _toolCallId, NormalizeMetadataValue(value));
        }

        public string? CallerAgentId
        {
            get => _callerAgentId;
            set => SetProperty(ref _callerAgentId, NormalizeMetadataValue(value));
        }

        public string? ResourcePath
        {
            get => _resourcePath;
            set => SetProperty(ref _resourcePath, NormalizeMetadataValue(value));
        }

        public string? PresentationKind
        {
            get => _presentationKind;
            set
            {
                if (SetProperty(ref _presentationKind, NormalizeMetadataValue(value)) && !IsStreaming)
                {
                    RebuildDerivedPresentationState();
                }
            }
        }

        public string ToolResultContent
        {
            get => _toolResultContent;
            set => SetProperty(ref _toolResultContent, value ?? string.Empty);
        }

        public string? ToolResultPresentationKind
        {
            get => _toolResultPresentationKind;
            set => SetProperty(ref _toolResultPresentationKind, NormalizeMetadataValue(value));
        }

        public bool HasToolResult => !string.IsNullOrWhiteSpace(ToolResultContent);

        public ObservableCollection<ChatStructuredXmlNodeModel> StructuredXmlNodes { get; } = new();

        public bool HasStructuredXmlNodes => StructuredXmlNodes.Count > 0;

        public ObservableCollection<ChatToolOutputDiffLineModel> ToolOutputDiffLines { get; } = new();

        public bool HasToolOutputDiffLines => ToolOutputDiffLines.Count > 0;

        public SkyweaverToolInvocationPresentationState? ToolPresentationState
        {
            get => _toolPresentationState;
            private set => SetProperty(ref _toolPresentationState, value);
        }

        public FrameworkElement? ToolPresentationView
        {
            get => _toolPresentationView;
            private set => SetProperty(ref _toolPresentationView, value);
        }

        public ChatMessagePartModel(
            ChatMessagePartType partType,
            string content,
            string? title = null,
            string? language = null,
            string? badgeText = null,
            bool isStreaming = false,
            string? toolCallId = null,
            string? callerAgentId = null,
            string? resourcePath = null,
            bool isUserVisible = true,
            bool isCollapsible = true,
            bool isExpanded = false)
        {
            _partType = partType;
            _content = content;
            _title = title;
            _language = language;
            _badgeText = badgeText;
            _toolResultContent = string.Empty;
            _isStreaming = isStreaming;
            _isUserVisible = isUserVisible;
            _isCollapsible = isCollapsible;
            _isExpanded = isExpanded;
            _toolCallId = NormalizeMetadataValue(toolCallId);
            _callerAgentId = NormalizeMetadataValue(callerAgentId);
            _resourcePath = NormalizeMetadataValue(resourcePath);
            StructuredXmlNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStructuredXmlNodes));
            ToolOutputDiffLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasToolOutputDiffLines));
            if (!_isStreaming)
            {
                RebuildDerivedPresentationState();
            }
        }

        public static ChatMessagePartModel CreateText(string content, string? title = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.Text, content, title);
        }

        public static ChatMessagePartModel CreateCode(string content, string? title = null, string? language = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.Code, content, title, language);
        }

        public static ChatMessagePartModel CreateStatus(string content, string? title = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.Status, content, title);
        }

        public static ChatMessagePartModel CreatePlaceholder(string content, string? title = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.Placeholder, content, title);
        }

        public static ChatMessagePartModel CreateTool(string content, string? title = null)
        {
            return CreateToolOutput(content, title);
        }

        public static ChatMessagePartModel CreateToolCall(string content, string? title = null, bool isStreaming = false)
        {
            return new ChatMessagePartModel(ChatMessagePartType.ToolCall, content, title, badgeText: "工具调用", isStreaming: isStreaming);
        }

        public static ChatMessagePartModel CreateToolOutput(
            string content,
            string? title = null,
            bool isStreaming = false,
            bool isUserVisible = false)
        {
            return new ChatMessagePartModel(
                ChatMessagePartType.ToolOutput,
                content,
                title,
                badgeText: "工具输出",
                isStreaming: isStreaming,
                isUserVisible: isUserVisible);
        }

        public static ChatMessagePartModel CreateStructuredXml(string xmlText, string? title = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.StructuredXml, xmlText, title, badgeText: "XML");
        }

        public static ChatMessagePartModel CreateImage(string path, string? title = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.Image, path, title, badgeText: "图片", resourcePath: path);
        }

        public static ChatMessagePartModel CreateAudio(string path, string? title = null)
        {
            return new ChatMessagePartModel(ChatMessagePartType.Audio, path, title, badgeText: "音频", resourcePath: path);
        }

        public static ChatMessagePartModel CreateReasoning(
            string content,
            string? title = null,
            bool isStreaming = false,
            bool isCollapsible = true,
            bool isExpanded = false)
        {
            return new ChatMessagePartModel(
                ChatMessagePartType.Reasoning,
                content,
                title ?? "推理过程",
                badgeText: "推理过程",
                isStreaming: isStreaming,
                isCollapsible: isCollapsible,
                isExpanded: isExpanded);
        }

        public void AttachToolPresentation(
            SkyweaverToolInvocationPresentationState state,
            FrameworkElement view)
        {
            ToolPresentationState = state ?? throw new ArgumentNullException(nameof(state));
            ToolPresentationView = view ?? throw new ArgumentNullException(nameof(view));
        }

        public void DetachToolPresentation()
        {
            ToolPresentationState = null;
            ToolPresentationView = null;
        }

        private void RebuildStructuredXmlNodes()
        {
            StructuredXmlNodes.Clear();

            if (PartType != ChatMessagePartType.StructuredXml || string.IsNullOrWhiteSpace(Content))
            {
                return;
            }

            try
            {
                var document = XDocument.Parse(Content, LoadOptions.PreserveWhitespace);
                if (document.Root == null)
                {
                    return;
                }

                StructuredXmlNodes.Add(CreateXmlNode(document.Root));
            }
            catch
            {
                // Keep the raw text visible even when XML is incomplete or malformed.
            }
        }

        private void RebuildToolOutputDiffLines()
        {
            ToolOutputDiffLines.Clear();

            if (PartType != ChatMessagePartType.ToolOutput ||
                !string.Equals(PresentationKind, SkyweaverToolResultPresentationKinds.LineDiffV1, StringComparison.OrdinalIgnoreCase) ||
                !SkyweaverLineDiffPresentation.TryParseToolOutputXml(Content, out var diffEntries))
            {
                return;
            }

            foreach (var entry in diffEntries)
            {
                ToolOutputDiffLines.Add(new ChatToolOutputDiffLineModel(
                    entry.LineNumberText,
                    entry.Marker,
                    entry.Text,
                    entry.Kind switch
                    {
                        SkyweaverLineDiffEntryKind.Added => ChatToolOutputDiffLineKind.Added,
                        SkyweaverLineDiffEntryKind.Removed => ChatToolOutputDiffLineKind.Removed,
                        SkyweaverLineDiffEntryKind.Separator => ChatToolOutputDiffLineKind.Separator,
                        _ => ChatToolOutputDiffLineKind.Anchor
                    }));
            }
        }

        private void RebuildDerivedPresentationState()
        {
            RebuildStructuredXmlNodes();
            RebuildToolOutputDiffLines();
        }

        private static ChatStructuredXmlNodeModel CreateXmlNode(XElement element)
        {
            var node = new ChatStructuredXmlNodeModel
            {
                Name = element.Name.LocalName,
                Value = element.HasElements
                    ? string.Empty
                    : (element.Value ?? string.Empty).Trim()
            };

            foreach (var attribute in element.Attributes())
            {
                node.Children.Add(new ChatStructuredXmlNodeModel
                {
                    Name = $"@{attribute.Name.LocalName}",
                    Value = attribute.Value
                });
            }

            foreach (var child in element.Elements())
            {
                node.Children.Add(CreateXmlNode(child));
            }

            return node;
        }

        private static string? NormalizeMetadataValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
