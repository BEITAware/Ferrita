using System.ClientModel;
using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;
using Skyweaver.Controls.LanguageModelConfigurationControl.Models;

namespace Skyweaver.Controls.LanguageModelConfigurationControl.Services
{
    internal sealed class MeaiLanguageModelInterfaceAdapter : ILanguageModelInterfaceAdapter
    {
        public string InterfaceType => "MEAI";

        public LanguageModelInterfaceSettings CreateInterfaceSettings()
        {
            return new MeaiLanguageModelSettings();
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
            var response = await CreateChatClient(settings).GetResponseAsync(
                messages.Select(ToSdkMessage).ToArray(),
                CreateChatOptions(settings),
                cancellationToken).ConfigureAwait(false);

            return new LanguageModelChatResponse
            {
                Text = SanitizeModelText(response.Text),
                ReasoningText = ExtractReasoningText(response.Messages.SelectMany(message => message.Contents)),
                ModelId = response.ModelId
            };
        }

        public async IAsyncEnumerable<LanguageModelStreamingChatUpdate> GetStreamingResponseAsync(
            LanguageModelDefinition model,
            IReadOnlyList<LanguageModelChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var settings = GetSettings(model);

            await foreach (var update in CreateChatClient(settings).GetStreamingResponseAsync(
                               messages.Select(ToSdkMessage).ToArray(),
                               CreateChatOptions(settings),
                               cancellationToken).ConfigureAwait(false))
            {
                var streamingUpdate = BuildStreamingUpdate(update);
                if (!ShouldEmitStreamingUpdate(streamingUpdate))
                {
                    continue;
                }

                yield return streamingUpdate;
            }
        }

        private static string SanitizeModelText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Some OpenAI-compatible endpoints leak non-printing control characters
            // into text deltas. They break the XML tool protocol if forwarded verbatim.
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

        private static LanguageModelStreamingChatUpdate BuildStreamingUpdate(ChatResponseUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);

            var rawText = update.Text;
            var sanitizedText = SanitizeModelText(rawText);
            var rawReasoningText = ExtractReasoningText(update.Contents);
            var sanitizedReasoningText = SanitizeModelText(rawReasoningText);
            var rawRepresentation = update.RawRepresentation;
            var continuationToken = GetContinuationToken(update);

            return new LanguageModelStreamingChatUpdate
            {
                TextDelta = sanitizedText,
                ReasoningTextDelta = sanitizedReasoningText,
                ModelId = NormalizeMetadataText(update.ModelId),
                RawText = rawText,
                WasTextSanitized = !string.Equals(rawText ?? string.Empty, sanitizedText, StringComparison.Ordinal),
                Role = NormalizeMetadataText(update.Role.ToString()),
                AuthorName = NormalizeMetadataText(update.AuthorName),
                FinishReason = NormalizeMetadataText(update.FinishReason?.ToString()),
                ResponseId = NormalizeMetadataText(update.ResponseId),
                MessageId = NormalizeMetadataText(update.MessageId),
                ConversationId = NormalizeMetadataText(update.ConversationId),
                CreatedAt = update.CreatedAt,
                ContinuationToken = DescribeContinuationToken(continuationToken),
                RawRepresentationType = rawRepresentation?.GetType().FullName,
                RawRepresentationSummary = DescribeDebugValue(rawRepresentation),
                AdditionalProperties = BuildAdditionalProperties(update.AdditionalProperties),
                Contents = BuildContentDebugItems(update.Contents)
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

        private static IReadOnlyList<LanguageModelStreamingContentDebugItem> BuildContentDebugItems(
            IEnumerable<AIContent>? contents)
        {
            if (contents == null)
            {
                return Array.Empty<LanguageModelStreamingContentDebugItem>();
            }

            return contents
                .Where(content => content != null)
                .Select(BuildContentDebugItem)
                .ToArray();
        }

        private static LanguageModelStreamingContentDebugItem BuildContentDebugItem(AIContent content)
        {
            ArgumentNullException.ThrowIfNull(content);

            var rawRepresentation = content.RawRepresentation;
            return new LanguageModelStreamingContentDebugItem
            {
                ContentType = content.GetType().FullName ?? content.GetType().Name,
                Text = content switch
                {
                    TextContent textContent => textContent.Text,
                    TextReasoningContent reasoningContent => reasoningContent.Text,
                    _ => null
                },
                Summary = DescribeContent(content),
                RawRepresentationType = rawRepresentation?.GetType().FullName,
                RawRepresentationSummary = DescribeDebugValue(rawRepresentation),
                AdditionalProperties = BuildAdditionalProperties(content.AdditionalProperties)
            };
        }

        private static IReadOnlyDictionary<string, object?> BuildAdditionalProperties(
            IEnumerable<KeyValuePair<string, object?>>? properties)
        {
            if (properties == null)
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }

            var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
            {
                if (string.IsNullOrWhiteSpace(property.Key))
                {
                    continue;
                }

                normalized[property.Key] = NormalizeDebugValue(property.Value);
            }

            return normalized;
        }

