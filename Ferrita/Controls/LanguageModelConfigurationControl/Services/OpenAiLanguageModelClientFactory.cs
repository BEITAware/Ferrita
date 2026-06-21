#pragma warning disable OPENAI001
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    internal sealed class OpenAiLanguageModelInterfaceAdapter : ILanguageModelInterfaceAdapter
    {
        public string InterfaceType => "OpenAI";

        private static readonly HttpClient s_httpClient = new();

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

        public LanguageModelInterfaceSettings CreateInterfaceSettings()
        {
            return new OpenAiLanguageModelSettings();
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
            bool useNativeTools = tools != null && tools.Count > 0;
            
            ChatMessage[] sdkMessages;
            if (useNativeTools)
            {
                sdkMessages = ProjectMessagesForJsonToolCalling(messages).ToArray();
            }
            else
            {
                sdkMessages = messages.Select(ToSdkMessage).ToArray();
            }

            var options = CreateChatOptions(settings, tools);
            var client = CreateChatClient(settings);
            
            ClientResult<ChatCompletion> result = await client.CompleteChatAsync(sdkMessages, options, cancellationToken).ConfigureAwait(false);
            ChatCompletion response = result.Value;

            var toolCalls = new List<LanguageModelToolCall>();
            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();

            if (response.Content != null)
            {
                foreach (var part in response.Content)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        textBuilder.Append(part.Text);
                    }
                }
            }

            string? reasoningText = null;
            try
            {
                var prop = response.GetType().GetProperty("ReasoningContent");
                if (prop != null)
                {
                    reasoningText = prop.GetValue(response) as string;
                }
            }
            catch {}

            if (!string.IsNullOrEmpty(reasoningText))
            {
                reasoningBuilder.Append(reasoningText);
            }

            if (response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                foreach (var tc in response.ToolCalls)
                {
                    toolCalls.Add(new LanguageModelToolCall
                    {
                        Name = tc.FunctionName,
                        ArgumentsJson = tc.FunctionArguments.ToString(),
                        Id = tc.Id
                    });
                }
            }

            var finalResponseText = textBuilder.ToString();

            // 如果有原生工具调用，把原生工具调用翻译成 XML 作为响应内容返回给上层！
            if (useNativeTools && toolCalls.Count > 0)
            {
                var xmlBuilder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(finalResponseText))
                {
                    xmlBuilder.Append(finalResponseText);
                    xmlBuilder.Append("\n\n");
                }
                
                foreach (var tc in toolCalls)
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
                
                finalResponseText = xmlBuilder.ToString();
            }

            return new LanguageModelChatResponse
            {
                Text = SanitizeModelText(finalResponseText),
                ReasoningText = reasoningBuilder.Length > 0 ? SanitizeModelText(reasoningBuilder.ToString()) : string.Empty,
                ModelId = response.Model,
                InputTokenCount = response.Usage?.InputTokenCount,
                TotalTokenCount = response.Usage?.TotalTokenCount
            };
        }

        private static IReadOnlyList<LanguageModelToolCall> ParseXmlToolCalls(string xmlText)
        {
            var results = new List<LanguageModelToolCall>();
            if (string.IsNullOrEmpty(xmlText))
            {
                return results;
            }

            var standardMatches = Regex.Matches(xmlText, @"<(Tool|ToolAsync)\s+[^>]*ToolName\s*=\s*(?:""(?<name>[^""]*)""|'(?<name>[^']*)')[^>]*>(?<body>.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in standardMatches)
            {
                var toolName = match.Groups["name"].Value;
                var body = match.Groups["body"].Value;
                AddToolCall(results, match.Value, toolName, body, null);
            }

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

            clean = Regex.Replace(clean, @"<(Tool|ToolAsync)\s+[^>]*ToolName\s*=\s*(?:""[^""]*""|'[^']*')[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"<(tool_call|Tool_call)(?:\s+[^>]*)?>.*?</\1>", "", RegexOptions.Singleline);

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

        private static IReadOnlyList<ChatMessage> ProjectMessagesForJsonToolCalling(
            IReadOnlyList<LanguageModelChatMessage> sourceMessages)
        {
            var projected = new List<ChatMessage>();
            
            foreach (var msg in sourceMessages)
            {
                var role = msg.Role;
                var content = msg.Content;

                if (role == LanguageModelChatRole.Assistant)
                {
                    var xmlToolCalls = ParseXmlToolCalls(content);
                    if (xmlToolCalls.Count > 0)
                    {
                        var cleanText = StripXmlToolCalls(content);
                        var sdkToolCalls = new List<ChatToolCall>();

                        foreach (var tc in xmlToolCalls)
                        {
                            var arguments = new Dictionary<string, object?>();
                            if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                            {
                                try
                                {
                                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson) ?? arguments;
                                }
                                catch {}
                            }
                            var argumentsJson = JsonSerializer.Serialize(arguments);
                            sdkToolCalls.Add(ChatToolCall.CreateFunctionToolCall(tc.Id, tc.Name, BinaryData.FromString(argumentsJson)));
                        }

                        var assistantMsg = new AssistantChatMessage(cleanText);
                        foreach (var tc in sdkToolCalls)
                        {
                            assistantMsg.ToolCalls.Add(tc);
                        }

                        projected.Add(assistantMsg);
                        continue;
                    }
                }
                
                var xmlToolReturns = ParseXmlToolReturns(content);
                if (xmlToolReturns.Count > 0)
                {
                    foreach (var tr in xmlToolReturns)
                    {
                        var toolMsg = new ToolChatMessage(tr.ToolCallId, tr.Content);
                        projected.Add(toolMsg);
                    }
                    continue;
                }

                projected.Add(ToSdkMessage(msg));
            }

            return projected;
        }

        public async Task<int> CountTokensAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var settings = GetSettings(model);

            try
            {
                var inputItems = new List<object>();
                foreach (var message in messages)
                {
                    var role = message.IsHostInjectedTail
                        ? "system"
                        : message.Role.ToString().ToLowerInvariant();

                    if (message.ContentBlocks == null || message.ContentBlocks.Count == 0)
                    {
                        inputItems.Add(new
                        {
                            type = "message",
                            role = role,
                            content = message.Content ?? string.Empty
                        });
                    }
                    else
                    {
                        var contentParts = new List<object>();
                        foreach (var block in message.ContentBlocks)
                        {
                            switch (block.Kind)
                            {
                                case LanguageModelChatContentBlockKind.Text:
                                case LanguageModelChatContentBlockKind.HostPreservedContent:
                                    if (!string.IsNullOrWhiteSpace(block.Content))
                                    {
                                        contentParts.Add(new { type = "text", text = block.Content });
                                    }
                                    break;
                                case LanguageModelChatContentBlockKind.Image:
                                    if (block.Data is { Length: > 0 } bytes)
                                    {
                                        var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);
                                        var base64 = Convert.ToBase64String(bytes);
                                        contentParts.Add(new
                                        {
                                            type = "image",
                                            image = $"data:{mediaType};base64,{base64}"
                                        });
                                    }
                                    else
                                    {
                                        var rawUri = block.ResourcePath ?? block.Content;
                                        if (!string.IsNullOrWhiteSpace(rawUri))
                                        {
                                            contentParts.Add(new
                                            {
                                                type = "image",
                                                image = rawUri
                                            });
                                        }
                                    }
                                    break;
                            }
                        }

                        inputItems.Add(new
                        {
                            type = "message",
                            role = role,
                            content = contentParts
                        });
                    }
                }

                var payload = new
                {
                    model = settings.ModelId,
                    input = inputItems
                };

                var url = new Uri(new Uri(NormalizeBaseUrl(settings.BaseUrl)), "responses/input_tokens");
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);

                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"OpenAI token counting endpoint failed with status {response.StatusCode} ({response.ReasonPhrase}).");
                }

                var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responsePayload);
                if (doc.RootElement.TryGetProperty("input_tokens", out var inputTokensProp) &&
                    inputTokensProp.TryGetInt32(out var count))
                {
                    return count;
                }

                throw new InvalidOperationException("OpenAI token counting endpoint did not return input token usage.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("OpenAI token counting endpoint failed.", ex);
            }
        }

        public async IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            var settings = GetSettings(model);
            bool useNativeTools = tools != null && tools.Count > 0;

            ChatMessage[] sdkMessages;
            if (useNativeTools)
            {
                sdkMessages = ProjectMessagesForJsonToolCalling(messages).ToArray();
            }
            else
            {
                sdkMessages = messages.Select(ToSdkMessage).ToArray();
            }

            var options = CreateChatOptions(settings, tools);
            var client = CreateChatClient(settings);

            var toolCallAccumulators = new Dictionary<string, ToolCallAccumulator>();
            int fallbackIdCounter = 0;

            AsyncCollectionResult<StreamingChatCompletionUpdate> updates = client.CompleteChatStreamingAsync(sdkMessages, options, cancellationToken);

            await foreach (var update in updates.ConfigureAwait(false))
            {
                if (useNativeTools && update.ToolCallUpdates != null)
                {
                    foreach (var tcUpdate in update.ToolCallUpdates)
                    {
                        var id = tcUpdate.ToolCallId;
                        if (string.IsNullOrEmpty(id))
                        {
                            id = tcUpdate.FunctionName;
                        }
                        if (string.IsNullOrEmpty(id))
                        {
                            id = $"tc_fallback_{fallbackIdCounter++}";
                        }

                        if (!toolCallAccumulators.TryGetValue(id, out var accum))
                        {
                            accum = new ToolCallAccumulator { Id = id };
                            toolCallAccumulators[id] = accum;
                        }

                        if (!string.IsNullOrEmpty(tcUpdate.FunctionName))
                        {
                            accum.Name = tcUpdate.FunctionName;
                        }

                        if (tcUpdate.FunctionArgumentsUpdate != null)
                        {
                            var argChunk = tcUpdate.FunctionArgumentsUpdate.ToString();
                            if (!string.IsNullOrEmpty(argChunk))
                            {
                                accum.ArgumentsBuilder.Append(argChunk);
                            }
                        }
                    }
                }

                var streamingUpdate = BuildStreamingUpdate(update);
                if (!ShouldEmitStreamingUpdate(streamingUpdate))
                {
                    continue;
                }

                yield return streamingUpdate;
            }

            if (useNativeTools && toolCallAccumulators.Count > 0)
            {
                var xmlBuilder = new StringBuilder();
                foreach (var kvp in toolCallAccumulators)
                {
                    var accum = kvp.Value;
                    var name = accum.Name;
                    var argsJson = accum.ArgumentsBuilder.ToString();
                    var id = accum.Id;

                    xmlBuilder.Append($"<Tool ToolName=\"{name}\" ToolCallID=\"{id}\">");
                    if (!string.IsNullOrEmpty(argsJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(argsJson);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                xmlBuilder.Append($"<{prop.Name}>");
                                if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    xmlBuilder.Append(prop.Value.GetString());
                                }
                                else
                                {
                                    xmlBuilder.Append(prop.Value.GetRawText());
                                }
                                xmlBuilder.Append($"</{prop.Name}>");
                            }
                        }
                        catch
                        {
                            xmlBuilder.Append(argsJson);
                        }
                    }
                    xmlBuilder.Append("</Tool>");
                }

                yield return new LanguageModelStreamingChatUpdate
                {
                    TextDelta = "\n\n" + xmlBuilder.ToString(),
                    Role = "assistant"
                };
            }
        }

        private class ToolCallAccumulator
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public StringBuilder ArgumentsBuilder { get; } = new();
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

        private static string ExtractText(StreamingChatCompletionUpdate update)
        {
            var sb = new StringBuilder();
            if (update.ContentUpdate != null)
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        sb.Append(part.Text);
                    }
                }
            }
            return sb.ToString();
        }

        private static string ExtractReasoningText(StreamingChatCompletionUpdate update)
        {
            try
            {
                var prop = update.GetType().GetProperty("ReasoningContentUpdate");
                if (prop != null)
                {
                    return prop.GetValue(update) as string ?? string.Empty;
                }
            }
            catch {}
            return string.Empty;
        }

        private static LanguageModelStreamingChatUpdate BuildStreamingUpdate(StreamingChatCompletionUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);

            var rawText = ExtractText(update);
            var sanitizedText = SanitizeModelText(rawText);
            var rawReasoningText = ExtractReasoningText(update);
            var sanitizedReasoningText = SanitizeModelText(rawReasoningText);

            string? model = null;
            try { model = update.GetType().GetProperty("Model")?.GetValue(update) as string; } catch {}
            
            string? role = null;
            try { role = update.Role?.ToString(); } catch {}

            string? finishReason = null;
            try { finishReason = update.FinishReason?.ToString(); } catch {}

            string? responseId = null;
            try { responseId = update.GetType().GetProperty("ResponseId")?.GetValue(update) as string; } catch {}

            DateTimeOffset? createdAt = null;
            try { createdAt = update.GetType().GetProperty("CreatedAt")?.GetValue(update) as DateTimeOffset?; } catch {}

            return new LanguageModelStreamingChatUpdate
            {
                TextDelta = sanitizedText,
                ReasoningTextDelta = sanitizedReasoningText,
                ModelId = NormalizeMetadataText(model),
                RawText = string.Equals(rawText ?? string.Empty, sanitizedText, StringComparison.Ordinal)
                    ? null
                    : rawText,
                WasTextSanitized = !string.Equals(rawText ?? string.Empty, sanitizedText, StringComparison.Ordinal),
                Role = NormalizeMetadataText(role),
                FinishReason = NormalizeMetadataText(finishReason),
                ResponseId = NormalizeMetadataText(responseId),
                CreatedAt = createdAt
            };
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
                !string.IsNullOrWhiteSpace(update.FinishReason) ||
                !string.IsNullOrWhiteSpace(update.ResponseId) ||
                update.CreatedAt != null;
        }

        private static string? NormalizeMetadataText(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static ChatClient CreateChatClient(OpenAiLanguageModelSettings settings)
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(NormalizeBaseUrl(settings.BaseUrl))
            };

            var credential = new ApiKeyCredential(settings.ApiKey);
            return new ChatClient(settings.ModelId, credential, clientOptions);
        }

        private static ChatCompletionOptions CreateChatOptions(OpenAiLanguageModelSettings settings, IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            var options = new ChatCompletionOptions();

            if (settings.UseTemperature)
            {
                options.Temperature = (float)settings.Temperature;
            }

            if (settings.UseTopP)
            {
                options.TopP = (float)settings.TopP;
            }

            if (settings.UseMaxOutputTokens)
            {
                options.MaxOutputTokenCount = settings.MaxOutputTokens;
            }

            if (settings.UsePresencePenalty)
            {
                options.PresencePenalty = (float)settings.PresencePenalty;
            }

            if (settings.UseFrequencyPenalty)
            {
                options.FrequencyPenalty = (float)settings.FrequencyPenalty;
            }

            if (settings.UseSeed)
            {
                options.Seed = settings.Seed;
            }

            if (settings.UseReasoningEffort)
            {
                var effort = ParseReasoningEffort(settings.ReasoningEffort);
                if (effort != null)
                {
                    options.ReasoningEffortLevel = effort.Value;
                }
            }

            if (tools != null && tools.Count > 0)
            {
                foreach (var tool in tools)
                {
                    var schemaObj = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() }
                    };

                    var properties = (Dictionary<string, object>)schemaObj["properties"];
                    var requiredList = new List<string>();

                    foreach (var p in tool.Parameters)
                    {
                        var paramType = p.ParameterType switch
                        {
                            FerritaToolParameterType.Boolean => "boolean",
                            FerritaToolParameterType.Integer => "integer",
                            FerritaToolParameterType.Number => "number",
                            _ => "string"
                        };

                        properties[p.Name] = new Dictionary<string, object>
                        {
                            { "type", paramType },
                            { "description", p.Description ?? string.Empty }
                        };

                        if (p.IsRequired)
                        {
                            requiredList.Add(p.Name);
                        }
                    }

                    if (requiredList.Count > 0)
                    {
                        schemaObj["required"] = requiredList;
                    }

                    var jsonString = JsonSerializer.Serialize(schemaObj);
                    var parameters = BinaryData.FromString(jsonString);

                    options.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description, parameters));
                }
            }

            return options;
        }

        private static ChatCompletionOptions CreateTokenCountingChatOptions(OpenAiLanguageModelSettings settings)
        {
            var options = CreateChatOptions(settings, null);
            options.MaxOutputTokenCount = 1;
            return options;
        }

        private static ChatMessage ToSdkMessage(LanguageModelChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var contents = BuildSdkContents(message).ToList();
            if (contents.Count == 0 && !string.IsNullOrEmpty(message.Content))
            {
                contents.Add(ChatMessageContentPart.CreateTextPart(message.Content));
            }

            var role = message.IsHostInjectedTail
                ? "system"
                : message.Role.ToString().ToLowerInvariant();

            ChatMessage sdkMessage = role switch
            {
                "system" => new SystemChatMessage(contents) { ParticipantName = message.AuthorName },
                "assistant" => new AssistantChatMessage(contents) { ParticipantName = message.AuthorName },
                _ => new UserChatMessage(contents) { ParticipantName = message.AuthorName }
            };

            return sdkMessage;
        }

        private static IList<ChatMessageContentPart> BuildSdkContents(LanguageModelChatMessage message)
        {
            if (message.ContentBlocks.Count == 0)
            {
                return Array.Empty<ChatMessageContentPart>();
            }

            var contents = new List<ChatMessageContentPart>();
            foreach (var block in message.ContentBlocks)
            {
                switch (block.Kind)
                {
                    case LanguageModelChatContentBlockKind.Text:
                    case LanguageModelChatContentBlockKind.HostPreservedContent:
                        if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            contents.Add(ChatMessageContentPart.CreateTextPart(block.Content));
                        }

                        break;

                    case LanguageModelChatContentBlockKind.Image:
                    case LanguageModelChatContentBlockKind.Audio:
                    case LanguageModelChatContentBlockKind.Video:
                    case LanguageModelChatContentBlockKind.Document:
                        if (TryCreateDataContent(block, out var dataContent) && dataContent != null)
                        {
                            contents.Add(dataContent);
                        }
                        else if (TryCreateUriContent(block, out var uriContent) && uriContent != null)
                        {
                            contents.Add(uriContent);
                        }
                        else if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            contents.Add(ChatMessageContentPart.CreateTextPart(block.Content));
                        }

                        break;
                }
            }

            return contents;
        }

        private static bool TryCreateDataContent(
            LanguageModelChatContentBlock block,
            out ChatMessageContentPart? dataContent)
        {
            dataContent = null;
            var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);
            if (mediaType.Length == 0)
            {
                return false;
            }

            if (block.Data is { Length: > 0 } bytes)
            {
                dataContent = ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mediaType);
                return true;
            }

            var path = block.ResourcePath ?? block.Content;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            if (!LanguageModelMediaResourcePolicy.CanReadLocalMediaFile(
                    path,
                    block.Kind,
                    block.MediaType,
                    out mediaType,
                    out _))
            {
                return false;
            }

            dataContent = ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(File.ReadAllBytes(path)), mediaType);
            return true;
        }

        private static bool TryCreateUriContent(
            LanguageModelChatContentBlock block,
            out ChatMessageContentPart? uriContent)
        {
            uriContent = null;
            var rawUri = block.ResourcePath ?? block.Content;
            if (string.IsNullOrWhiteSpace(rawUri) ||
                !Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var uri) ||
                uri.IsFile)
            {
                return false;
            }

            var mediaType = NormalizeMediaType(block.MediaType, rawUri, block.Kind);
            if (mediaType.Length == 0)
            {
                return false;
            }

            uriContent = ChatMessageContentPart.CreateImagePart(uri);
            return true;
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

        private static OpenAiLanguageModelSettings GetSettings(LanguageModelDefinition model)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (model.InterfaceSettings is not OpenAiLanguageModelSettings settings)
            {
                throw new InvalidOperationException($"Current interface type {model.InterfaceType} does not support OpenAI chat calls.");
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

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var normalized = baseUrl.Trim();
            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return normalized;
        }

        private static ChatReasoningEffortLevel? ParseReasoningEffort(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "LOW" => ChatReasoningEffortLevel.Low,
                "MEDIUM" => ChatReasoningEffortLevel.Medium,
                "HIGH" => ChatReasoningEffortLevel.High,
                "EXTRAHIGH" => ChatReasoningEffortLevel.High,
                "EXTRA_HIGH" => ChatReasoningEffortLevel.High,
                _ => null
            };
        }
    }
}
