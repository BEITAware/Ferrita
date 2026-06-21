using Ferrita.Controls.AgentConfigurationControl.Models;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Controls.LanguageModelConfigurationControl.Services;
using Ferrita.Services.Localization;

namespace Ferrita.Controls.AgentConfigurationControl.Services
{
    public sealed class AgentLanguageModelResolver : IAgentLanguageModelResolver
    {
        private readonly LanguageModelConfigurationRepository _languageModelRepository;
        private readonly CapabilityLayerConfigurationRepository _capabilityLayerRepository;

        private sealed record ResolutionPlan(
            bool UseFallback,
            string SelectionDisplayName,
            IReadOnlyList<LanguageModelDefinition> Candidates,
            IReadOnlyList<string> Issues);

        public AgentLanguageModelResolver()
            : this(
                new LanguageModelConfigurationRepository(new LanguageModelConfigurationPathProvider()),
                new CapabilityLayerConfigurationRepository(new LanguageModelConfigurationPathProvider()))
        {
        }

        public AgentLanguageModelResolver(
            LanguageModelConfigurationRepository languageModelRepository,
            CapabilityLayerConfigurationRepository capabilityLayerRepository)
        {
            _languageModelRepository = languageModelRepository ?? throw new ArgumentNullException(nameof(languageModelRepository));
            _capabilityLayerRepository = capabilityLayerRepository ?? throw new ArgumentNullException(nameof(capabilityLayerRepository));
        }

        public IReadOnlyList<LanguageModelDefinition> GetCandidateModels(AgentDefinition agent)
        {
            var plan = BuildResolutionPlan(agent, LoadLanguageModelMap(), LoadCapabilityLayerMap());
            return plan.Candidates;
        }

        public IReadOnlyList<LanguageModelDefinition> GetCandidateModelsForCapabilityLayer(string capabilityLayerKey)
        {
            var languageModelMap = LoadLanguageModelMap();
            var capabilityLayerMap = LoadCapabilityLayerMap();
            return BuildCapabilityLayerPlan(capabilityLayerKey, languageModelMap, capabilityLayerMap).Cands;
        }

        public Task<LanguageModelDefinition> ResolveFirstAvailableAsync(
            AgentDefinition agent,
            Func<LanguageModelDefinition, CancellationToken, Task>? probeAsync = null,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWithFallbackAsync(
                agent,
                async (model, ct) =>
                {
                    if (probeAsync != null)
                    {
                        await probeAsync(model, ct).ConfigureAwait(false);
                    }

                    return model;
                },
                cancellationToken);
        }

