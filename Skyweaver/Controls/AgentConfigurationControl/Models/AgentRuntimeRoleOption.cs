namespace Skyweaver.Controls.AgentConfigurationControl.Models
{
    public sealed class AgentRuntimeRoleOption
    {
        public AgentRuntimeRoleOption(AgentRuntimeRole role, string displayName)
        {
            Role = role;
            DisplayName = displayName ?? role.ToString();
        }

        public AgentRuntimeRole Role { get; }

        public string DisplayName { get; }
    }
}
