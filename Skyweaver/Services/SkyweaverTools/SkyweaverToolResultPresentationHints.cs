namespace Skyweaver.Services.SkyweaverTools
{
    public static class SkyweaverToolResultPresentationKinds
    {
        public const string LineDiffV1 = "line-diff-v1";
    }

    public static class SkyweaverToolResultPresentationMetadataKeys
    {
        public const string PresentationKind = "ToolResultPresentationKind";
    }

    public sealed class SkyweaverToolResultPresentationHints
    {
        public static SkyweaverToolResultPresentationHints None { get; } = new();

        public string PresentationKind { get; init; } = string.Empty;

        public bool IsUserVisible { get; init; }

        public bool HasAnyValue =>
            !string.IsNullOrWhiteSpace(PresentationKind) ||
            IsUserVisible;

        public static SkyweaverToolResultPresentationHints CreateLineDiff()
        {
            return new SkyweaverToolResultPresentationHints
            {
                PresentationKind = SkyweaverToolResultPresentationKinds.LineDiffV1
            };
        }
    }
}
