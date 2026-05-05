using System.Text;
using Skyweaver.Controls.AgentConfigurationControl.Models;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Controls.AgentConfigurationControl.Services
{
    public sealed class AgentSystemPromptBuilder
    {
        private readonly AgentConfigurationRepository _configurationRepository;
        private readonly SkyweaverPromptToolCatalogService _promptToolCatalogService;

        public AgentSystemPromptBuilder()
            : this(
                new AgentConfigurationRepository(new AgentConfigurationPathProvider()),
                new SkyweaverPromptToolCatalogService())
        {
        }

        public AgentSystemPromptBuilder(
            AgentConfigurationRepository configurationRepository,
            SkyweaverPromptToolCatalogService promptToolCatalogService)
        {
            _configurationRepository = configurationRepository ?? throw new ArgumentNullException(nameof(configurationRepository));
            _promptToolCatalogService = promptToolCatalogService ?? throw new ArgumentNullException(nameof(promptToolCatalogService));
        }

        public string BuildCompleteSystemPrompt(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName))
            {
                throw new ArgumentException("Agent name cannot be empty.", nameof(agentName));
            }

            var normalizedAgentName = agentName.Trim();
            var agent = _configurationRepository.Load().FirstOrDefault(candidate =>
                string.Equals(candidate.AgentId, normalizedAgentName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.DisplayName, normalizedAgentName, StringComparison.OrdinalIgnoreCase));

            if (agent == null)
            {
                throw new InvalidOperationException($"Agent '{normalizedAgentName}' could not be found.");
            }

            return BuildCompleteSystemPrompt(agent);
        }

        public string BuildCompleteSystemPrompt(AgentDefinition agent)
        {
            return BuildCompleteSystemPrompt(
                agent,
                supportsHostToolConfirmation: false,
                availableToolKits: null);
        }

        public string BuildCompleteSystemPrompt(
            AgentDefinition agent,
            bool supportsHostToolConfirmation,
            IReadOnlyList<SkyweaverToolKitDefinition>? availableToolKits = null,
            IReadOnlyCollection<string>? activeToolKitKeys = null)
        {
            ArgumentNullException.ThrowIfNull(agent);

            var externalTools = _promptToolCatalogService.ResolveCallableTools(
                agent,
                supportsHostToolConfirmation,
                activeToolKitKeys: activeToolKitKeys,
                restrictToToolNames: null,
                availableToolKits: availableToolKits);
            var builder = new StringBuilder(4096);

            AppendIdentitySection(builder, agent);
            AppendInstructionSection(builder, agent);
            AppendInputOutputSection(builder, agent);
            AppendPassdownSection(builder, agent);
            AppendExternalToolSection(builder, externalTools, supportsHostToolConfirmation);
            AppendProtocolSection(builder, agent, externalTools.Count > 0, supportsHostToolConfirmation);
            AppendResponseRulesSection(builder, agent, externalTools.Count > 0);

            return builder.ToString().Trim();
        }

        private static void AppendIdentitySection(StringBuilder builder, AgentDefinition agent)
        {
            builder.AppendLine("角色");
            builder.AppendLine($"- 代理名称：{agent.DisplayNameOrFallback}");
            builder.AppendLine($"- 代理 ID：{agent.AgentIdOrFallback}");
            builder.AppendLine($"- 输出模式：{(agent.IsStructuredXmlIO ? "结构化 XML" : "自然语言")}");
            builder.AppendLine();
        }

        private static void AppendInstructionSection(StringBuilder builder, AgentDefinition agent)
        {
            builder.AppendLine("任务");

            var systemPrompt = NormalizeMultiline(agent.SystemPrompt);
            if (systemPrompt.Length == 0)
            {
                builder.AppendLine("- 当前未配置自定义任务说明。请结合运行时输入和下方规则完成任务。");
            }
            else
            {
                builder.AppendLine("- 严格遵循下面的自定义代理指令：");
                builder.AppendLine(systemPrompt);
            }

            builder.AppendLine();
        }

        private static void AppendInputOutputSection(StringBuilder builder, AgentDefinition agent)
        {
            builder.AppendLine("输入输出约定");

            if (agent.IsStructuredXmlIO)
            {
                builder.AppendLine($"- 运行时输入将是一个完整的 <{AgentDefinition.InputRootName}> XML 文档。");
                AppendOptionalDescription(builder, "输入说明", agent.InputDescription);
                AppendSchemaPreview(builder, "输入结构", agent.InputSchemaRoot);
                builder.AppendLine($"- 当你不调用工具并结束代理循环时，最终回复必须是一个完整的 <{AgentDefinition.OutputRootName}> XML 文档。");
                AppendOptionalDescription(builder, "输出说明", agent.OutputDescription);
                AppendSchemaPreview(builder, "输出结构", agent.OutputSchemaRoot);
            }
            else
            {
                builder.AppendLine("- 运行时输入是自然语言文本。");
                AppendOptionalDescription(builder, "输入说明", agent.InputDescription);
                builder.AppendLine("- 当你不调用工具并结束代理循环时，最终回复应直接写自然语言正文。");
                AppendOptionalDescription(builder, "输出说明", agent.OutputDescription);
            }

            builder.AppendLine();
        }

        private static void AppendPassdownSection(StringBuilder builder, AgentDefinition agent)
        {
            builder.AppendLine("内置工具");
            builder.AppendLine($"- {SkyweaverBuiltInToolNames.Passdown}：把当前代理节点的输出传递给会话流下游。它是内部传递工具，不是普通外部操作。");
            builder.AppendLine($"  调用格式：<Tool ToolName=\"{SkyweaverBuiltInToolNames.Passdown}\"><{SkyweaverBuiltInToolNames.PassdownParameter}>...</{SkyweaverBuiltInToolNames.PassdownParameter}></Tool>");

            if (agent.IsStructuredXmlIO)
            {
                builder.AppendLine($"  结构化输出格式：把完整 <{AgentDefinition.OutputRootName}> XML 树作为 <{SkyweaverBuiltInToolNames.PassdownParameter}> 的子树。");
                builder.AppendLine($"  示例：<Tool ToolName=\"{SkyweaverBuiltInToolNames.Passdown}\"><{SkyweaverBuiltInToolNames.PassdownParameter}><{AgentDefinition.OutputRootName}>...</{AgentDefinition.OutputRootName}></{SkyweaverBuiltInToolNames.PassdownParameter}></Tool>");
            }
            else
            {
                builder.AppendLine($"  自然语言输出格式：把文本直接放在 <{SkyweaverBuiltInToolNames.PassdownParameter}> 内。");
                builder.AppendLine($"  示例：<Tool ToolName=\"{SkyweaverBuiltInToolNames.Passdown}\"><{SkyweaverBuiltInToolNames.PassdownParameter}>这里是要传递给下游的文本</{SkyweaverBuiltInToolNames.PassdownParameter}></Tool>");
            }

            builder.AppendLine("- 调用 Passdown 后，Host 会返回工具结果并自动继续代理循环。之后如果不再需要工具，直接输出最终聊天内容或留空结束。");
            builder.AppendLine();
        }

        private static void AppendExternalToolSection(
            StringBuilder builder,
            IReadOnlyList<SkyweaverPromptToolDefinition> tools,
            bool supportsHostToolConfirmation)
        {
            builder.AppendLine("可调用外部工具");

            if (tools.Count == 0)
            {
                builder.AppendLine(supportsHostToolConfirmation
                    ? "- 当前该代理没有可用的外部可调用工具。"
                    : "- 当前该代理没有可用的外部可调用工具。仅需 Host 确认的工具因宿主不支持确认而被隐藏。");
                builder.AppendLine();
                return;
            }

            SkyweaverToolPromptSupport.AppendToolListing(builder, tools);
            builder.AppendLine();
        }

        private static void AppendProtocolSection(
            StringBuilder builder,
            AgentDefinition agent,
            bool hasExternalTools,
            bool supportsHostToolConfirmation)
        {
            builder.AppendLine("聊天与工具协议");
            builder.AppendLine("- 普通聊天内容直接写正文，不要把聊天消息包进 XML 工具树。正文会被 Host 持续流式显示。");
            builder.AppendLine("- 需要调用工具时，只输出一个或多个独立的 <Tool ToolName=\"...\">...</Tool> 标签。不要再使用 <Tools> 根节点。");
            builder.AppendLine("- <Tool> 标签之外的文本会被当成可见聊天内容；<Tool> 标签本身会被 Host 捕获、流式呈现为工具调用卡片，并从聊天正文中移除。");
            builder.AppendLine("- 只要本次 assistant 响应包含任意 <Tool> 调用，Host 就会按出现顺序执行工具并自动继续下一轮代理循环。");
            builder.AppendLine("- 如果本次 assistant 响应不包含 <Tool> 调用，Host 会自动结束当前代理循环。");
            builder.AppendLine("- 不要调用未列出的外部工具。不要使用 CreateMessage、FinishTask、<tool_call>、<function_call> 或其他旧式伪协议。");
            builder.AppendLine("- 工具参数写成子元素：");
            builder.AppendLine("  <Tool ToolName=\"ToolName\">");
            builder.AppendLine("    <ParameterName>value</ParameterName>");
            builder.AppendLine("  </Tool>");

            if (hasExternalTools)
            {
                builder.AppendLine("- 可以在一次响应中连续放置多个 <Tool> 标签；Host 会按标签出现顺序执行。");
            }
            else
            {
                builder.AppendLine("- 当前运行时没有外部工具可用；如需向会话流下游传递载荷，只使用 Passdown。");
            }

            if (supportsHostToolConfirmation)
            {
                builder.AppendLine("- 某些外部工具可能会暂停等待 Host 确认。如果批准被拒绝，请基于返回的工具结果继续。");
            }

            builder.AppendLine();
            builder.AppendLine("示例");
            if (agent.IsStructuredXmlIO)
            {
                builder.AppendLine("- 不调用工具并结束：");
                builder.AppendLine($"  <{AgentDefinition.OutputRootName}>...</{AgentDefinition.OutputRootName}>");
                builder.AppendLine("- 传递结构化载荷给下游：");
                builder.AppendLine($"  <Tool ToolName=\"{SkyweaverBuiltInToolNames.Passdown}\"><{SkyweaverBuiltInToolNames.PassdownParameter}><{AgentDefinition.OutputRootName}>...</{AgentDefinition.OutputRootName}></{SkyweaverBuiltInToolNames.PassdownParameter}></Tool>");
            }
            else
            {
                builder.AppendLine("- 不调用工具并结束：");
                builder.AppendLine("  这里直接写最终答复。");
                builder.AppendLine("- 传递自然语言载荷给下游：");
                builder.AppendLine($"  <Tool ToolName=\"{SkyweaverBuiltInToolNames.Passdown}\"><{SkyweaverBuiltInToolNames.PassdownParameter}>这里是要传递给下游的文本</{SkyweaverBuiltInToolNames.PassdownParameter}></Tool>");
            }

            builder.AppendLine();
        }

        private static void AppendResponseRulesSection(
            StringBuilder builder,
            AgentDefinition agent,
            bool hasExternalTools)
        {
            builder.AppendLine("响应规则");
            builder.AppendLine("- 若只是回答用户或结束当前代理循环，直接输出最终内容，不要调用工具。");
            builder.AppendLine("- 如果还需要代理循环继续，必须调用工具，否则代理循环将结束。");
            builder.AppendLine("- 若需要工具结果，输出 <Tool> 标签并等待 Host 自动继续。");
            builder.AppendLine("- 不要输出格式错误的 XML 工具标签；Host 不会修复工具 XML。");
            builder.AppendLine("- 若正文需要提到字面量 <Tool> 标签，请使用实体转义（例如 &lt;Tool&gt;），否则 Host 会把它当成工具调用。");

            if (agent.IsStructuredXmlIO)
            {
                builder.AppendLine($"- 结构化代理的工具自由最终输出必须以 <{AgentDefinition.OutputRootName}> 作为根节点。");
                builder.AppendLine($"- 结构化代理使用 Passdown 时，<{SkyweaverBuiltInToolNames.PassdownParameter}> 内也必须包含完整 <{AgentDefinition.OutputRootName}> XML 树。");
            }
            else
            {
                builder.AppendLine($"- 自然语言代理不要把最终自然语言答复单独写成 <{AgentDefinition.OutputRootName}> XML 文档。");
                builder.AppendLine("- 自然语言 Passdown 的参数内容必须是文本，不要放 XML 子树。");
            }

            if (!hasExternalTools)
            {
                builder.AppendLine("- 没有外部工具时，不要为了普通回复调用 Passdown；只有需要向会话流下游传递载荷时才使用它。");
            }
        }

        private static void AppendOptionalDescription(
            StringBuilder builder,
            string label,
            string? description)
        {
            var normalized = NormalizeInline(description);
            if (normalized.Length > 0)
            {
                builder.AppendLine($"- {label}：{normalized}");
            }
        }

        private static void AppendSchemaPreview(
            StringBuilder builder,
            string label,
            XmlElementNodeDefinition rootNode)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(rootNode);

            builder.AppendLine($"- {label}：");
            AppendSchemaNode(builder, rootNode, indentLevel: 1);
        }

        private static void AppendSchemaNode(
            StringBuilder builder,
            XmlElementNodeDefinition node,
            int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);
            var elementName = string.IsNullOrWhiteSpace(node.Name)
                ? "节点"
                : node.Name.Trim();

            if (node.Children.Count == 0)
            {
                builder.AppendLine($"{indent}<{elementName}>...</{elementName}>");
                return;
            }

            builder.AppendLine($"{indent}<{elementName}>");
            foreach (var child in node.Children)
            {
                AppendSchemaNode(builder, child, indentLevel + 1);
            }

            builder.AppendLine($"{indent}</{elementName}>");
        }

        private static string NormalizeInline(string? text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : text.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        }

        private static string NormalizeMultiline(string? text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : text.Trim();
        }
    }
}
