using Skyweaver.Services.Localization;

namespace Skyweaver.Models.ChatSession
{
    public sealed class ChatSessionModel
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public string IconPath { get; set; } = "pack://application:,,,/Resources/NewNodeGraphAlt.png";

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        // 会话列表摘要只是 UI 投影，不作为历史事实来源。
        public string ContextSummary { get; set; } = string.Empty;

        public string MetadataNote { get; set; } = string.Empty;

        public bool IsShellSession { get; set; }

        public bool IsScheduledTaskSession { get; set; }

        public ChatSessionFlowBinding FlowBinding { get; } = new();

        public ChatSessionTranscript Transcript { get; } = new();

        public ChatSessionResourceManifest Resources { get; } = new();

        public string SessionFolderPath { get; set; } = string.Empty;

        public string SessionFilePath { get; set; } = string.Empty;

        public string ResourcesFolderPath { get; set; } = string.Empty;

        public bool HasBoundFlow => FlowBinding.IsBound;

        public string BoundFlowDisplayName => string.IsNullOrWhiteSpace(FlowBinding.GraphName)
            ? L("ChatSession.BoundFlow.Unbound", "未绑定会话流")
            : FlowBinding.GraphName;

        public DateTime CreatedAt
        {
            get => CreatedAtUtc;
            set => CreatedAtUtc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        public DateTime UpdatedAt
        {
            get => UpdatedAtUtc;
            set => UpdatedAtUtc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }
    }
}