        private static object? NormalizeDebugValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or char or DateTime or DateTimeOffset or TimeSpan or Guid)
            {
                return value;
            }

            if (value is Enum or Uri)
            {
                return value.ToString();
            }

            if (value is byte[] bytes)
            {
                return $"byte[{bytes.Length}]";
            }

            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return readOnlyDictionary.ToDictionary(
                    item => item.Key,
                    item => NormalizeDebugValue(item.Value),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (value is IDictionary dictionary)
            {
                var normalizedDictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key?.ToString();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    normalizedDictionary[key] = NormalizeDebugValue(entry.Value);
                }

                return normalizedDictionary;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                var normalizedItems = new List<object?>();
                foreach (var item in enumerable)
                {
                    normalizedItems.Add(NormalizeDebugValue(item));
                }

                return normalizedItems;
            }

            return DescribeDebugValue(value);
        }

        private static string? DescribeContent(AIContent content)
        {
            ArgumentNullException.ThrowIfNull(content);

            if (content is TextContent textContent)
            {
                return TruncateText(textContent.Text, 512);
            }

            if (content is TextReasoningContent reasoningContent)
            {
                return TruncateText(reasoningContent.Text, 512);
            }

            var rawRepresentationSummary = DescribeDebugValue(content.RawRepresentation);
            if (!string.IsNullOrWhiteSpace(rawRepresentationSummary))
            {
                return rawRepresentationSummary;
            }

            return content.GetType().FullName ?? content.GetType().Name;
        }

        private static string? DescribeDebugValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string text)
            {
                return TruncateText(text, 512);
            }

            if (value is byte[] bytes)
            {
                return $"byte[{bytes.Length}]";
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                return DescribeEnumerable(value.GetType(), enumerable);
            }

            return TruncateText(value.ToString(), 512);
        }

        private static string DescribeEnumerable(Type type, IEnumerable enumerable)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(enumerable);

            var countText = enumerable is ICollection collection
                ? collection.Count.ToString()
                : "unknown-count";
            var typeName = type.FullName ?? type.Name;
            return $"{typeName} ({countText})";
        }

#pragma warning disable MEAI001
        private static object? GetContinuationToken(ChatResponseUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);
            return update.ContinuationToken;
        }
