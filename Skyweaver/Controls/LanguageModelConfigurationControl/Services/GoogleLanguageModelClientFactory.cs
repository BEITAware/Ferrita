using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skyweaver.Controls.LanguageModelConfigurationControl.Models;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
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

        public string InterfaceType => "GOOGLE";

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
            CancellationToken cancellationToken = default)
        {
            var settings = GetSettings(model);
            using var request = await CreateGenerateContentRequestAsync(
                    settings,
                    messages,
                    useStreamingEndpoint: false,
                    cancellationToken).ConfigureAwait(false);
            using var response = await s_httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);

            using var document = await ReadJsonDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            var parsed = ParseResponsePayload(document.RootElement, settings.ModelId);
            return new LanguageModelChatResponse
            {
                Text = parsed.Text,
                ReasoningText = parsed.ReasoningText,
                ModelId = parsed.ModelId
            };
        }

        public async IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var settings = GetSettings(model);
            using var request = await CreateGenerateContentRequestAsync(
                    settings,
                    messages,
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
            bool useStreamingEndpoint,
            CancellationToken cancellationToken)
        {
            var payload = await BuildRequestPayloadAsync(settings, messages, cancellationToken).ConfigureAwait(false);
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

        private static async Task<GoogleGenerateContentRequest> BuildRequestPayloadAsync(
            GoogleLanguageModelSettings settings,
            IReadOnlyList<LanguageModelChatMessage> messages,
            CancellationToken cancellationToken)
        {
            var inlineBudget = new InlinePayloadBudget(InlinePayloadBudgetBytes);
            var systemInstruction = await BuildSystemInstructionAsync(settings, messages, inlineBudget, cancellationToken).ConfigureAwait(false);
            var contents = await BuildContentsAsync(settings, messages, inlineBudget, cancellationToken).ConfigureAwait(false);

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
                GenerationConfig = CreateGenerationConfig(settings)
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
            var localPath = ResolveLocalPath(block);

            if (TryGetInlineBytes(block, localPath, out var bytes) && bytes.Length > 0)
            {
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

        private static bool TryGetInlineBytes(
            LanguageModelChatContentBlock block,
            string? localPath,
            out byte[] bytes)
        {
            if (block.Data is { Length: > 0 } providedBytes)
            {
                bytes = providedBytes.ToArray();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
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
            var debugItems = new List<LanguageModelStreamingContentDebugItem>();

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
                        AppendPartText(partElement, textBuilder, reasoningBuilder, debugItems);
                    }
                }
            }

            return new ParsedGoogleResponse(
                SanitizeModelText(textBuilder.ToString()),
                SanitizeModelText(reasoningBuilder.ToString()),
                GetOptionalString(rootElement, "modelVersion") ?? fallbackModelId,
                debugItems);
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
                RawText = rawText,
                WasTextSanitized = !string.Equals(rawText, parsed.Text, StringComparison.Ordinal),
                Role = "assistant",
                FinishReason = GetFirstCandidateProperty(document.RootElement, "finishReason"),
                ResponseId = responseId,
                RawRepresentationType = typeof(JsonDocument).FullName,
                RawRepresentationSummary = TruncateText(payload, 512),
                AdditionalProperties = BuildStreamingAdditionalProperties(document.RootElement),
                Contents = parsed.DebugItems
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
            ICollection<LanguageModelStreamingContentDebugItem> debugItems)
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

            if (partElement.ValueKind == JsonValueKind.Object)
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
                $"Google API request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). {errorMessage}");
        }

        private static string TryExtractErrorMessage(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "No error payload was returned.";
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

            return TruncateText(payload.Trim(), 512) ?? "Unknown Google API error.";
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

            return block.Kind == LanguageModelChatContentBlockKind.Audio
                ? "audio"
                : "image";
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
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                return mediaType.Trim();
            }

            var extension = Path.GetExtension(pathOrUri ?? string.Empty).ToLowerInvariant();
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return "image/jpeg";
            }

            if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                return "image/gif";
            }

            if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                return "image/webp";
            }

            if (string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return "image/bmp";
            }

            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return "audio/wav";
            }

            if (string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return "audio/mpeg";
            }

            if (string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase))
            {
                return "audio/mp4";
            }

            if (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase))
            {
                return "audio/ogg";
            }

            if (string.Equals(extension, ".flac", StringComparison.OrdinalIgnoreCase))
            {
                return "audio/flac";
            }

            return kind == LanguageModelChatContentBlockKind.Audio ? "audio/wav" : "image/png";
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

        private static int EstimateBase64Length(int byteCount)
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
            IReadOnlyList<LanguageModelStreamingContentDebugItem> DebugItems);

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
        }

        private sealed class GoogleContent
        {
            [JsonPropertyName("role")]
            public string? Role { get; init; }

            [JsonPropertyName("parts")]
            public IReadOnlyList<GooglePart> Parts { get; init; } = Array.Empty<GooglePart>();
        }

        private sealed class GooglePart
        {
            [JsonPropertyName("text")]
            public string? Text { get; init; }

            [JsonPropertyName("inlineData")]
            public GoogleInlineData? InlineData { get; init; }

            [JsonPropertyName("fileData")]
            public GoogleFileData? FileData { get; init; }
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
    }
}
