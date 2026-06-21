using System.Text;

namespace Ferrita.Services.FerritaTools
{
    public sealed class FerritaToolPromptDescriptionContext
    {
        public FerritaToolPromptDescriptionContext(
            FerritaToolRegistration registration,
            IReadOnlyList<FerritaToolKitDefinition> availableToolKits)
        {
            Registration = registration ?? throw new ArgumentNullException(nameof(registration));
            AvailableToolKits = availableToolKits ?? Array.Empty<FerritaToolKitDefinition>();
        }

        public FerritaToolRegistration Registration { get; }

        public FerritaToolDefinition BaseDefinition => Registration.BaseDefinition;

        public FerritaToolDefinition EffectiveDefinition => Registration.Definition;

        public FerritaToolConfigurationState ConfigurationState => Registration.ConfigurationState;

        public IReadOnlyList<FerritaToolKitDefinition> AvailableToolKits { get; }
    }

    public interface IFerritaToolPromptDescriptionProvider
    {
        string GetPromptDescription(FerritaToolPromptDescriptionContext context);
    }

    public sealed record FerritaPromptToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<FerritaToolParameterDefinition> Parameters,
        bool RequiresHostConfirmation,
        string? FewShots = null);

    public static class FerritaToolPromptSupport
    {
        public static (string Description, string? FewShots) SplitFewShots(string description)
        {
            if (string.IsNullOrEmpty(description)) return (string.Empty, null);
            
            var markers = new[] { "\nFew-shots:", "\nFew-shot:", "\n调用示例：", "\n调用格式：", "\n示例：", "\n格式：" };
            foreach (var marker in markers)
            {
                int index = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var cleanDesc = description.Substring(0, index).Trim();
                    var fewShots = description.Substring(index).Trim();
                    return (cleanDesc, fewShots);
                }
            }
            return (description, null);
        }
        public static string ResolvePromptDescription(
            FerritaToolRegistration registration,
            IReadOnlyList<FerritaToolKitDefinition> availableToolKits)
        {
            ArgumentNullException.ThrowIfNull(registration);

            if (registration.Tool is IFerritaToolPromptDescriptionProvider descriptionProvider)
            {
                var dynamicDescription = descriptionProvider.GetPromptDescription(
                    new FerritaToolPromptDescriptionContext(
                        registration,
                        availableToolKits));

                if (!string.IsNullOrWhiteSpace(dynamicDescription))
                {
                    return dynamicDescription.Trim();
                }
            }

            return registration.Definition.Description;
        }

        public static void AppendToolListing(
            StringBuilder builder,
            IEnumerable<FerritaPromptToolDefinition> tools)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(tools);

            foreach (var tool in tools)
            {
                var toolDescription = NormalizeInline(tool.Description);
                builder.AppendLine($"- {tool.Name}：{(toolDescription.Length == 0 ? "未提供说明。" : toolDescription)}");
                builder.AppendLine(tool.RequiresHostConfirmation
                    ? "  确认：执行前需要 Host 批准。"
                    : "  确认：无需额外 Host 批准。");

                if (tool.Parameters.Count == 0)
                {
                    builder.AppendLine("  参数：无。");
                    continue;
                }

                builder.AppendLine("  参数：");
                foreach (var parameter in tool.Parameters)
                {
                    var requirementText = parameter.IsRequired ? "必填" : "可选";
                    var defaultText = string.IsNullOrWhiteSpace(parameter.DefaultValue)
                        ? string.Empty
                        : $" 默认值={parameter.DefaultValue}。";
                    var parameterDescription = NormalizeInline(parameter.Description);
                    builder.AppendLine(
                        $"    - {parameter.Name}（{LocalizeParameterType(parameter.ParameterType)}，{requirementText}）：{(parameterDescription.Length == 0 ? "未提供说明。" : parameterDescription)}{defaultText}");
                }
            }
        }

        public static string FormatToolListing(IEnumerable<FerritaPromptToolDefinition> tools)
        {
            ArgumentNullException.ThrowIfNull(tools);

            var builder = new StringBuilder();
            AppendToolListing(builder, tools);
            return builder.ToString().TrimEnd();
        }

        public static string LocalizeParameterType(FerritaToolParameterType parameterType)
        {
            return parameterType switch
            {
                FerritaToolParameterType.String => "文本",
                FerritaToolParameterType.Boolean => "布尔",
                FerritaToolParameterType.Integer => "整数",
                FerritaToolParameterType.Number => "数值",
                FerritaToolParameterType.Json => "JSON",
                _ => parameterType.ToString()
            };
        }

        private static string NormalizeInline(string? text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : text.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        }
    }
}