#pragma warning restore MEAI001

        private static string? DescribeContinuationToken(object? continuationToken)
        {
            if (continuationToken == null)
            {
                return null;
            }

            try
            {
                var toBytesMethod = continuationToken.GetType().GetMethod("ToBytes", Type.EmptyTypes);
                if (toBytesMethod?.Invoke(continuationToken, null) is ReadOnlyMemory<byte> bytes)
                {
                    return bytes.IsEmpty
                        ? "empty"
                        : Convert.ToBase64String(bytes.ToArray());
                }
            }
            catch
            {
            }

            return TruncateText(continuationToken.ToString(), 512);
        }

        private static string? NormalizeMetadataText(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
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

        private static IChatClient CreateChatClient(MeaiLanguageModelSettings settings)
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(NormalizeBaseUrl(settings.BaseUrl))
            };

            var credential = new ApiKeyCredential(settings.ApiKey);
            return new OpenAIClient(credential, clientOptions)
                .GetChatClient(settings.ModelId)
                .AsIChatClient();
        }

        private static ChatOptions CreateChatOptions(MeaiLanguageModelSettings settings)
        {
            var options = new ChatOptions();

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
                options.MaxOutputTokens = settings.MaxOutputTokens;
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

            if (settings.UseReasoningEffort || settings.UseReasoningOutput)
            {
                var reasoningOptions = CreateReasoningOptions(settings);
                if (reasoningOptions != null)
                {
                    options.Reasoning = reasoningOptions;
                }
            }

            return options;
        }

        private static ChatMessage ToSdkMessage(LanguageModelChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var sdkContents = BuildSdkContents(message);
            var sdkRole = message.IsHostInjectedTail
                ? ChatRole.System
                : ToSdkRole(message.Role);
            var sdkMessage = sdkContents.Count == 0 || sdkContents.All(content => content is TextContent)
                ? new ChatMessage(sdkRole, message.Content)
                : new ChatMessage(sdkRole, sdkContents)
            {
                AuthorName = message.AuthorName
            };

            return sdkMessage;
        }

        private static IList<AIContent> BuildSdkContents(LanguageModelChatMessage message)
        {
            if (message.ContentBlocks.Count == 0)
            {
                return Array.Empty<AIContent>();
            }

            var contents = new List<AIContent>();
            foreach (var block in message.ContentBlocks)
            {
                switch (block.Kind)
                {
                    case LanguageModelChatContentBlockKind.Text:
                    case LanguageModelChatContentBlockKind.HostPreservedContent:
                        if (!string.IsNullOrWhiteSpace(block.Content))
                        {
                            contents.Add(new TextContent(block.Content));
                        }

                        break;

                    case LanguageModelChatContentBlockKind.Image:
                    case LanguageModelChatContentBlockKind.Audio:
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
                            contents.Add(new TextContent(block.Content));
                        }

                        break;
                }
            }

            return contents;
        }

        private static bool TryCreateDataContent(
            LanguageModelChatContentBlock block,
            out DataContent? dataContent)
        {
            dataContent = null;
            var mediaType = NormalizeMediaType(block.MediaType, block.ResourcePath ?? block.Content, block.Kind);

            if (block.Data is { Length: > 0 } bytes)
            {
                dataContent = new DataContent(bytes, mediaType);
                return true;
            }

            var path = block.ResourcePath ?? block.Content;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            dataContent = new DataContent(File.ReadAllBytes(path), mediaType);
            return true;
        }

        private static bool TryCreateUriContent(
            LanguageModelChatContentBlock block,
            out UriContent? uriContent)
        {
            uriContent = null;
            var rawUri = block.ResourcePath ?? block.Content;
            if (string.IsNullOrWhiteSpace(rawUri) ||
                !Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var uri) ||
                uri.IsFile)
            {
                return false;
            }

            uriContent = new UriContent(
                uri,
                NormalizeMediaType(block.MediaType, rawUri, block.Kind));
            return true;
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
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".ogg" => "audio/ogg",
                _ => kind == LanguageModelChatContentBlockKind.Audio ? "audio/wav" : "image/png"
            };
        }

        private static string ExtractReasoningText(IEnumerable<AIContent>? contents)
        {
            if (contents == null)
            {
                return string.Empty;
            }

            return string.Concat(
                contents
                    .OfType<TextReasoningContent>()
                    .Select(content => content.Text ?? string.Empty));
        }

        private static ChatRole ToSdkRole(LanguageModelChatRole role)
        {
            return role switch
            {
                LanguageModelChatRole.System => ChatRole.System,
                LanguageModelChatRole.Assistant => ChatRole.Assistant,
                // Skyweaver replays XML tool transcripts as plain text, not MEAI FunctionResultContent.
                // Preserve those transcripts for OpenAI-compatible backends by downgrading any
                // remaining legacy Tool messages to ordinary User messages.
                LanguageModelChatRole.Tool => ChatRole.User,
                _ => ChatRole.User
            };
        }

        private static MeaiLanguageModelSettings GetSettings(LanguageModelDefinition model)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (model.InterfaceSettings is not MeaiLanguageModelSettings settings)
            {
                throw new InvalidOperationException($"Current interface type {model.InterfaceType} does not support MEAI chat calls.");
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

        private static ReasoningOptions? CreateReasoningOptions(MeaiLanguageModelSettings settings)
        {
            var effort = ParseReasoningEffort(settings.ReasoningEffort);
            var output = settings.UseReasoningOutput
                ? ParseReasoningOutput(settings.ReasoningOutput)
                : null;

            if (effort == null && output == null)
            {
                return null;
            }

            var options = new ReasoningOptions();
            if (effort != null)
            {
                options.Effort = effort;
            }

            if (output != null)
            {
                options.Output = output;
            }

            return options;
        }

        private static ReasoningEffort? ParseReasoningEffort(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "LOW" => ReasoningEffort.Low,
                "MEDIUM" => ReasoningEffort.Medium,
                "HIGH" => ReasoningEffort.High,
                "EXTRAHIGH" => ReasoningEffort.High,
                "EXTRA_HIGH" => ReasoningEffort.High,
                _ => null
            };
        }

        private static ReasoningOutput? ParseReasoningOutput(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "SUMMARY" => ReasoningOutput.Summary,
                "FULL" => ReasoningOutput.Full,
                "NONE" => ReasoningOutput.None,
                _ => null
            };
        }
    }
}
