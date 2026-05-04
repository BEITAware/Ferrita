using System;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.Models
{
    public sealed class PreferencePageInfo
    {
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public Type? ViewType { get; init; }

        public Type? ViewModelType { get; init; }

        public int Order { get; init; }
    }
}