        public async Task<T> ExecuteWithFallbackAsync<T>(
            AgentDefinition agent,
            Func<LanguageModelDefinition, CancellationToken, Task<T>> operationAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(operationAsync);

            var languageModelMap = LoadLanguageModelMap();
            var capabilityLayerMap = LoadCapabilityLayerMap();
            var plan = BuildResolutionPlan(agent, languageModelMap, capabilityLayerMap);

            if (plan.Candidates.Count == 0)
            {
                throw new InvalidOperationException(BuildMissingSelectionMessage(agent, plan));
            }

            if (!plan.UseFallback)
            {
                var targetModel = plan.Candidates[0];
                EnsureModelConfigured(agent, targetModel);
                return await operationAsync(targetModel, cancellationToken).ConfigureAwait(false);
            }

            Exception? lastError = null;
            var failureMessages = new List<string>();

            int errorCount = 0;
            const int maxRetries = 10;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failureMessages.Clear();
                lastError = null;
                bool shouldRetryWholeLevel = false;

                foreach (var candidate in plan.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!candidate.InterfaceSettings.IsFullyConfigured)
                    {
                        failureMessages.Add(LF("AgentLanguageModelResolver.Failure.InterfaceIncompleteFormat", "{0}：接口配置不完整", GetLanguageModelDisplayName(candidate)));
                        continue;
                    }

                    try
                    {
                        return await operationAsync(candidate, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        failureMessages.Add(LF("AgentLanguageModelResolver.Failure.ModelErrorFormat", "{0}：{1}", GetLanguageModelDisplayName(candidate), ex.Message));

                        if (IsContextOverflowError(ex))
                        {
                            continue;
                        }

                        shouldRetryWholeLevel = true;
                    }
                }

                if (shouldRetryWholeLevel)
                {
                    errorCount++;
                    if (errorCount <= maxRetries)
                    {
                        double delaySeconds = Math.Ceiling(Math.Pow(1.4, errorCount));
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                var failureText = failureMessages.Count == 0
                    ? L("AgentLanguageModelResolver.Failure.NoCallableCandidates", "没有可调用的候选语言模型。")
                    : LF("AgentLanguageModelResolver.Failure.AttemptedInOrderFormat", "已按顺序尝试：{0}", JoinMessages(failureMessages));

                throw new InvalidOperationException(
                    LF("AgentLanguageModelResolver.Error.AgentCapabilityLayerNoAvailableModelFormat", "代理“{0}”绑定的功能层级“{1}”没有找到无错误可用的语言模型。{2}", agent.DisplayNameOrFallback, plan.SelectionDisplayName, failureText),
                    lastError);
            }
        }

        public async Task<T> ExecuteCapabilityLayerWithFallbackAsync<T>(
            string capabilityLayerKey,
            Func<LanguageModelDefinition, CancellationToken, Task<T>> operationAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capabilityLayerKey);
            ArgumentNullException.ThrowIfNull(operationAsync);

            var languageModelMap = LoadLanguageModelMap();
            var capabilityLayerMap = LoadCapabilityLayerMap();
            var plan = BuildCapabilityLayerPlan(capabilityLayerKey, languageModelMap, capabilityLayerMap);

            if (plan.Cands.Count == 0)
            {
                var issues = plan.Issues.Count == 0 ? string.Empty : $" {JoinMessages(plan.Issues)}";
                throw new InvalidOperationException(
                    LF("AgentLanguageModelResolver.Error.CapabilityLayerNoCandidatesFormat", "功能层级“{0}”无法解析出候选语言模型。{1}", plan.SelectionDisplayName, issues));
            }

            Exception? lastError = null;
            var failureMessages = new List<string>();

            int errorCount = 0;
            const int maxRetries = 10;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failureMessages.Clear();
                lastError = null;
                bool shouldRetryWholeLevel = false;

                foreach (var candidate in plan.Cands)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!candidate.InterfaceSettings.IsFullyConfigured)
                    {
                        failureMessages.Add(LF("AgentLanguageModelResolver.Failure.InterfaceIncompleteFormat", "{0}：接口配置不完整", GetLanguageModelDisplayName(candidate)));
                        continue;
                    }

                    try
                    {
                        return await operationAsync(candidate, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        failureMessages.Add(LF("AgentLanguageModelResolver.Failure.ModelErrorFormat", "{0}：{1}", GetLanguageModelDisplayName(candidate), ex.Message));

                        if (IsContextOverflowError(ex))
                        {
                            continue;
                        }

                        shouldRetryWholeLevel = true;
                    }
                }

                if (shouldRetryWholeLevel)
                {
                    errorCount++;
                    if (errorCount <= maxRetries)
                    {
                        double delaySeconds = Math.Ceiling(Math.Pow(1.4, errorCount));
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                var failureText = failureMessages.Count == 0
                    ? L("AgentLanguageModelResolver.Failure.NoCallableCandidates", "没有可调用的候选语言模型。")
                    : LF("AgentLanguageModelResolver.Failure.AttemptedInOrderFormat", "已按顺序尝试：{0}", JoinMessages(failureMessages));

                throw new InvalidOperationException(
                    LF("AgentLanguageModelResolver.Error.CapabilityLayerNoAvailableModelFormat", "功能层级“{0}”没有找到无错误可用的语言模型。{1}", plan.SelectionDisplayName, failureText),
                    lastError);
            }
        }

        public int GetMinimumContextWindowTokens(AgentDefinition agent)
        {
            ArgumentNullException.ThrowIfNull(agent);

            var candidates = GetCandidateModels(agent);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    LF("AgentLanguageModelResolver.Error.AgentContextModelMissingFormat", "代理“{0}”未能解析到用于计算上下文窗口的语言模型。", agent.DisplayNameOrFallback));
            }

