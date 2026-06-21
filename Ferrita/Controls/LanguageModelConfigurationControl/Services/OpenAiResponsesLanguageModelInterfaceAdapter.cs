using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ferrita.Controls.LanguageModelConfigurationControl.Models;
using Ferrita.Services.FerritaTools;

namespace Ferrita.Controls.LanguageModelConfigurationControl.Services
{
    internal class OpenAiResponsesLanguageModelInterfaceAdapter : ILanguageModelInterfaceAdapter
    {
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

        public string InterfaceType => "OpenAI Responses API";

        public LanguageModelInterfaceSettings CreateInterfaceSettings()
        {
            return new OpenAiResponsesLanguageModelSettings();
        }

        public void Validate(LanguageModelDefinition model)
        {
            var settings = GetSettings(model);
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
        }

        public async Task<LanguageModelChatResponse> GetResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<FerritaPromptToolDefinition>? tools = null)
        {
            var settings = GetSettings(model);
            bool useNativeTools = tools != null && tools.Count > 0;

            var inputItems = ProjectInputItemsForResponses(messages, useNativeTools);

            var requestBody = new Dictionary<string, object>
            {
                { "model", settings.ModelId },
                { "input", inputItems }
            };

            if (settings.UseTemperature)
            {
                requestBody["temperature"] = (double)settings.Temperature;
            }

            if (settings.UseTopP)
            {
                requestBody["top_p"] = (double)settings.TopP;
            }

            if (settings.UseMaxOutputTokens)
            {
                requestBody["max_output_tokens"] = settings.MaxOutputTokens;
            }

            if (settings.UseReasoningEffort)
            {
                requestBody["reasoning"] = new Dictionary<string, string>
                {
                    { "effort", settings.ReasoningEffort.ToLowerInvariant() }
                };
            }

            if (useNativeTools && tools != null)
            {
                var convertedTools = ConvertToolsForResponses(tools);
                requestBody["tools"] = convertedTools;
            }

            var url = new Uri(new Uri(NormalizeBaseUrl(settings.BaseUrl)), "responses");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            var json = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"OpenAI Responses API failed with status {response.StatusCode}. Details: {errContent}");
            }

            var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responsePayload);
            var root = doc.RootElement;

            string finalResponseText = string.Empty;
            string finalReasoningText = string.Empty;
            var toolCalls = new List<LanguageModelToolCall>();

            if (root.TryGetProperty("output", out var outputProp) && outputProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputProp.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeProp))
                    {
                        var typeStr = typeProp.GetString();
                        if (string.Equals(typeStr, "message", StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
                            {
                                var textParts = new List<string>();
                                foreach (var part in contentProp.EnumerateArray())
                                {
                                    if (part.TryGetProperty("type", out var pTypeProp) &&
                                        string.Equals(pTypeProp.GetString(), "output_text", StringComparison.OrdinalIgnoreCase) &&
                                        part.TryGetProperty("text", out var textProp))
                                    {
                                        textParts.Add(textProp.GetString() ?? string.Empty);
                                    }
                                }
                                finalResponseText = string.Concat(textParts);
                            }
                        }
                        else if (string.Equals(typeStr, "reasoning", StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
                            {
                                var reasoningParts = new List<string>();
                                foreach (var part in contentProp.EnumerateArray())
                                {
                                    if (part.TryGetProperty("type", out var pTypeProp) &&
                                        string.Equals(pTypeProp.GetString(), "reasoning_text", StringComparison.OrdinalIgnoreCase) &&
                                        part.TryGetProperty("text", out var textProp))
                                    {
                                        reasoningParts.Add(textProp.GetString() ?? string.Empty);
                                    }
                                }
                                finalReasoningText = string.Concat(reasoningParts);
                            }
                        }
                        else if (string.Equals(typeStr, "function_call", StringComparison.OrdinalIgnoreCase))
                        {
                            var tcId = item.TryGetProperty("call_id", out var callIdProp) ? callIdProp.GetString() : null;
                            if (string.IsNullOrEmpty(tcId))
                            {
                                tcId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            }
                            if (string.IsNullOrEmpty(tcId))
                            {
                                tcId = "tc_" + Guid.NewGuid().ToString("N")[..8];
                            }

                            var tcName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                            var tcArgs = item.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? string.Empty : string.Empty;

                            toolCalls.Add(new LanguageModelToolCall
                            {
                                Id = tcId,
                                Name = tcName,
                                ArgumentsJson = tcArgs
                            });
                        }
                    }
                }
            }

            int? inputTokens = null;
            int? totalTokens = null;
            if (root.TryGetProperty("usage", out var usageProp))
            {
                if (usageProp.TryGetProperty("input_tokens", out var inTokensVal))
                {
                    inputTokens = inTokensVal.GetInt32();
                }
                if (usageProp.TryGetProperty("total_tokens", out var totTokensVal))
                {
                    totalTokens = totTokensVal.GetInt32();
                }
            }

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
                            using var argDoc = JsonDocument.Parse(tc.ArgumentsJson);
                            foreach (var prop in argDoc.RootElement.EnumerateObject())
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
                        catch
                        {
                            xmlBuilder.Append(tc.ArgumentsJson);
                        }
                    }
                    xmlBuilder.Append("</Tool>");
                }

                finalResponseText = xmlBuilder.ToString();
            }

            var summaryModelId = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : settings.ModelId;

            return new LanguageModelChatResponse
            {
                Text = SanitizeModelText(finalResponseText),
                ReasoningText = SanitizeModelText(finalReasoningText),
                ModelId = summaryModelId ?? settings.ModelId,
                InputTokenCount = inputTokens,
                TotalTokenCount = totalTokens
            };
        }

        public async Task<int> CountTokensAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var settings = GetSettings(model);

            try
            {
                var inputItems = ProjectInputItemsForResponses(messages, useNativeTools: false);

                var payload = new
                {
                    model = settings.ModelId,
                    input = inputItems
                };

                var url = new Uri(new Uri(NormalizeBaseUrl(settings.BaseUrl)), "responses/input_tokens");
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"OpenAI Responses token counting endpoint failed with status {response.StatusCode}.");
                }

                var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responsePayload);
                if (doc.RootElement.TryGetProperty("input_tokens", out var inputTokensProp) &&
                    inputTokensProp.TryGetInt32(out var count))
                {
                    return count;
                }

                throw new InvalidOperationException("OpenAI Responses token counting endpoint did not return input token usage.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("OpenAI Responses token counting endpoint failed.", ex);
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

            var inputItems = ProjectInputItemsForResponses(messages, useNativeTools);

            var requestBody = new Dictionary<string, object>
            {
                { "model", settings.ModelId },
                { "input", inputItems },
                { "stream", true }
            };

            if (settings.UseTemperature)
            {
                requestBody["temperature"] = (double)settings.Temperature;
            }

            if (settings.UseTopP)
            {
                requestBody["top_p"] = (double)settings.TopP;
            }

            if (settings.UseMaxOutputTokens)
            {
                requestBody["max_output_tokens"] = settings.MaxOutputTokens;
            }

            if (settings.UseReasoningEffort)
            {
                requestBody["reasoning"] = new Dictionary<string, string>
                {
                    { "effort", settings.ReasoningEffort.ToLowerInvariant() }
                };
            }

            if (useNativeTools && tools != null)
            {
                var convertedTools = ConvertToolsForResponses(tools);
                requestBody["tools"] = convertedTools;
            }

            var url = new Uri(new Uri(NormalizeBaseUrl(settings.BaseUrl)), "responses");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            var json = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // 使用 ResponseHeadersRead 避免把整个 stream 一次性读入内存
            using var response = await s_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"OpenAI Responses stream request failed: {response.StatusCode}. Details: {errContent}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var toolCalls = new List<LanguageModelToolCall>();

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("data:"))
                {
                    var dataStr = trimmedLine.Substring(5).Trim();
                    if (string.Equals(dataStr, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (dataStr.StartsWith("{"))
                    {
                        string? textDelta = null;
                        string? reasoningDelta = null;
                        JsonDocument? chunkDoc = null;
                        try
                        {
                            chunkDoc = JsonDocument.Parse(dataStr);
                            var root = chunkDoc.RootElement;
                            if (root.TryGetProperty("type", out var typeProp))
                            {
                                var typeStr = typeProp.GetString();
                                if (string.Equals(typeStr, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (root.TryGetProperty("delta", out var deltaProp))
                                    {
                                        textDelta = deltaProp.GetString();
                                    }
                                }
                                else if (string.Equals(typeStr, "response.reasoning_text.delta", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(typeStr, "response.reasoning.delta", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (root.TryGetProperty("delta", out var deltaProp))
                                    {
                                        reasoningDelta = deltaProp.GetString();
                                    }
                                }
                                else if (string.Equals(typeStr, "response.output_item.done", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (root.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.Object)
                                    {
                                        if (itemProp.TryGetProperty("type", out var itemTypeProp) &&
                                            string.Equals(itemTypeProp.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var tcId = itemProp.TryGetProperty("call_id", out var callIdProp) ? callIdProp.GetString() : null;
                                            if (string.IsNullOrEmpty(tcId))
                                            {
                                                tcId = itemProp.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                            }
                                            if (string.IsNullOrEmpty(tcId))
                                            {
                                                tcId = "tc_" + Guid.NewGuid().ToString("N")[..8];
                                            }

                                            var tcName = itemProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                                            var tcArgs = itemProp.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? string.Empty : string.Empty;

                                            toolCalls.Add(new LanguageModelToolCall
                                            {
                                                Id = tcId,
                                                Name = tcName,
                                                ArgumentsJson = tcArgs
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 忽略单个 chunk 的解析错误
                        }
                        finally
                        {
                            chunkDoc?.Dispose();
                        }

                        if (!string.IsNullOrEmpty(textDelta))
                        {
                            yield return new LanguageModelStreamingChatUpdate
                            {
                                TextDelta = textDelta,
                                Role = "assistant"
                            };
                        }

                        if (!string.IsNullOrEmpty(reasoningDelta))
                        {
                            yield return new LanguageModelStreamingChatUpdate
                            {
                                ReasoningTextDelta = reasoningDelta,
                                Role = "assistant"
                            };
                        }
                    }
                }
            }

            if (useNativeTools && toolCalls.Count > 0)
            {
                var xmlBuilder = new StringBuilder();
                foreach (var tc in toolCalls)
                {
                    xmlBuilder.Append($"<Tool ToolName=\"{tc.Name}\" ToolCallID=\"{tc.Id}\">");
                    if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                    {
                        try
                        {
                            using var argDoc = JsonDocument.Parse(tc.ArgumentsJson);
                            foreach (var prop in argDoc.RootElement.EnumerateObject())
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
                            xmlBuilder.Append(tc.ArgumentsJson);
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

        #region Helper Methods

        private static OpenAiLanguageModelSettings GetSettings(LanguageModelDefinition model)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (model.InterfaceSettings is not OpenAiLanguageModelSettings settings)
            {
                throw new InvalidOperationException($"Current interface type {model.InterfaceType} does not support OpenAI Responses API calls.");
            }

            return settings;
        }

        private static string NormalizeBaseUrl(string url)
        {
            var trimmed = url.Trim();
            if (!trimmed.EndsWith("/"))
            {
                trimmed += "/";
            }
            return trimmed;
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

        private List<object> ProjectInputItemsForResponses(
            IReadOnlyList<LanguageModelChatMessage> sourceMessages,
            bool useNativeTools)
        {
            var inputItems = new List<object>();

            foreach (var msg in sourceMessages)
            {
                var role = msg.IsHostInjectedTail
                    ? "system"
                    : msg.Role.ToString().ToLowerInvariant();

                if (role != "system" && role != "user" && role != "assistant" && role != "developer")
                {
                    role = "user";
                }

                if (!useNativeTools || msg.Role != LanguageModelChatRole.Assistant)
                {
                    if (useNativeTools && msg.Role == LanguageModelChatRole.User)
                    {
                        var xmlToolReturns = ParseXmlToolReturns(msg.Content);
                        if (xmlToolReturns.Count > 0)
                        {
                            foreach (var tr in xmlToolReturns)
                            {
                                inputItems.Add(new
                                {
                                    type = "message",
                                    role = "user",
                                    content = new[]
                                    {
                                        new
                                        {
                                            type = "function_call_output",
                                            call_id = tr.ToolCallId,
                                            output = tr.Content
                                        }
                                    }
                                });
                            }
                            continue;
                        }
                    }

                    inputItems.Add(new
                    {
                        type = "message",
                        role = role,
                        content = ConvertToResponsesContentBlocks(msg)
                    });
                    continue;
                }

                var xmlToolCalls = ParseXmlToolCalls(msg.Content);
                if (xmlToolCalls.Count > 0)
                {
                    var cleanText = StripXmlToolCalls(msg.Content);
                    var contentList = new List<object>();
                    if (!string.IsNullOrWhiteSpace(cleanText))
                    {
                        contentList.Add(new
                        {
                            type = "input_text",
                            text = cleanText
                        });
                    }

                    // 助理消息也可以直接传纯文本，模型可以通过对话本身学习。
                    inputItems.Add(new
                    {
                        type = "message",
                        role = "assistant",
                        content = contentList
                    });
                }
                else
                {
                    inputItems.Add(new
                    {
                        type = "message",
                        role = "assistant",
                        content = ConvertToResponsesContentBlocks(msg)
                    });
                }
            }

            return inputItems;
        }

        private static List<object> ConvertToResponsesContentBlocks(LanguageModelChatMessage message)
        {
            var blocks = new List<object>();

            if (message.ContentBlocks == null || message.ContentBlocks.Count == 0)
            {
                if (!string.IsNullOrEmpty(message.Content))
                {
                    blocks.Add(new { type = "input_text", text = message.Content });
                }
            }
            else
            {
                foreach (var block in message.ContentBlocks)
                {
                    switch (block.Kind)
                    {
                        case LanguageModelChatContentBlockKind.Text:
                        case LanguageModelChatContentBlockKind.HostPreservedContent:
                            if (!string.IsNullOrWhiteSpace(block.Content))
                            {
                                blocks.Add(new { type = "input_text", text = block.Content });
                            }
                            break;
                        case LanguageModelChatContentBlockKind.Image:
                            if (block.Data is { Length: > 0 } bytes)
                            {
                                var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);
                                var base64 = Convert.ToBase64String(bytes);
                                blocks.Add(new
                                {
                                    type = "input_image",
                                    image_url = $"data:{mediaType};base64,{base64}"
                                });
                            }
                            else
                            {
                                var rawUri = block.ResourcePath ?? block.Content;
                                if (!string.IsNullOrWhiteSpace(rawUri))
                                {
                                    blocks.Add(new
                                    {
                                        type = "input_image",
                                        image_url = rawUri
                                    });
                                }
                            }
                            break;
                    }
                }
            }

            return blocks;
        }

        private static List<object> ConvertToolsForResponses(IReadOnlyList<FerritaPromptToolDefinition> tools)
        {
            var list = new List<object>();
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

                list.Add(new
                {
                    type = "function",
                    name = tool.Name,
                    description = tool.Description ?? string.Empty,
                    parameters = schemaObj
                });
            }
            return list;
        }

        #endregion

        #region XML Parsing Helpers

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
                var pattern = @"<(" + string.Join("|", escapedNames) + @")(?:\s+[^>]?>)?.*?</\1>";
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

        private static string SanitizeModelText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var clean = text.Trim();
            if (clean.StartsWith("```xml", StringComparison.OrdinalIgnoreCase) && clean.EndsWith("```"))
            {
                clean = clean.Substring(6, clean.Length - 9).Trim();
            }

            return clean;
        }

        #endregion
    }
}
