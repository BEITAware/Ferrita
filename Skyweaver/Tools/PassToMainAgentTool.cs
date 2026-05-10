using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class PassToMainAgentTool : ISkyweaverTool
    {
        public const string ToolName = SkyweaverBuiltInToolNames.PassToMainAgent;

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Only for sub-agents. Call this tool to end the sub-agent loop and pass the final payload back to the main agent. The tool payload becomes the sub-agent's response returned to SpawnSubAgent.",
            "GuideBot",
            [
                new SkyweaverToolParameterDefinition(
                    SkyweaverBuiltInToolNames.PassToMainAgentParameter,
                    "The exact content to return to the main agent. For structured tasks, this can be XML or preserved content.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            isSystemTool: true,
            supportsAsyncInvocation: false);

        public SkyweaverToolDefinition Definition => s_definition;

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.IsSubAgent)
            {
                return Task.FromResult(SkyweaverToolResult.Failure("PassToMainAgent can only be used inside a sub-agent loop."));
            }

            var payload = arguments.GetString(SkyweaverBuiltInToolNames.PassToMainAgentParameter)?.Trim() ?? string.Empty;
            if (payload.Length == 0)
            {
                return Task.FromResult(SkyweaverToolResult.Failure("PassToMainAgent requires non-empty content."));
            }

            return Task.FromResult(SkyweaverToolResult.Success(
                "PassToMainAgent accepted.",
                new Dictionary<string, object?>
                {
                    ["passToMainAgent"] = payload
                }));
        }
    }
}