            return candidates.Min(model => model.EffectiveContextWindowTokens);
        }

        public int GetCapabilityLayerMinimumContextWindowTokens(string capabilityLayerKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capabilityLayerKey);

            var candidates = GetCandidateModelsForCapabilityLayer(capabilityLayerKey);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    LF("AgentLanguageModelResolver.Error.CapabilityLayerContextModelMissingFormat", "功能层级“{0}”未能解析到用于计算上下文窗口的语言模型。", capabilityLayerKey));
            }

            return candidates.Min(model => model.EffectiveContextWindowTokens);
        }

        private Dictionary<string, LanguageModelDefinition> LoadLanguageModelMap()
        {
            return _languageModelRepository.Load()
                .Where(model => !string.IsNullOrWhiteSpace(model.Key))
                .ToDictionary(model => model.Key, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, CapabilityLayerDefinition> LoadCapabilityLayerMap()
        {
            return _capabilityLayerRepository.Load()
                .Where(layer => !string.IsNullOrWhiteSpace(layer.Key))
                .ToDictionary(layer => layer.Key, StringComparer.OrdinalIgnoreCase);
        }

        private static ResolutionPlan BuildResolutionPlan(
            AgentDefinition agent,
            IReadOnlyDictionary<string, LanguageModelDefinition> languageModelMap,
            IReadOnlyDictionary<string, CapabilityLayerDefinition> capabilityLayerMap)
        {
            return agent.LanguageModelSelectionMode == AgentLanguageModelSelectionMode.CapabilityLayer
                ? BuildCapabilityLayerPlan(agent, languageModelMap, capabilityLayerMap)
                : BuildSpecificLanguageModelPlan(agent, languageModelMap);
        }

        private static ResolutionPlan BuildSpecificLanguageModelPlan(
            AgentDefinition agent,
            IReadOnlyDictionary<string, LanguageModelDefinition> languageModelMap)
        {
            if (string.IsNullOrWhiteSpace(agent.SelectedLanguageModelKey))
            {
                return new ResolutionPlan(
                    UseFallback: false,
                    SelectionDisplayName: L("AgentLanguageModelResolver.Selection.NoLanguageModel", "未选择语言模型"),
                    Candidates: Array.Empty<LanguageModelDefinition>(),
                    Issues: [L("AgentLanguageModelResolver.Issue.AgentNoSpecificModel", "未为代理绑定具体语言模型。")]);
            }

            if (!languageModelMap.TryGetValue(agent.SelectedLanguageModelKey, out var model))
            {
                return new ResolutionPlan(
                    UseFallback: false,
                    SelectionDisplayName: agent.SelectedLanguageModelKey,
                    Candidates: Array.Empty<LanguageModelDefinition>(),
                    Issues: [L("AgentLanguageModelResolver.Issue.LanguageModelMissing", "引用的语言模型不存在。")]);
            }

            return new ResolutionPlan(
                UseFallback: false,
                SelectionDisplayName: GetLanguageModelDisplayName(model),
                Candidates: [model],
                Issues: Array.Empty<string>());
        }

        private static ResolutionPlan BuildCapabilityLayerPlan(
            AgentDefinition agent,
            IReadOnlyDictionary<string, LanguageModelDefinition> languageModelMap,
            IReadOnlyDictionary<string, CapabilityLayerDefinition> capabilityLayerMap)
        {
            return BuildCapabilityLayerPlan(agent.SelectedCapabilityLayerKey, languageModelMap, capabilityLayerMap).ToResolutionPlan();
        }

        private static CapabilityLayerResolutionPlan BuildCapabilityLayerPlan(
            string? capabilityLayerKey,
            IReadOnlyDictionary<string, LanguageModelDefinition> languageModelMap,
            IReadOnlyDictionary<string, CapabilityLayerDefinition> capabilityLayerMap)
        {
            if (string.IsNullOrWhiteSpace(capabilityLayerKey))
            {
                return new CapabilityLayerResolutionPlan(
                    SelectionDisplayName: L("AgentLanguageModelResolver.Selection.NoCapabilityLayer", "未选择功能层级"),
                    Cands: Array.Empty<LanguageModelDefinition>(),
                    Issues: [L("AgentLanguageModelResolver.Issue.CapabilityLayerNotSpecified", "未指定功能层级。")]);
            }

            if (!capabilityLayerMap.TryGetValue(capabilityLayerKey, out var layer))
            {
                return new CapabilityLayerResolutionPlan(
                    SelectionDisplayName: capabilityLayerKey,
                    Cands: Array.Empty<LanguageModelDefinition>(),
                    Issues: [L("AgentLanguageModelResolver.Issue.CapabilityLayerMissing", "引用的功能层级不存在。")]);
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<LanguageModelDefinition>();
            var issues = new List<string>();

            foreach (var entry in layer.LanguageModels)
            {
                var languageModelKey = entry.LanguageModelKey?.Trim() ?? string.Empty;
                if (languageModelKey.Length == 0 || !seenKeys.Add(languageModelKey))
                {
                    continue;
                }

                if (!languageModelMap.TryGetValue(languageModelKey, out var model))
                {
                    issues.Add(LF("AgentLanguageModelResolver.Issue.LayerLanguageModelMissingFormat", "功能层级中的语言模型引用“{0}”不存在。", languageModelKey));
                    continue;
                }

                candidates.Add(model);
            }

            return new CapabilityLayerResolutionPlan(
                SelectionDisplayName: GetCapabilityLayerDisplayName(layer),
                Cands: candidates,
                Issues: issues);
        }

        private static void EnsureModelConfigured(AgentDefinition agent, LanguageModelDefinition model)
        {
            if (model.InterfaceSettings.IsFullyConfigured)
            {
                return;
            }

            throw new InvalidOperationException(
                LF("AgentLanguageModelResolver.Error.AgentModelInterfaceIncompleteFormat", "代理“{0}”绑定的语言模型“{1}”接口配置不完整。", agent.DisplayNameOrFallback, GetLanguageModelDisplayName(model)));
        }

        private static string BuildMissingSelectionMessage(AgentDefinition agent, ResolutionPlan plan)
        {
            var issues = plan.Issues.Count == 0
                ? string.Empty
                : $" {JoinMessages(plan.Issues)}";

            return plan.UseFallback
                ? LF("AgentLanguageModelResolver.Error.AgentCapabilityLayerNoCandidatesFormat", "代理“{0}”绑定的功能层级无法解析出候选语言模型。{1}", agent.DisplayNameOrFallback, issues)
                : LF("AgentLanguageModelResolver.Error.AgentSpecificModelMissingFormat", "代理“{0}”未能解析到具体语言模型。{1}", agent.DisplayNameOrFallback, issues);
        }

        private static string GetLanguageModelDisplayName(LanguageModelDefinition model)
        {
            var displayName = model.DisplayName?.Trim() ?? string.Empty;
            if (displayName.Length > 0)
            {
                return displayName;
            }

            var summaryModelId = model.SummaryModelId?.Trim() ?? string.Empty;
            if (summaryModelId.Length > 0)
            {
                return LF("AgentLanguageModelResolver.Display.UnnamedModelFormat", "未命名模型 ({0})", summaryModelId);
            }

            return LF("AgentLanguageModelResolver.Display.UnnamedModelFormat", "未命名模型 ({0})", GetShortKey(model.Key));
        }

        private static string GetCapabilityLayerDisplayName(CapabilityLayerDefinition layer)
        {
            var displayName = layer.Name?.Trim() ?? string.Empty;
            return displayName.Length > 0
                ? displayName
                : LF("AgentLanguageModelResolver.Display.UnnamedCapabilityLayerFormat", "未命名功能层级 ({0})", GetShortKey(layer.Key));
        }

        private static string GetShortKey(string? key)
        {
            var normalizedKey = (key ?? string.Empty).Trim();
            if (normalizedKey.Length <= 8)
            {
                return normalizedKey;
            }

            return normalizedKey[..8];
        }

        private static string JoinMessages(IEnumerable<string> messages)
        {
            return string.Join(L("Common.ListSeparator.Semicolon", "；"), messages);
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static string LF(string resourceKey, string fallbackFormat, params object?[] args)
        {
            var format = L(resourceKey, fallbackFormat);
            return string.Format(format, args);
        }

        private static bool IsContextOverflowError(Exception? ex)
        {
            if (ex == null)
            {
                return false;
            }

            if (IsContextOverflowMessage(ex.Message))
            {
                return true;
            }

            if (ex.InnerException != null && IsContextOverflowError(ex.InnerException))
            {
                return true;
            }

            if (ex is AggregateException aggEx)
            {
                foreach (var inner in aggEx.InnerExceptions)
                {
                    if (IsContextOverflowError(inner))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsContextOverflowMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            var lower = message.ToLowerInvariant();
            return lower.Contains("max token") ||
                   lower.Contains("too much") ||
                   lower.Contains("token") ||
                   lower.Contains("context length") ||
                   lower.Contains("context window") ||
                   lower.Contains("context_length_exceeded") ||
                   lower.Contains("limit exceeded") ||
                   lower.Contains("too many tokens");
        }

        private sealed record CapabilityLayerResolutionPlan(
            string SelectionDisplayName,
            IReadOnlyList<LanguageModelDefinition> Cands,
            IReadOnlyList<string> Issues)
        {
            public ResolutionPlan ToResolutionPlan()
            {
                return new ResolutionPlan(
                    UseFallback: true,
                    SelectionDisplayName,
                    Cands,
                    Issues);
            }
        }
    }
}
