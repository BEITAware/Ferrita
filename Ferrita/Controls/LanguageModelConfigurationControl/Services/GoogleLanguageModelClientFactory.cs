using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.Localization;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    internal sealed class GoogleLanguageModelInterfaceAdapter : ILanguageModelInterfaceAdapter
    {
        private const int InlinePayloadBudgetBytes = 15 * 1024 * 1024;
        private const int FileActivationPollingDelayMilliseconds = 1000;
        private const int FileActivationPollingAttempts = 60;

        private static readonly HttpClient s_httpClient = new();
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null
        };
        private static readonly ConcurrentDictionary<string, UploadedGoogleFileReference> s_uploadedFileCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Lazy<HashSet<string>> s_validToolNames = new(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var toolManager = new FerritaToolManager();
                var list = toolManager.GetRegisteredTools(resolveIcons: false);
                if (list != null)
                {
                    foreach (var tool in list)
                    {
                        if (tool?.Definition?.Name != null)
                        {
                            set.Add(tool.Definition.Name);
                        }
                    }
                }
            }
            catch
            {
            }
            return set;
        });

        public string InterfaceType => "Google";

        public LanguageModelInterfaceSettings CreateInterfaceSettings()
        {
            return new GoogleLanguageModelSettings();
        }

        public void Validate(LanguageModelDefinition model)
        {
            _ = GetSettings(model);
        }

        public async Task<LanguageModelChatResponse> GetResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            var settings = GetSettings(model);
            using var request = await CreateGenerateContentRequestAsync(
                    settings,
                    messages,
                    tools,
                    useStreamingEndpoint: false,
                    cancellationToken).ConfigureAwait(false);
            using var response = await s_httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);

            using var document = await ReadJsonDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            var parsed = ParseResponsePayload(document.RootElement, settings.ModelId);

            var finalText = parsed.Text;
            bool useNativeTools = tools != null && tools.Count > 0;

            if (useNativeTools && parsed.ToolCalls.Count > 0)
            {
                var xmlBuilder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    xmlBuilder.Append(finalText);
                    xmlBuilder.Append("\n\n");
                }
                
                foreach (var tc in parsed.ToolCalls)
                {
                    xmlBuilder.Append($"<Tool ToolName=\"{tc.Name}\" ToolCallID=\"{tc.Id}\">");
                    if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(tc.ArgumentsJson);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                var paramName = prop.Name;
                                var paramValue = prop.Value.ValueKind == JsonValueKind.String
                                    ? prop.Value.GetString() ?? string.Empty
                                    : prop.Value.GetRawText();

                                xmlBuilder.Append($"<{paramName}>");
                                if (paramValue.Contains("<") || paramValue.Contains(">") || paramValue.Contains("&"))
                                {
                                    xmlBuilder.Append($"<![CDATA[{paramValue}]]>");
                                }
                                else
                                {
                                    xmlBuilder.Append(paramValue);
                                }
                                xmlBuilder.Append($"</{paramName}>");
                            }
                        }
                        catch {}
                    }
                    xmlBuilder.Append("</Tool>");
                }
                
                finalText = xmlBuilder.ToString();
            }

            return new LanguageModelChatResponse
            {
                Text = finalText,
                ReasoningText = parsed.ReasoningText,
                ModelId = parsed.ModelId,
                InputTokenCount = parsed.InputTokenCount,
                TotalTokenCount = parsed.TotalTokenCount
            };
        }

        private static IReadOnlyList<LanguageModelToolCall> ParseXmlToolCalls(string xmlText)
        {
            var results = new List<LanguageModelToolCall>();
            if (string.IsNullOrEmpty(xmlText))
            {
                return results;
            }

            // 1. Standard format: <Tool ToolName="xxx">...</Tool> or <ToolAsync ToolName="xxx">...</ToolAsync>
            var standardMatches = Regex.Matches(xmlText, @"<(Tool|ToolAsync)\s+[^>]*ToolName\s*=\s*(?:""(?<name>[^""]*)""|'(?<name>[^']*)')[^>]*>(?<body>.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in standardMatches)
            {
                var toolName = match.Groups["name"].Value;
                var body = match.Groups["body"].Value;
                AddToolCall(results, match.Value, toolName, body, null);
            }

            // 2. Alternative format: <tool_call>...</tool_call> or <Tool_call>...</Tool_call>
            var toolCallMatches = Regex.Matches(xmlText, @"<(tool_call|Tool_call)(?<attrs>\s+[^>]*)?>(?<body>.*?)</\1>", RegexOptions.Singleline);
            foreach (Match match in toolCallMatches)
            {
                var attrs = match.Groups["attrs"].Value;
                var body = match.Groups["body"].Value;

                var toolName = Regex.Match(attrs, @"(?:ToolName|name)\s*=\s*(?:""(?<name>[^""]*)""|'(?<name>[^']*)')", RegexOptions.IgnoreCase).Groups["name"].Value;
                if (string.IsNullOrEmpty(toolName))
                {
                    var childNameMatch = Regex.Match(body, @"<(?:ToolName|name)>(?:\s*<!\[CDATA\[(?<cdata>.*?)\]\]>\s*|(?<val>.*?))</(?:ToolName|name)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (childNameMatch.Success)
                    {
                        toolName = childNameMatch.Groups["cdata"].Success ? childNameMatch.Groups["cdata"].Value : childNameMatch.Groups["val"].Value;
                    }
                }

                toolName = toolName?.Trim();
                if (!string.IsNullOrEmpty(toolName))
                {
                    AddToolCall(results, match.Value, toolName, body, new[] { "ToolName", "name" });
                }
            }

            // 3. Alternative format: <ToolName>...</ToolName> where ToolName is a valid Ferrita tool name
            var validTools = s_validToolNames.Value;
            if (validTools.Count > 0)
            {
                var escapedNames = new List<string>();
                foreach (var name in validTools)
                {
                    escapedNames.Add(Regex.Escape(name));
                }
                var pattern = @"<(" + string.Join("|", escapedNames) + @")(?<attrs>\s+[^>]*)?>(?<body>.*?)</\1>";
                var tagNameMatches = Regex.Matches(xmlText, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                foreach (Match match in tagNameMatches)
                {
                    var toolName = match.Groups[1].Value;
                    var body = match.Groups["body"].Value;
                    AddToolCall(results, match.Value, toolName, body, null);
                }
            }

            return results;
        }

        private static void AddToolCall(
            List<LanguageModelToolCall> results,
            string fullMatchText,
            string toolName,
            string body,
            string[]? excludeParamNames)
        {
            var args = new Dictionary<string, object?>();
            var paramMatches = Regex.Matches(body, @"<(?<param>[A-Za-z0-9_.-]+)>(?:\s*<!\[CDATA\[(?<cdata>.*?)\]\]>\s*|(?<val>.*?))</\1>", RegexOptions.Singleline);
            foreach (Match pm in paramMatches)
            {
                var paramName = pm.Groups["param"].Value;
                if (excludeParamNames != null)
                {
                    bool shouldExclude = false;
                    foreach (var exclude in excludeParamNames)
                    {
                        if (string.Equals(exclude, paramName, StringComparison.OrdinalIgnoreCase))
                        {
                            shouldExclude = true;
                            break;
                        }
                    }
                    if (shouldExclude)
                    {
                        continue;
                    }
                }
                var paramValue = pm.Groups["cdata"].Success ? pm.Groups["cdata"].Value : pm.Groups["val"].Value;
                args[paramName] = paramValue;
            }

            var toolCallId = Regex.Match(fullMatchText, @"ToolCallID\s*=\s*(?:""(?<id>[^""]*)""|'(?<id>[^']*)')", RegexOptions.IgnoreCase).Groups["id"].Value;
            if (string.IsNullOrEmpty(toolCallId))
            {
                toolCallId = "tc_" + Guid.NewGuid().ToString("N")[..8];
            }

            bool alreadyExists = false;
            foreach (var r in results)
            {
                if (string.Equals(r.Name, toolName, StringComparison.OrdinalIgnoreCase) && r.Id == toolCallId)
                {
                    alreadyExists = true;
                    break;
                }
            }

            if (!alreadyExists)
            {
                results.Add(new LanguageModelToolCall
                {
                    Name = toolName,
                    ArgumentsJson = JsonSerializer.Serialize(args),
                    Id = toolCallId
                });
            }
        }

        private static string StripXmlToolCalls(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var clean = text;

            // 1. Strip standard format
            clean = Regex.Replace(clean, @"<(Tool|ToolAsync)\s+[^>]*ToolName\s*=\s*(?:""[^""]*""|'[^']*')[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // 2. Strip <tool_call> and <Tool_call>
            clean = Regex.Replace(clean, @"<(tool_call|Tool_call)(?:\s+[^>]*)?>.*?</\1>", "", RegexOptions.Singleline);

            // 3. Strip <ToolName>...</ToolName> for valid tool names
            var validTools = s_validToolNames.Value;
            if (validTools.Count > 0)
            {
                var escapedNames = new List<string>();
                foreach (var name in validTools)
                {
                    escapedNames.Add(Regex.Escape(name));
                }
                var pattern = @"<(" + string.Join("|", escapedNames) + @")(?:\s+[^>]*)?>.*?</\1>";
                clean = Regex.Replace(clean, pattern, "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            }

            return clean.Trim();
        }

        private static IReadOnlyList<(string ToolName, string Content, string ToolCallId)> ParseXmlToolReturns(string xmlText)
        {
            var results = new List<(string ToolName, string Content, string ToolCallId)>();
            if (string.IsNullOrEmpty(xmlText))
            {
                return results;
            }

            var matches = Regex.Matches(xmlText, @"<ToolReturn\s+[^>]*ToolName\s*=\s*(?:""(?<name>[^""]*)""|'(?<name>[^']*)')[^>]*>(?<body>.*?)</ToolReturn>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var toolName = match.Groups["name"].Value;
                var body = match.Groups["body"].Value;

                var toolCallId = Regex.Match(match.Value, @"ToolCallID\s*=\s*(?:""(?<id>[^""]*)""|'(?<id>[^']*)')", RegexOptions.IgnoreCase).Groups["id"].Value;
                if (string.IsNullOrEmpty(toolCallId))
                {
                    toolCallId = Regex.Match(match.Value, @"ToolCallId\s*=\s*(?:""(?<id>[^""]*)""|'(?<id>[^']*)')", RegexOptions.IgnoreCase).Groups["id"].Value;
                }

                results.Add((toolName, body, toolCallId));
            }

            return results;
        }

        private static async Task<List<GoogleContent>> BuildContentsForJsonToolCallingAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            InlinePayloadBudget inlineBudget,
            CancellationToken cancellationToken)
        {
            var contents = new List<GoogleContent>();
            
            foreach (var message in messages.Where(message => message.Role != LanguageModelChatRole.System))
            {
                var role = message.Role;
                var contentText = message.Content;

                if (role == LanguageModelChatRole.Assistant)
                {
                    var xmlToolCalls = ParseXmlToolCalls(contentText);
                    if (xmlToolCalls.Count > 0)
                    {
                        var cleanText = StripXmlToolCalls(contentText);
                        var parts = new List<GooglePart>();
                        if (!string.IsNullOrEmpty(message.ReasoningContent))
                        {
                            parts.Add(new GooglePart
                            {
                                Text = message.ReasoningContent,
                                Thought = true
                            });
                        }
                        if (!string.IsNullOrWhiteSpace(cleanText))
                        {
                            parts.Add(new GooglePart { Text = cleanText });
                        }

                        foreach (var tc in xmlToolCalls)
                        {
                            var args = new Dictionary<string, object?>();
                            if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                            {
                                try
                                {
                                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson) ?? args;
                                }
                                catch {}
                            }
                            parts.Add(new GooglePart
                            {
                                FunctionCall = new GoogleFunctionCall
                                {
                                    Name = tc.Name,
                                    Args = args
                                }
                            });
                        }

                        contents.Add(new GoogleContent
                        {
                            Role = "model",
                            Parts = parts
                        });
                        continue;
                    }
                }

                var xmlToolReturns = ParseXmlToolReturns(contentText);
                if (xmlToolReturns.Count > 0)
                {
                    foreach (var tr in xmlToolReturns)
                    {
                        contents.Add(new GoogleContent
                        {
                            Role = "function",
                            Parts = new[]
                            {
                                new GooglePart
                                {
                                    FunctionResponse = new GoogleFunctionResponse
                                    {
                                        Name = tr.ToolName,
                                        Response = new Dictionary<string, object?> { { "output", tr.Content } }
                                    }
                                }
                            }
                        });
                    }
                    continue;
                }

                var normalParts = await BuildPartsAsync(
                    settings,
                    message,
                    inlineBudget,
                    preserveAuthorMetadata: true,
                    cancellationToken).ConfigureAwait(false);
                if (normalParts.Count > 0)
                {
                    contents.Add(new GoogleContent
                    {
                        Role = ToGoogleRole(message.Role),
                        Parts = normalParts
                    });
                }
            }

            return contents;
        }

        private static IReadOnlyList<GoogleTool>? MapGoogleTools(IReadOnlyList<FerritaPromptToolDefinition>? tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return null;
            }

            var declarations = new List<GoogleFunctionDeclaration>();
            foreach (var tool in tools)
            {
                var properties = new Dictionary<string, GoogleSchema>();
                var required = new List<string>();

                foreach (var p in tool.Parameters)
                {
                    var paramType = p.ParameterType switch
                    {
                        FerritaToolParameterType.Boolean => "BOOLEAN",
                        FerritaToolParameterType.Integer => "INTEGER",
                        FerritaToolParameterType.Number => "NUMBER",
                        _ => "STRING"
                    };

                    properties[p.Name] = new GoogleSchema
                    {
                        Type = paramType,
                        Description = p.Description
                    };

                    if (p.IsRequired)
                    {
                        required.Add(p.Name);
                    }
                }

                declarations.Add(new GoogleFunctionDeclaration
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = new GoogleSchema
                    {
                        Type = "OBJECT",
                        Properties = properties,
                        Required = required.Count > 0 ? required : null
                    }
                });
            }

            return new[]
            {
                new GoogleTool
                {
                    FunctionDeclarations = declarations
                }
            };
        }

        public async Task<int> CountTokensAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var settings = GetSettings(model);
            using var request = await CreateCountTokensRequestAsync(settings, messages, cancellationToken).ConfigureAwait(false);
            using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    LF("GoogleLanguageModel.Error.CountTokensFailedFormat", "Google countTokens request failed with status {0} ({1}). {2}", (int)response.StatusCode, response.ReasonPhrase, TryExtractErrorMessage(responsePayload)));
            }

            using var document = JsonDocument.Parse(responsePayload);
            if (document.RootElement.TryGetProperty("totalTokens", out var totalTokensElement) &&
                totalTokensElement.TryGetInt32(out var totalTokens) &&
                totalTokens > 0)
            {
                return totalTokens;
            }

            throw new InvalidOperationException(L("GoogleLanguageModel.Error.TotalTokensMissing", "Google countTokens response did not include totalTokens."));
        }

        public async IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            var settings = GetSettings(model);
            using var request = await CreateGenerateContentRequestAsync(
                    settings,
                    messages,
                    tools,
                    useStreamingEndpoint: true,
                    cancellationToken).ConfigureAwait(false);
            request.Headers.Accept.ParseAdd("text/event-stream");

            using var response = await s_httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(responseStream);
            var eventBuilder = new StringBuilder();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawLine = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (rawLine == null)
                {
                    break;
                }

                if (rawLine.Length == 0)
                {
                    if (eventBuilder.Length == 0)
                    {
                        continue;
                    }

                    var update = ParseStreamingEventPayload(eventBuilder.ToString(), settings.ModelId);
                    eventBuilder.Clear();
                    if (ShouldEmitStreamingUpdate(update))
                    {
                        yield return update;
                    }

                    continue;
                }

                if (rawLine.StartsWith(':'))
                {
                    continue;
                }

                if (!rawLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = rawLine[5..];
                if (payload.StartsWith(' '))
                {
                    payload = payload[1..];
                }

                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (eventBuilder.Length > 0)
                {
                    eventBuilder.AppendLine();
                }

                eventBuilder.Append(payload);
            }

            if (eventBuilder.Length > 0)
            {
                var trailingUpdate = ParseStreamingEventPayload(eventBuilder.ToString(), settings.ModelId);
                if (ShouldEmitStreamingUpdate(trailingUpdate))
                {
                    yield return trailingUpdate;
                }
            }
        }

        private static bool ShouldEmitStreamingUpdate(LanguageModelStreamingChatUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);

            return
                !string.IsNullOrEmpty(update.TextDelta) ||
                !string.IsNullOrEmpty(update.ReasoningTextDelta) ||
                !string.IsNullOrEmpty(update.RawText) ||
                update.WasTextSanitized ||
                !string.IsNullOrWhiteSpace(update.ModelId) ||
                !string.IsNullOrWhiteSpace(update.Role) ||
                !string.IsNullOrWhiteSpace(update.AuthorName) ||
                !string.IsNullOrWhiteSpace(update.FinishReason) ||
                !string.IsNullOrWhiteSpace(update.ResponseId) ||
                !string.IsNullOrWhiteSpace(update.MessageId) ||
                !string.IsNullOrWhiteSpace(update.ConversationId) ||
                update.CreatedAt != null ||
                !string.IsNullOrWhiteSpace(update.ContinuationToken) ||
                !string.IsNullOrWhiteSpace(update.RawRepresentationType) ||
                !string.IsNullOrWhiteSpace(update.RawRepresentationSummary) ||
                update.AdditionalProperties.Count > 0 ||
                update.Contents.Count > 0;
        }

        private static async Task<HttpRequestMessage> CreateGenerateContentRequestAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            IReadOnlyList<FerritaPromptToolDefinition>? tools,
            bool useStreamingEndpoint,
            CancellationToken cancellationToken)
        {
            var payload = await BuildRequestPayloadAsync(settings, messages, tools, cancellationToken).ConfigureAwait(false);
            var action = useStreamingEndpoint
                ? $"{NormalizeModelIdForPath(settings.ModelId)}:streamGenerateContent?alt=sse"
                : $"{NormalizeModelIdForPath(settings.ModelId)}:generateContent";
            var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUri(settings, $"v1beta/models/{action}"));
            request.Headers.Add("x-goog-api-key", settings.ApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, s_jsonOptions),
                Encoding.UTF8,
                "application/json");
            return request;
        }

        private static async Task<HttpRequestMessage> CreateCountTokensRequestAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken)
        {
            var payload = await BuildRequestPayloadAsync(settings, messages, null, cancellationToken).ConfigureAwait(false);
            var action = $"{NormalizeModelIdForPath(settings.ModelId)}:countTokens";
            var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUri(settings, $"v1beta/models/{action}"));
            request.Headers.Add("x-goog-api-key", settings.ApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, s_jsonOptions),
                Encoding.UTF8,
                "application/json");
            return request;
        }

        private static async Task<GoogleGenerateContentRequest> BuildRequestPayloadAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            IReadOnlyList<FerritaPromptToolDefinition>? tools,
            CancellationToken cancellationToken)
        {
            var inlineBudget = new InlinePayloadBudget(InlinePayloadBudgetBytes);
            var systemInstruction = await BuildSystemInstructionAsync(settings, messages, inlineBudget, cancellationToken).ConfigureAwait(false);
            
            bool useNativeTools = tools != null && tools.Count > 0;
            List<GoogleContent> contents;
            if (useNativeTools)
            {
                contents = await BuildContentsForJsonToolCallingAsync(settings, messages, inlineBudget, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                contents = await BuildContentsAsync(settings, messages, inlineBudget, cancellationToken).ConfigureAwait(false);
            }

            if (contents.Count == 0)
            {
                contents.Add(new GoogleContent
                {
                    Role = "user",
                    Parts = [new GooglePart { Text = string.Empty }]
                });
            }

            return new GoogleGenerateContentRequest
            {
                SystemInstruction = systemInstruction,
                Contents = contents,
                GenerationConfig = CreateGenerationConfig(settings),
                Tools = MapGoogleTools(tools)
            };
        }

        private static async Task<GoogleContent?> BuildSystemInstructionAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            InlinePayloadBudget inlineBudget,
            CancellationToken cancellationToken)
        {
            var parts = new List<GooglePart>();
            foreach (var message in messages.Where(message => message.Role == LanguageModelChatRole.System))
            {
                parts.AddRange(await BuildPartsAsync(
                        settings,
                        message,
                        inlineBudget,
                        preserveAuthorMetadata: false,
                        cancellationToken).ConfigureAwait(false));
            }

            return parts.Count == 0 ? null : new GoogleContent { Parts = parts };
        }

        private static async Task<List<GoogleContent>> BuildContentsAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            InlinePayloadBudget inlineBudget,
            CancellationToken cancellationToken)
        {
            var contents = new List<GoogleContent>();
            foreach (var message in messages.Where(message => message.Role != LanguageModelChatRole.System))
            {
                var parts = await BuildPartsAsync(
                        settings,
                        message,
                        inlineBudget,
                        preserveAuthorMetadata: true,
                        cancellationToken).ConfigureAwait(false);
                if (parts.Count == 0)
                {
                    continue;
                }

                contents.Add(new GoogleContent
                {
                    Role = ToGoogleRole(message.Role),
                    Parts = parts
                });
            }

            return contents;
        }

        private static async Task<List<GooglePart>> BuildPartsAsync(
            GoogleLanguageModelSettings settings,
            LanguageModelChatMessage message,
            InlinePayloadBudget inlineBudget,
            bool preserveAuthorMetadata,
            CancellationToken cancellationToken)
        {
            var parts = new List<GooglePart>();
            if (!string.IsNullOrEmpty(message.ReasoningContent))
            {
                parts.Add(new GooglePart
                {
                    Text = message.ReasoningContent,
                    Thought = true
                });
            }
            if (preserveAuthorMetadata && !string.IsNullOrWhiteSpace(message.AuthorName))
            {
                parts.Add(new GooglePart
                {
                    Text = $"[Author: {message.AuthorName.Trim()}]{Environment.NewLine}"
                });
            }

            foreach (var block in message.ContentBlocks)
            {
                switch (block.Kind)
                {
                    case LanguageModelChatContentBlockKind.Text:
                    case LanguageModelChatContentBlockKind.HostPreservedContent:
                        if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            parts.Add(new GooglePart { Text = block.Content });
                        }

                        break;

                    case LanguageModelChatContentBlockKind.Image:
                    case LanguageModelChatContentBlockKind.Audio:
                    case LanguageModelChatContentBlockKind.Video:
                    case LanguageModelChatContentBlockKind.Document:
                        var mediaPart = await TryCreateMediaPartAsync(
                                settings,
                                block,
                                inlineBudget,
                                cancellationToken).ConfigureAwait(false);
                        if (mediaPart != null)
                        {
                            parts.Add(mediaPart);
                        }
                        else if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            parts.Add(new GooglePart { Text = block.Content });
                        }

                        break;
                }
            }

            return parts;
        }

        private static async Task<GooglePart?> TryCreateMediaPartAsync(
            GoogleLanguageModelSettings settings,
            LanguageModelChatContentBlock block,
            InlinePayloadBudget inlineBudget,
            CancellationToken cancellationToken)
        {
            var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);
            if (mediaType.Length == 0)
            {
                return null;
            }

            var localPath = ResolveLocalPath(block);

            if (block.Data is { Length: > 0 } providedBytes)
            {
                var bytes = providedBytes.ToArray();
                var estimatedEncodedBytes = EstimateBase64Length(bytes.Length);
                if (estimatedEncodedBytes <= inlineBudget.RemainingBytes)
                {
                    inlineBudget.RemainingBytes -= estimatedEncodedBytes;
                    return new GooglePart
                    {
                        InlineData = new GoogleInlineData
                        {
                            MimeType = mediaType,
                            Data = Convert.ToBase64String(bytes)
                        }
                    };
                }

                var fileReference = await UploadFileAsync(
                        settings,
                        bytes,
                        mediaType,
                        BuildUploadDisplayName(block, localPath),
                        BuildUploadCacheKey(block, localPath, bytes),
                        cancellationToken).ConfigureAwait(false);
                return new GooglePart
                {
                    FileData = new GoogleFileData
                    {
                        MimeType = fileReference.MediaType,
                        FileUri = fileReference.FileUri
                    }
                };
            }

            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                var fileInfo = new FileInfo(localPath);
                var estimatedEncodedBytes = EstimateBase64Length(fileInfo.Length);
                if (estimatedEncodedBytes <= inlineBudget.RemainingBytes &&
                    TryReadInlineFileBytes(block, localPath, out var bytes) &&
                    bytes.Length > 0)
                {
                    inlineBudget.RemainingBytes -= estimatedEncodedBytes > int.MaxValue
                        ? inlineBudget.RemainingBytes
                        : (int)estimatedEncodedBytes;
                    return new GooglePart
                    {
                        InlineData = new GoogleInlineData
                        {
                            MimeType = mediaType,
                            Data = Convert.ToBase64String(bytes)
                        }
                    };
                }

                var fileReference = await UploadLocalFileAsync(
                        settings,
                        localPath,
                        mediaType,
                        BuildUploadDisplayName(block, localPath),
                        BuildUploadCacheKey(block, localPath),
                        cancellationToken).ConfigureAwait(false);
                return new GooglePart
                {
                    FileData = new GoogleFileData
                    {
                        MimeType = fileReference.MediaType,
                        FileUri = fileReference.FileUri
                    }
                };
            }

            return null;
        }

        private static string? ResolveLocalPath(LanguageModelChatContentBlock block)
        {
            var path = block.ResourcePath ?? block.Content;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmedPath = path.Trim();
            if (Uri.TryCreate(trimmedPath, UriKind.Absolute, out var uri))
            {
                return uri.IsFile ? uri.LocalPath : null;
            }

            return trimmedPath;
        }

        private static bool TryReadInlineFileBytes(
            LanguageModelChatContentBlock block,
            string? localPath,
            out byte[] bytes)
        {
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                if (!LanguageModelMediaResourcePolicy.CanReadLocalMediaFile(
                        localPath,
                        block.Kind,
                        block.MediaType,
                        out _,
                        out _))
                {
                    bytes = Array.Empty<byte>();
                    return false;
                }

                bytes = File.ReadAllBytes(localPath);
                return true;
            }

            bytes = Array.Empty<byte>();
            return false;
        }

        private static UploadedGoogleFileReference? TryGetCachedUploadedFile(string cacheKey)
        {
            return s_uploadedFileCache.TryGetValue(cacheKey, out var cachedReference)
                ? cachedReference
                : null;
        }

        private static async Task<UploadedGoogleFileReference> UploadLocalFileAsync(
            GoogleLanguageModelSettings settings,
            string localPath,
            string mediaType,
            string displayName,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            var cachedReference = TryGetCachedUploadedFile(cacheKey);
            if (cachedReference != null)
            {
                return cachedReference;
            }

            var fileInfo = new FileInfo(localPath);
            using var startRequest = new HttpRequestMessage(
                HttpMethod.Post,
                BuildApiUri(settings, "upload/v1beta/files"));
            startRequest.Headers.Add("x-goog-api-key", settings.ApiKey);
            startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            startRequest.Headers.Add("X-Goog-Upload-Command", "start");
            startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", fileInfo.Length.ToString(CultureInfo.InvariantCulture));
            startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mediaType);
            startRequest.Content = new StringContent(
                JsonSerializer.Serialize(new { file = new Dictionary<string, string> { ["display_name"] = displayName } }),
                Encoding.UTF8,
                "application/json");

            using var startResponse = await s_httpClient.SendAsync(
                    startRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(startResponse, cancellationToken).ConfigureAwait(false);

            if (!TryGetHeaderValue(startResponse, "X-Goog-Upload-URL", out var uploadUrl))
            {
                throw new InvalidOperationException("Google Files API did not return an upload URL.");
            }

            await using var stream = File.OpenRead(localPath);
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            uploadRequest.Content = new StreamContent(stream);
            uploadRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

            using var uploadResponse = await s_httpClient.SendAsync(
                    uploadRequest,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonDocumentAsync(uploadResponse, cancellationToken).ConfigureAwait(false);

            var fileReference = await ParseUploadedFileReferenceAsync(
                    settings,
                    document.RootElement,
                    mediaType,
                    cancellationToken).ConfigureAwait(false);
            s_uploadedFileCache[cacheKey] = fileReference;
            return fileReference;
        }

        private static async Task<UploadedGoogleFileReference> UploadFileAsync(
            GoogleLanguageModelSettings settings,
            byte[] bytes,
            string mediaType,
            string displayName,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            var cachedReference = TryGetCachedUploadedFile(cacheKey);
            if (cachedReference != null)
            {
                return cachedReference;
            }

            using var startRequest = new HttpRequestMessage(
                HttpMethod.Post,
                BuildApiUri(settings, "upload/v1beta/files"));
            startRequest.Headers.Add("x-goog-api-key", settings.ApiKey);
            startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            startRequest.Headers.Add("X-Goog-Upload-Command", "start");
            startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
            startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mediaType);
            startRequest.Content = new StringContent(
                JsonSerializer.Serialize(new { file = new Dictionary<string, string> { ["display_name"] = displayName } }),
                Encoding.UTF8,
                "application/json");

            using var startResponse = await s_httpClient.SendAsync(
                    startRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(startResponse, cancellationToken).ConfigureAwait(false);

            if (!TryGetHeaderValue(startResponse, "X-Goog-Upload-URL", out var uploadUrl))
            {
                throw new InvalidOperationException("Google Files API did not return an upload URL.");
            }

            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            uploadRequest.Content = new ByteArrayContent(bytes);
            uploadRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

            using var uploadResponse = await s_httpClient.SendAsync(
                    uploadRequest,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonDocumentAsync(uploadResponse, cancellationToken).ConfigureAwait(false);

            var fileReference = await ParseUploadedFileReferenceAsync(
                    settings,
                    document.RootElement,
                    mediaType,
                    cancellationToken).ConfigureAwait(false);
            s_uploadedFileCache[cacheKey] = fileReference;
            return fileReference;
        }

        private static async Task<UploadedGoogleFileReference> ParseUploadedFileReferenceAsync(
            GoogleLanguageModelSettings settings,
            JsonElement rootElement,
            string mediaType,
            CancellationToken cancellationToken)
        {
            var fileElement = rootElement.TryGetProperty("file", out var nestedFile) &&
                              nestedFile.ValueKind == JsonValueKind.Object
                ? nestedFile
                : rootElement;

            var fileUri = GetOptionalString(fileElement, "uri")
                ?? throw new InvalidOperationException("Google Files API upload response did not contain a file URI.");
            var fileName = GetOptionalString(fileElement, "name");
            var state = GetOptionalString(fileElement, "state");

            if (!string.IsNullOrWhiteSpace(fileName) &&
                !string.IsNullOrWhiteSpace(state) &&
                !string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return await WaitForFileActivationAsync(
                        settings,
                        fileName,
                        fileUri,
                        mediaType,
                        cancellationToken).ConfigureAwait(false);
            }

            return new UploadedGoogleFileReference(fileUri, mediaType, fileName);
        }

        private static async Task<UploadedGoogleFileReference> WaitForFileActivationAsync(
            GoogleLanguageModelSettings settings,
            string fileName,
            string initialFileUri,
            string mediaType,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < FileActivationPollingAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(FileActivationPollingDelayMilliseconds, cancellationToken).ConfigureAwait(false);

                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, BuildApiUri(settings, $"v1beta/{fileName.TrimStart('/')}"));
                pollRequest.Headers.Add("x-goog-api-key", settings.ApiKey);
                using var pollResponse = await s_httpClient.SendAsync(
                        pollRequest,
                        HttpCompletionOption.ResponseContentRead,
                        cancellationToken).ConfigureAwait(false);
                using var document = await ReadJsonDocumentAsync(pollResponse, cancellationToken).ConfigureAwait(false);

                var state = GetOptionalString(document.RootElement, "state");
                if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    return new UploadedGoogleFileReference(
                        GetOptionalString(document.RootElement, "uri") ?? initialFileUri,
                        mediaType,
                        GetOptionalString(document.RootElement, "name"));
                }

                if (string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Google Files API failed to process uploaded file '{fileName}'.");
                }
            }

            throw new InvalidOperationException($"Google Files API did not activate uploaded file '{fileName}' within the expected time.");
        }

        private static GoogleGenerationConfig? CreateGenerationConfig(GoogleLanguageModelSettings settings)
        {
            GoogleThinkingConfig? thinkingConfig = null;
            if (settings.IncludeThoughts || settings.UseThinkingBudget || settings.UseThinkingLevel)
            {
                thinkingConfig = new GoogleThinkingConfig
                {
                    IncludeThoughts = settings.IncludeThoughts,
                    ThinkingBudget = settings.UseThinkingBudget ? settings.ThinkingBudget : null,
                    ThinkingLevel = settings.UseThinkingLevel ? NormalizeThinkingLevel(settings.ThinkingLevel) : null
                };
            }

            if (!settings.UseTemperature &&
                !settings.UseTopP &&
                !settings.UseMaxOutputTokens &&
                thinkingConfig == null)
            {
                return null;
            }

            return new GoogleGenerationConfig
            {
                Temperature = settings.UseTemperature ? (double)settings.Temperature : null,
                TopP = settings.UseTopP ? (double)settings.TopP : null,
                MaxOutputTokens = settings.UseMaxOutputTokens ? settings.MaxOutputTokens : null,
                ThinkingConfig = thinkingConfig
            };
        }

        private static ParsedGoogleResponse ParseResponsePayload(JsonElement rootElement, string fallbackModelId)
        {
            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            List<LanguageModelStreamingContentDebugItem>? debugItems = null;
            var toolCalls = new List<LanguageModelToolCall>();

            if (rootElement.TryGetProperty("candidates", out var candidatesElement) &&
                candidatesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidateElement in candidatesElement.EnumerateArray())
                {
                    var contentElement = candidateElement.TryGetProperty("content", out var nestedContent)
                        ? nestedContent
                        : default;
                    if (contentElement.ValueKind != JsonValueKind.Object ||
                        !contentElement.TryGetProperty("parts", out var partsElement) ||
                        partsElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var partElement in partsElement.EnumerateArray())
                    {
                        if (partElement.TryGetProperty("functionCall", out var funcCallElement) &&
                            funcCallElement.ValueKind == JsonValueKind.Object)
                        {
                            var name = GetOptionalString(funcCallElement, "name") ?? string.Empty;
                            var argsJson = string.Empty;
                            if (funcCallElement.TryGetProperty("args", out var argsElement))
                            {
                                argsJson = JsonSerializer.Serialize(argsElement, s_jsonOptions);
                            }
                            toolCalls.Add(new LanguageModelToolCall
                            {
                                Name = name,
                                ArgumentsJson = argsJson,
                                Id = "tc_" + Guid.NewGuid().ToString("N")[..8]
                            });
                        }
                        else
                        {
                            AppendPartText(partElement, textBuilder, reasoningBuilder, debugItems);
                        }
                    }
                }
            }

            return new ParsedGoogleResponse(
                SanitizeModelText(textBuilder.ToString()),
                SanitizeModelText(reasoningBuilder.ToString()),
                GetOptionalString(rootElement, "modelVersion") ?? fallbackModelId,
                debugItems ?? (IReadOnlyList<LanguageModelStreamingContentDebugItem>)Array.Empty<LanguageModelStreamingContentDebugItem>(),
                NormalizeUsageCount(GetUsageMetadataNumber(rootElement, "promptTokenCount")),
                NormalizeUsageCount(GetUsageMetadataNumber(rootElement, "totalTokenCount")),
                toolCalls);
        }

        private static LanguageModelStreamingChatUpdate ParseStreamingEventPayload(string payload, string fallbackModelId)
        {
            using var document = JsonDocument.Parse(payload);
            var parsed = ParseResponsePayload(document.RootElement, fallbackModelId);

            var responseId = GetOptionalString(document.RootElement, "responseId");
            var rawText = parsed.Text;
            return new LanguageModelStreamingChatUpdate
            {
                TextDelta = parsed.Text,
                ReasoningTextDelta = parsed.ReasoningText,
                ModelId = parsed.ModelId,
                RawText = string.Equals(rawText, parsed.Text, StringComparison.Ordinal) ? null : rawText,
                WasTextSanitized = !string.Equals(rawText, parsed.Text, StringComparison.Ordinal),
                Role = "assistant",
                FinishReason = GetFirstCandidateProperty(document.RootElement, "finishReason"),
                ResponseId = responseId,
                AdditionalProperties = BuildStreamingAdditionalProperties(document.RootElement)
            };
        }

        private static IReadOnlyDictionary<string, object?> BuildStreamingAdditionalProperties(JsonElement rootElement)
        {
            var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var promptTokenCount = GetUsageMetadataNumber(rootElement, "promptTokenCount");
            var candidatesTokenCount = GetUsageMetadataNumber(rootElement, "candidatesTokenCount");
            var thoughtsTokenCount = GetUsageMetadataNumber(rootElement, "thoughtsTokenCount");

            if (promptTokenCount != null)
            {
                normalized["promptTokenCount"] = promptTokenCount.Value;
            }

            if (candidatesTokenCount != null)
            {
                normalized["candidatesTokenCount"] = candidatesTokenCount.Value;
            }

            if (thoughtsTokenCount != null)
            {
                normalized["thoughtsTokenCount"] = thoughtsTokenCount.Value;
            }

            return normalized;
        }

        private static void AppendPartText(
            JsonElement partElement,
            StringBuilder textBuilder,
            StringBuilder reasoningBuilder,
            ICollection<LanguageModelStreamingContentDebugItem>? debugItems)
        {
            var isThought = partElement.TryGetProperty("thought", out var thoughtElement) &&
                            thoughtElement.ValueKind == JsonValueKind.True;
            // Google may stream leading or trailing spaces as their own part.text fragments.
            // Preserve those verbatim so token boundaries do not collapse during reassembly.
            var text = GetOptionalString(partElement, "text", trim: false) ?? string.Empty;
            var sanitizedText = SanitizeModelText(text);

            if (text.Length > 0)
            {
                if (isThought)
                {
                    reasoningBuilder.Append(sanitizedText);
                }
                else
                {
                    textBuilder.Append(sanitizedText);
                }
            }

            if (debugItems != null && partElement.ValueKind == JsonValueKind.Object)
            {
                var additionalProperties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var thoughtSignature = GetOptionalString(partElement, "thoughtSignature");
                if (!string.IsNullOrWhiteSpace(thoughtSignature))
                {
                    additionalProperties["thoughtSignature"] = thoughtSignature;
                }

                debugItems.Add(new LanguageModelStreamingContentDebugItem
                {
                    ContentType = isThought ? "Thought" : "Text",
                    Text = sanitizedText.Length == 0 ? null : sanitizedText,
                    Summary = sanitizedText.Length == 0 ? (isThought ? "Thought part" : "Text part") : TruncateText(sanitizedText, 512),
                    RawRepresentationType = typeof(JsonElement).FullName,
                    RawRepresentationSummary = TruncateText(partElement.GetRawText(), 512),
                    AdditionalProperties = additionalProperties
                });
            }
        }

        private static async Task<JsonDocument> ReadJsonDocumentAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var errorMessage = TryExtractErrorMessage(payload);
            throw new InvalidOperationException(
                LF("GoogleLanguageModel.Error.RequestFailedFormat", "Google API request failed with status {0} ({1}). {2}", (int)response.StatusCode, response.ReasonPhrase, errorMessage));
        }

        private static string TryExtractErrorMessage(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return L("GoogleLanguageModel.Error.NoErrorPayload", "No error payload was returned.");
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var message = GetOptionalString(errorElement, "message");
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message.Trim();
                    }
                }
            }
            catch (JsonException)
            {
            }

            return TruncateText(payload.Trim(), 512) ?? L("GoogleLanguageModel.Error.Unknown", "Unknown Google API error.");
        }

        private static bool TryGetHeaderValue(HttpResponseMessage response, string headerName, out string headerValue)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                headerValue = values.FirstOrDefault()?.Trim() ?? string.Empty;
                if (headerValue.Length > 0)
                {
                    return true;
                }
            }

            if (response.Content.Headers.TryGetValues(headerName, out values))
            {
                headerValue = values.FirstOrDefault()?.Trim() ?? string.Empty;
                return headerValue.Length > 0;
            }

            headerValue = string.Empty;
            return false;
        }

        private static string BuildUploadDisplayName(LanguageModelChatContentBlock block, string? localPath)
        {
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                return Path.GetFileName(localPath);
            }

            return block.Kind switch
            {
                LanguageModelChatContentBlockKind.Audio => "audio",
                LanguageModelChatContentBlockKind.Video => "video",
                LanguageModelChatContentBlockKind.Document => "document",
                _ => "image"
            };
        }

        private static string BuildUploadCacheKey(
            LanguageModelChatContentBlock block,
            string? localPath,
            byte[] bytes)
        {
            var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                var fileInfo = new FileInfo(localPath);
                return $"{localPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}|{mediaType}";
            }

            return $"{mediaType}|{Convert.ToHexString(SHA256.HashData(bytes))}";
        }

        private static string BuildUploadCacheKey(
            LanguageModelChatContentBlock block,
            string localPath)
        {
            var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);
            var fileInfo = new FileInfo(localPath);
            return $"{localPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}|{mediaType}";
        }

        private static Uri BuildApiUri(GoogleLanguageModelSettings settings, string relativePath)
        {
            return new Uri(new Uri(NormalizeBaseUrl(settings.BaseUrl)), relativePath);
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var normalized = baseUrl.Trim();
            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return normalized;
        }

        private static string NormalizeModelIdForPath(string modelId)
        {
            var normalized = modelId.Trim();
            return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? normalized["models/".Length..]
                : normalized;
        }

        private static string NormalizeMediaType(
            string? mediaType,
            string? pathOrUri,
            LanguageModelChatContentBlockKind kind)
        {
            return LanguageModelMediaResourcePolicy.TryNormalizeMediaType(
                kind,
                pathOrUri,
                mediaType,
                out var normalizedMediaType)
                ? normalizedMediaType
                : string.Empty;
        }

        private static GoogleLanguageModelSettings GetSettings(LanguageModelDefinition model)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (model.InterfaceSettings is not GoogleLanguageModelSettings settings)
            {
                throw new InvalidOperationException($"Current interface type {model.InterfaceType} does not support Google chat calls.");
            }

            if (string.IsNullOrWhiteSpace(settings.ModelId))
            {
                throw new InvalidOperationException("Model ID cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                throw new InvalidOperationException("API Key cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                throw new InvalidOperationException("Base URL cannot be empty.");
            }

            return settings;
        }

        private static string ToGoogleRole(LanguageModelChatRole role)
        {
            return role switch
            {
                LanguageModelChatRole.Assistant => "model",
                _ => "user"
            };
        }

        private static string? GetOptionalString(
            JsonElement element,
            string propertyName,
            bool trim = true)
        {
            if (!element.TryGetProperty(propertyName, out var propertyElement))
            {
                return null;
            }

            if (propertyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = propertyElement.GetString();
            return trim ? value?.Trim() : value;
        }

        private static string? GetFirstCandidateProperty(JsonElement rootElement, string propertyName)
        {
            if (!rootElement.TryGetProperty("candidates", out var candidatesElement) ||
                candidatesElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var candidateElement in candidatesElement.EnumerateArray())
            {
                var value = GetOptionalString(candidateElement, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static long? GetUsageMetadataNumber(JsonElement rootElement, string propertyName)
        {
            if (!rootElement.TryGetProperty("usageMetadata", out var usageMetadataElement) ||
                usageMetadataElement.ValueKind != JsonValueKind.Object ||
                !usageMetadataElement.TryGetProperty(propertyName, out var propertyElement))
            {
                return null;
            }

            if (propertyElement.ValueKind == JsonValueKind.Number && propertyElement.TryGetInt64(out var numericValue))
            {
                return numericValue;
            }

            return null;
        }

        private static int? NormalizeUsageCount(long? value)
        {
            if (value is not long count || count <= 0)
            {
                return null;
            }

            return count > int.MaxValue ? int.MaxValue : (int)count;
        }

        private static int EstimateBase64Length(int byteCount)
        {
            if (byteCount <= 0)
            {
                return 0;
            }

            return ((byteCount + 2) / 3) * 4;
        }

        private static long EstimateBase64Length(long byteCount)
        {
            if (byteCount <= 0)
            {
                return 0;
            }

            return ((byteCount + 2) / 3) * 4;
        }

        private static string SanitizeModelText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            foreach (var character in text)
            {
                if (character is '\r' or '\n' or '\t')
                {
                    builder.Append(character);
                    continue;
                }

                if (char.IsControl(character))
                {
                    continue;
                }

                builder.Append(character);
            }

            return builder.ToString();
        }

        private static string NormalizeThinkingLevel(string thinkingLevel)
        {
            return (thinkingLevel ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "MINIMAL" => "MINIMAL",
                "LOW" => "LOW",
                "MEDIUM" => "MEDIUM",
                "HIGH" => "HIGH",
                _ => "HIGH"
            };
        }

        private static string? TruncateText(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (maxLength <= 0 || value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + "...";
        }

        private static string L(string resourceKey, string fallback)
        {
            return LocalizationRuntime.Instance.GetString(resourceKey, fallback);
        }

        private static string LF(string resourceKey, string fallbackFormat, params object?[] args)
        {
            return string.Format(L(resourceKey, fallbackFormat), args);
        }

        private sealed class InlinePayloadBudget
        {
            public InlinePayloadBudget(int remainingBytes)
            {
                RemainingBytes = remainingBytes;
            }

            public int RemainingBytes { get; set; }
        }

        private sealed record ParsedGoogleResponse(
            string Text,
            string ReasoningText,
            string ModelId,
            IReadOnlyList<LanguageModelStreamingContentDebugItem> DebugItems,
            int? InputTokenCount,
            int? TotalTokenCount,
            IReadOnlyList<LanguageModelToolCall> ToolCalls);

        private sealed record UploadedGoogleFileReference(
            string FileUri,
            string MediaType,
            string? Name);

        private sealed class GoogleGenerateContentRequest
        {
            [JsonPropertyName("systemInstruction")]
            public GoogleContent? SystemInstruction { get; init; }

            [JsonPropertyName("contents")]
            public IReadOnlyList<GoogleContent> Contents { get; init; } = Array.Empty<GoogleContent>();

            [JsonPropertyName("generationConfig")]
            public GoogleGenerationConfig? GenerationConfig { get; init; }

            [JsonPropertyName("tools")]
            public IReadOnlyList<GoogleTool>? Tools { get; init; }
        }

        private sealed class GoogleContent
        {
            [JsonPropertyName("role")]
            public string? Role { get; set; }

            [JsonPropertyName("parts")]
            public IReadOnlyList<GooglePart> Parts { get; set; } = Array.Empty<GooglePart>();
        }

        private sealed class GooglePart
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("inlineData")]
            public GoogleInlineData? InlineData { get; set; }

            [JsonPropertyName("fileData")]
            public GoogleFileData? FileData { get; set; }

            [JsonPropertyName("functionCall")]
            public GoogleFunctionCall? FunctionCall { get; set; }

            [JsonPropertyName("functionResponse")]
            public GoogleFunctionResponse? FunctionResponse { get; set; }

            [JsonPropertyName("thought")]
            public bool? Thought { get; set; }
        }

        private sealed class GoogleInlineData
        {
            [JsonPropertyName("mimeType")]
            public string MimeType { get; init; } = string.Empty;

            [JsonPropertyName("data")]
            public string Data { get; init; } = string.Empty;
        }

        private sealed class GoogleFileData
        {
            [JsonPropertyName("mimeType")]
            public string MimeType { get; init; } = string.Empty;

            [JsonPropertyName("fileUri")]
            public string FileUri { get; init; } = string.Empty;
        }

        private sealed class GoogleGenerationConfig
        {
            [JsonPropertyName("temperature")]
            public double? Temperature { get; init; }

            [JsonPropertyName("topP")]
            public double? TopP { get; init; }

            [JsonPropertyName("maxOutputTokens")]
            public int? MaxOutputTokens { get; init; }

            [JsonPropertyName("thinkingConfig")]
            public GoogleThinkingConfig? ThinkingConfig { get; init; }
        }

        private sealed class GoogleThinkingConfig
        {
            [JsonPropertyName("includeThoughts")]
            public bool? IncludeThoughts { get; init; }

            [JsonPropertyName("thinkingBudget")]
            public int? ThinkingBudget { get; init; }

            [JsonPropertyName("thinkingLevel")]
            public string? ThinkingLevel { get; init; }
        }

        private sealed class GoogleTool
        {
            [JsonPropertyName("functionDeclarations")]
            public IReadOnlyList<GoogleFunctionDeclaration>? FunctionDeclarations { get; init; }
        }

        private sealed class GoogleFunctionDeclaration
        {
            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("description")]
            public string Description { get; init; } = string.Empty;

            [JsonPropertyName("parameters")]
            public GoogleSchema? Parameters { get; init; }
        }

        private sealed class GoogleSchema
        {
            [JsonPropertyName("type")]
            public string Type { get; init; } = "OBJECT";

            [JsonPropertyName("properties")]
            public IDictionary<string, GoogleSchema>? Properties { get; init; }

            [JsonPropertyName("required")]
            public IReadOnlyList<string>? Required { get; init; }

            [JsonPropertyName("description")]
            public string? Description { get; init; }
        }

        private sealed class GoogleFunctionCall
        {
            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("args")]
            public object? Args { get; init; }
        }

        private sealed class GoogleFunctionResponse
        {
            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("response")]
            public object? Response { get; init; }
        }
    }
}
