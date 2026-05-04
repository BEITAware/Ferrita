namespace Skyweaver.Controls.SkyweaverPreferencesControl.ViewModels
{
    public sealed class PreferenceScaffoldItemViewModel
    {
        public PreferenceScaffoldItemViewModel(
            string label,
            string value,
            string description,
            string status = "Pending")
        {
            Label = label;
            Value = value;
            Description = description;
            Status = status;
        }

        public string Label { get; }

        public string Value { get; }

        public string Description { get; }

        public string Status { get; }
    }
}
