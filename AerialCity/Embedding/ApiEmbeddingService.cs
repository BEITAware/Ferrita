using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using AerialCity.Delegates;
using AerialCity.Segmentation;

namespace AerialCity.Embedding;

/// <summary>
/// Creates API-backed embedding delegates for OpenAI-compatible and Google embedding APIs.
/// </summary>
public sealed class ApiEmbeddingService
{
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
    private const string DefaultGoogleBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private const string GoogleInlineDataParameter = "sendInlineData";
    private const double DefaultSegmentationBudgetRatio = 0.75d;
    private const int MaximumInputLengthFallbackDepth = 16;
    private const int MaximumStoredFallbackReasonLength = 500;

    private static readonly HttpClient SharedHttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    /// <summary>Creates a service that uses the shared HTTP client.</summary>
    public ApiEmbeddingService()
        : this(SharedHttpClient)
    {
    }

    /// <summary>Creates a service with a caller-supplied HTTP client.</summary>
    public ApiEmbeddingService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>Returns the embedding delegate exposed by this service.</summary>
    public EmbedContentDelegate CreateEmbeddingDelegate() => EmbedAsync;

    /// <summary>Returns the code file embedding delegate exposed by this service.</summary>
    public EmbedCodeFileDelegate CreateCodeFileEmbeddingDelegate() => EmbedCodeFileAsync;

    /// <summary>Returns the text file embedding delegate exposed by this service.</summary>
    public EmbedTextFileDelegate CreateTextFileEmbeddingDelegate() => EmbedTextFileAsync;

    /// <summary>Creates an embedding delegate using the shared HTTP client.</summary>
    public static EmbedContentDelegate CreateDelegate() =>
        new ApiEmbeddingService().CreateEmbeddingDelegate();

    /// <summary>Creates a code file embedding delegate using the shared HTTP client.</summary>
    public static EmbedCodeFileDelegate CreateCodeFileDelegate() =>
        new ApiEmbeddingService().CreateCodeFileEmbeddingDelegate();

    /// <summary>Creates a text file embedding delegate using the shared HTTP client.</summary>
    public static EmbedTextFileDelegate CreateTextFileDelegate() =>
        new ApiEmbeddingService().CreateTextFileEmbeddingDelegate();

    /// <summary>
    /// Embeds the request content and assigns the vector to the request segment when present.
    /// </summary>
    public async Task<EmbeddingResult> EmbedAsync(
        ApiEmbeddingRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var input = ResolveInput(request);
        var vector = request.ApiType switch
        {
            EmbeddingApiType.OpenAI => await EmbedWithOpenAiAsync(request, input, ct),
            EmbeddingApiType.Google => await EmbedWithGoogleAsync(request, input, ct),
            _ => throw new EmbeddingException($"Unsupported embedding API type: {request.ApiType}.")
        };

        if (request.Normalize)
            vector = vector.Normalize();

        if (request.Segment is not null)
            request.Segment.Embedding = vector;

        return new EmbeddingResult
        {
            Vector = vector,
            Segment = request.Segment,
            Model = request.Model,
            ApiType = request.ApiType
        };
    }

    /// <summary>
    /// Splits a complete source code file with Tree-sitter, embeds each chunk, and returns all results.
    /// </summary>
    public async Task<IReadOnlyList<EmbeddingResult>> EmbedCodeFileAsync(
        ApiCodeFileEmbeddingRequest request,
        CancellationToken ct = default)
    {
        ValidateCodeFileRequest(request);

        var sourceCode = await ResolveCodeFileSourceAsync(request, ct);
        if (sourceCode.Length == 0)
            return [];

        var sourceUri = request.SourceUri ?? request.FilePath;
        var segmentationMaxInputTokens = GetSegmentationTokenBudget(request.MaxInputTokens);
        var segments = TreeSitterCodeSegmenter.SegmentCode(
            sourceCode,
            request.Language,
            sourceUri,
            segmentationMaxInputTokens,
            request.Metadata,
            request.TokenCounter);

        var results = new List<EmbeddingResult>(segments.Count);
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            var chunkRequest = CreateChunkEmbeddingRequest(request, segment);
            await EmbedChunkWithInputLengthFallbackAsync(
                chunkRequest,
                results,
                request.MaxInputTokens,
                request.TokenCounter ?? TreeSitterCodeSegmenter.EstimateTokens,
                ct);
        }

        return results;
    }

    /// <summary>
    /// Splits a complete text file by paragraphs, embeds each chunk, and returns all results.
    /// </summary>
    public async Task<IReadOnlyList<EmbeddingResult>> EmbedTextFileAsync(
        ApiTextFileEmbeddingRequest request,
        CancellationToken ct = default)
    {
        ValidateTextFileRequest(request);

        var text = await ResolveTextFileSourceAsync(request, ct);
        if (text.Length == 0)
            return [];

        var sourceUri = request.SourceUri ?? request.FilePath;
        var segmentationMaxInputTokens = GetSegmentationTokenBudget(request.MaxInputTokens);
        var segments = TextFileSegmenter.SegmentText(
            text,
            sourceUri,
            segmentationMaxInputTokens,
            request.OverlapRatio,
            request.Metadata,
            request.TokenCounter);

        var results = new List<EmbeddingResult>(segments.Count);
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            var chunkRequest = CreateChunkEmbeddingRequest(request, segment);
            await EmbedChunkWithInputLengthFallbackAsync(
                chunkRequest,
                results,
                request.MaxInputTokens,
                request.TokenCounter ?? TextFileSegmenter.EstimateTokens,
                ct);
        }

        return results;
    }

    private async Task EmbedChunkWithInputLengthFallbackAsync(
        ApiEmbeddingRequest request,
        List<EmbeddingResult> results,
        int maxInputTokens,
        Func<string, int> tokenCounter,
        CancellationToken ct,
        int depth = 0)
    {
        try
        {
            results.Add(await EmbedAsync(request, ct));
        }
        catch (EmbeddingException ex) when (CanSplitOversizedEmbeddingInput(request, ex, depth))
        {
            var splitSegments = SplitSegmentForEmbeddingInputLimit(
                request.Segment!,
                ex.Message,
                depth + 1,
                maxInputTokens,
                tokenCounter);

            if (splitSegments.Count == 0)
                throw;

            foreach (var splitSegment in splitSegments)
            {
                ct.ThrowIfCancellationRequested();
                await EmbedChunkWithInputLengthFallbackAsync(
                    CreateChunkEmbeddingRequest(request, splitSegment),
                    results,
                    maxInputTokens,
                    tokenCounter,
                    ct,
                    depth + 1);
            }
        }
    }

    private async Task<EmbeddingVector> EmbedWithOpenAiAsync(
        ApiEmbeddingRequest request,
        EmbeddingInput input,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyUserParameters(request.Parameters, payload);

        if (request.Dimensions.HasValue && !ContainsKey(payload, "dimensions"))
            payload["dimensions"] = request.Dimensions.Value;

        if (!ContainsKey(payload, "encoding_format"))
            payload["encoding_format"] = "float";

        payload["model"] = request.Model;
        payload["input"] = BuildTextProjection(input, request.IncludeBinaryDataInTextProjection);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildOpenAiEmbeddingUri(request.BaseUrl));

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        httpRequest.Content = CreateJsonContent(payload);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, EmbeddingApiType.OpenAI);

        return ParseOpenAiEmbedding(body, response.Content.Headers.ContentType?.ToString());
    }

    private async Task<EmbeddingVector> EmbedWithGoogleAsync(
        ApiEmbeddingRequest request,
        EmbeddingInput input,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyUserParameters(request.Parameters, payload, GoogleInlineDataParameter);

        if (request.Dimensions.HasValue && !ContainsKey(payload, "outputDimensionality"))
            payload["outputDimensionality"] = request.Dimensions.Value;

        var modelPath = NormalizeGoogleModelPath(request.Model);
        payload["model"] = modelPath;
        payload["content"] = BuildGoogleContent(
            input,
            request.IncludeBinaryDataInTextProjection,
            GetBooleanParameter(request.Parameters, GoogleInlineDataParameter));

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildGoogleEmbeddingUri(request.BaseUrl, modelPath));

        httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey);
        httpRequest.Content = CreateJsonContent(payload);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, EmbeddingApiType.Google);

        return ParseGoogleEmbedding(body, response.Content.Headers.ContentType?.ToString());
    }

    private static void ValidateRequest(ApiEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new EmbeddingException("Embedding API key is required.");

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new EmbeddingException("Embedding model is required.");

        if (request.Content is null && request.Segment is null)
            throw new EmbeddingException("Embedding content or segment is required.");
    }

    private static void ValidateCodeFileRequest(ApiCodeFileEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new EmbeddingException("Embedding API key is required.");

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new EmbeddingException("Embedding model is required.");

        if (request.SourceCode is null && string.IsNullOrWhiteSpace(request.FilePath))
            throw new EmbeddingException("Code file path or source code is required.");

        if (request.MaxInputTokens <= 0)
            throw new EmbeddingException("Max input tokens must be greater than zero.");
    }

    private static void ValidateTextFileRequest(ApiTextFileEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new EmbeddingException("Embedding API key is required.");

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new EmbeddingException("Embedding model is required.");

        if (request.Text is null && string.IsNullOrWhiteSpace(request.FilePath))
            throw new EmbeddingException("Text file path or text content is required.");

        if (request.MaxInputTokens <= 0)
            throw new EmbeddingException("Max input tokens must be greater than zero.");

        if (double.IsNaN(request.OverlapRatio) ||
            double.IsInfinity(request.OverlapRatio) ||
            request.OverlapRatio < 0d ||
            request.OverlapRatio >= 1d)
        {
            throw new EmbeddingException("Overlap ratio must be greater than or equal to 0 and less than 1.");
        }
    }

    private static EmbeddingInput ResolveInput(ApiEmbeddingRequest request) =>
        request.Content ?? EmbeddingInput.FromSegment(request.Segment!);

    private static async Task<string> ResolveCodeFileSourceAsync(
        ApiCodeFileEmbeddingRequest request,
        CancellationToken ct)
    {
        if (request.SourceCode is not null)
            return request.SourceCode;

        try
        {
            return request.FileEncoding is null
                ? await File.ReadAllTextAsync(request.FilePath!, ct)
                : await File.ReadAllTextAsync(request.FilePath!, request.FileEncoding, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new EmbeddingException($"Could not read code file '{request.FilePath}'.", ex);
        }
    }

    private static async Task<string> ResolveTextFileSourceAsync(
        ApiTextFileEmbeddingRequest request,
        CancellationToken ct)
    {
        if (request.Text is not null)
            return request.Text;

        try
        {
            return request.FileEncoding is null
                ? await File.ReadAllTextAsync(request.FilePath!, ct)
                : await File.ReadAllTextAsync(request.FilePath!, request.FileEncoding, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new EmbeddingException($"Could not read text file '{request.FilePath}'.", ex);
        }
    }

    private static ApiEmbeddingRequest CreateChunkEmbeddingRequest(
        ApiCodeFileEmbeddingRequest request,
        Segment segment) =>
        new()
        {
            ApiKey = request.ApiKey,
            BaseUrl = request.BaseUrl,
            ApiType = request.ApiType,
            Model = request.Model,
            Segment = segment,
            Parameters = new Dictionary<string, object?>(request.Parameters, StringComparer.Ordinal),
            Dimensions = request.Dimensions,
            Normalize = request.Normalize,
            IncludeBinaryDataInTextProjection = request.IncludeBinaryDataInTextProjection
        };

    private static ApiEmbeddingRequest CreateChunkEmbeddingRequest(
        ApiTextFileEmbeddingRequest request,
        Segment segment) =>
        new()
        {
            ApiKey = request.ApiKey,
            BaseUrl = request.BaseUrl,
            ApiType = request.ApiType,
            Model = request.Model,
            Segment = segment,
            Parameters = new Dictionary<string, object?>(request.Parameters, StringComparer.Ordinal),
            Dimensions = request.Dimensions,
            Normalize = request.Normalize,
            IncludeBinaryDataInTextProjection = request.IncludeBinaryDataInTextProjection
        };

    private static ApiEmbeddingRequest CreateChunkEmbeddingRequest(
        ApiEmbeddingRequest request,
        Segment segment) =>
        new()
        {
            ApiKey = request.ApiKey,
            BaseUrl = request.BaseUrl,
            ApiType = request.ApiType,
            Model = request.Model,
            Segment = segment,
            Parameters = new Dictionary<string, object?>(request.Parameters, StringComparer.Ordinal),
            Dimensions = request.Dimensions,
            Normalize = request.Normalize,
            IncludeBinaryDataInTextProjection = request.IncludeBinaryDataInTextProjection
        };

    private static int GetSegmentationTokenBudget(int maxInputTokens)
    {
        if (maxInputTokens <= 1)
            return maxInputTokens;

        return Math.Max(1, (int)Math.Floor(maxInputTokens * DefaultSegmentationBudgetRatio));
    }

    private static bool CanSplitOversizedEmbeddingInput(
        ApiEmbeddingRequest request,
        EmbeddingException exception,
        int depth) =>
        depth < MaximumInputLengthFallbackDepth &&
        request.Segment is { Content.Length: > 1 } &&
        IsEmbeddingInputTooLargeFailure(exception);

    private static bool IsEmbeddingInputTooLargeFailure(EmbeddingException exception)
    {
        var message = exception.ToString();
        return message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("input is too long", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("context length", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("token", StringComparison.OrdinalIgnoreCase)) ||
            (message.Contains("input", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("maximum", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<Segment> SplitSegmentForEmbeddingInputLimit(
        Segment segment,
        string reason,
        int depth,
        int maxInputTokens,
        Func<string, int> tokenCounter)
    {
        var text = segment.Content;
        var splitIndex = FindNaturalSplitIndex(text);
        if (splitIndex <= 0 || splitIndex >= text.Length)
            return [];

        var splitSegments = new List<Segment>(2);
        AddSplitSegment(segment, 0, splitIndex, partIndex: 0, partCount: 2);
        AddSplitSegment(segment, splitIndex, text.Length, partIndex: 1, partCount: 2);
        return splitSegments;

        void AddSplitSegment(Segment source, int start, int end, int partIndex, int partCount)
        {
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;

            while (end > start && char.IsWhiteSpace(text[end - 1]))
                end--;

            if (end <= start)
                return;

            var content = text[start..end];
            var splitSegment = new Segment(source.Kind, content)
            {
                SourceUri = source.SourceUri,
                StartOffset = ProjectSplitOffset(source, start),
                EndOffset = ProjectSplitOffset(source, end),
                CollectionName = source.CollectionName,
                UpdatedAt = source.UpdatedAt
            };

            foreach (var (key, value) in source.Metadata)
                splitSegment.Metadata[key] = value;

            splitSegment.Metadata["embeddingInputSplit"] = true;
            splitSegment.Metadata["embeddingInputSplitDepth"] = depth;
            splitSegment.Metadata["embeddingInputSplitPartIndex"] = partIndex;
            splitSegment.Metadata["embeddingInputSplitPartCount"] = partCount;
            splitSegment.Metadata["embeddingInputSplitParentSegmentId"] = source.Id.ToString();
            splitSegment.Metadata["embeddingInputSplitReason"] = TruncateFallbackReason(reason);
            splitSegment.Metadata["estimatedTokens"] = tokenCounter(content);
            splitSegment.Metadata["maxInputTokens"] = maxInputTokens;

            splitSegments.Add(splitSegment);
        }
    }

    private static int FindNaturalSplitIndex(string text)
    {
        var midpoint = text.Length / 2;
        var searchRadius = Math.Max(32, text.Length / 4);
        var lowerBound = Math.Max(1, midpoint - searchRadius);
        var upperBound = Math.Min(text.Length - 2, midpoint + searchRadius);

        for (var offset = 0; offset <= searchRadius; offset++)
        {
            var left = midpoint - offset;
            if (left >= lowerBound && IsNaturalSplitBoundary(text[left - 1]))
                return left;

            var right = midpoint + offset;
            if (right <= upperBound && IsNaturalSplitBoundary(text[right]))
                return right + 1;
        }

        return midpoint;
    }

    private static bool IsNaturalSplitBoundary(char ch) =>
        char.IsWhiteSpace(ch) ||
        ch is '.' or ',' or ';' or ':' or ')' or ']' or '}' or '>';

    private static int ProjectSplitOffset(Segment segment, int contentOffset)
    {
        if (segment.EndOffset <= segment.StartOffset)
            return segment.StartOffset;

        var projectedOffset = segment.StartOffset + contentOffset;
        return Math.Clamp(projectedOffset, segment.StartOffset, segment.EndOffset);
    }

    private static string TruncateFallbackReason(string reason)
    {
        if (reason.Length <= MaximumStoredFallbackReasonLength)
            return reason;

        return reason[..MaximumStoredFallbackReasonLength];
    }

    private static StringContent CreateJsonContent(Dictionary<string, object?> payload) =>
        new(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

    private static Uri BuildOpenAiEmbeddingUri(string? baseUrl)
    {
        var text = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultOpenAiBaseUrl
            : baseUrl.Trim();

        if (text.TrimEnd('/').EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            return new Uri(text, UriKind.Absolute);

        var normalizedBaseUrl = NormalizeOpenAiBaseUrl(text);
        return new Uri($"{normalizedBaseUrl.TrimEnd('/')}/embeddings", UriKind.Absolute);
    }

    private static string NormalizeOpenAiBaseUrl(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            return baseUrl;

        return $"{baseUrl.TrimEnd('/')}/v1";
    }

    private static Uri BuildGoogleEmbeddingUri(string? baseUrl, string modelPath)
    {
        var text = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultGoogleBaseUrl
            : baseUrl.Trim();

        if (text.Contains(":embedContent", StringComparison.OrdinalIgnoreCase))
            return new Uri(text, UriKind.Absolute);

        return new Uri($"{text.TrimEnd('/')}/{modelPath}:embedContent", UriKind.Absolute);
    }

    private static string NormalizeGoogleModelPath(string model)
    {
        var trimmed = model.Trim().Trim('/');
        return trimmed.StartsWith("models/", StringComparison.Ordinal)
            ? trimmed
            : $"models/{trimmed}";
    }

    private static Dictionary<string, object?> BuildGoogleContent(
        EmbeddingInput input,
        bool includeBinaryData,
        bool sendInlineData)
    {
        var parts = new List<Dictionary<string, object?>>();
        foreach (var part in input.Parts)
        {
            var text = BuildPartTextProjection(part, parts.Count, includeBinaryData);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(new Dictionary<string, object?> { ["text"] = text });

            if (sendInlineData && part.HasBinary)
            {
                parts.Add(new Dictionary<string, object?>
                {
                    ["inlineData"] = new Dictionary<string, object?>
                    {
                        ["mimeType"] = ResolveMimeType(part),
                        ["data"] = Convert.ToBase64String(part.Binary!.Value.Span)
                    }
                });
            }
        }

        if (parts.Count == 0)
            throw new EmbeddingException("Embedding content is empty.");

        return new Dictionary<string, object?> { ["parts"] = parts };
    }

    private static string BuildTextProjection(EmbeddingInput input, bool includeBinaryData)
    {
        if (input.Parts.Count == 0)
            throw new EmbeddingException("Embedding content is empty.");

        var parts = new List<string>();
        for (var i = 0; i < input.Parts.Count; i++)
        {
            var projection = BuildPartTextProjection(input.Parts[i], i, includeBinaryData);
            if (!string.IsNullOrWhiteSpace(projection))
                parts.Add(projection);
        }

        if (parts.Count == 0)
            throw new EmbeddingException("Embedding content is empty.");

        return string.Join("\n\n", parts);
    }

    private static string BuildPartTextProjection(
        EmbeddingContentPart part,
        int index,
        bool includeBinaryData)
    {
        if (!part.HasBinary)
            return part.Text ?? string.Empty;

        var mimeType = ResolveMimeType(part);
        var sb = new StringBuilder();
        sb.Append("<SkyweaverPreservedContent");
        AppendXmlAttribute(sb, "index", index.ToString(CultureInfo.InvariantCulture));
        AppendXmlAttribute(sb, "mimeType", mimeType);
        AppendXmlAttribute(sb, "sourceUri", part.SourceUri);
        AppendXmlAttribute(sb, "name", part.Name);
        AppendXmlAttribute(sb, "bytes", part.Binary!.Value.Length.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine(">");

        if (part.HasText)
        {
            sb.Append("  <Text>");
            sb.Append(EscapeXml(part.Text!));
            sb.AppendLine("</Text>");
        }

        foreach (var (key, value) in part.Metadata)
        {
            sb.Append("  <Meta");
            AppendXmlAttribute(sb, "key", key);
            sb.Append(">");
            sb.Append(EscapeXml(FormatMetadataValue(value)));
            sb.AppendLine("</Meta>");
        }

        if (includeBinaryData)
        {
            sb.Append("  <Binary encoding=\"base64\">");
            sb.Append(Convert.ToBase64String(part.Binary.Value.Span));
            sb.AppendLine("</Binary>");
        }

        sb.Append("</SkyweaverPreservedContent>");
        return sb.ToString();
    }

    private static void CopyUserParameters(
        IReadOnlyDictionary<string, object?> source,
        Dictionary<string, object?> destination,
        params string[] reserved)
    {
        foreach (var (key, value) in source)
        {
            if (reserved.Any(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase)))
                continue;

            destination[key] = value;
        }
    }

    private static bool ContainsKey(IReadOnlyDictionary<string, object?> values, string key) =>
        values.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    private static bool GetBooleanParameter(IReadOnlyDictionary<string, object?> values, string key)
    {
        foreach (var (candidateKey, value) in values)
        {
            if (!string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
                continue;

            return value switch
            {
                bool b => b,
                string text when bool.TryParse(text, out var parsed) => parsed,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                _ => false
            };
        }

        return false;
    }

    private static string ResolveMimeType(EmbeddingContentPart part) =>
        string.IsNullOrWhiteSpace(part.MimeType)
            ? "application/octet-stream"
            : part.MimeType!;

    private static string FormatMetadataValue(object? value)
    {
        if (value is null)
            return string.Empty;

        if (value is string text)
            return text;

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static void AppendXmlAttribute(StringBuilder sb, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        sb.Append(' ');
        sb.Append(name);
        sb.Append("=\"");
        sb.Append(EscapeXml(value));
        sb.Append('"');
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string body,
        EmbeddingApiType apiType)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase
            : body.Trim();

        if (detail is { Length: > 1000 })
            detail = detail[..1000];

        throw new EmbeddingException(
            $"{apiType} embedding request failed with HTTP {(int)response.StatusCode}: {detail}");
    }

    private static EmbeddingVector ParseOpenAiEmbedding(string body, string? contentType)
    {
        using var doc = ParseJsonResponse(body, EmbeddingApiType.OpenAI, contentType);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new EmbeddingException("OpenAI embedding response does not contain a data array.");

        var first = data.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined
            || !first.TryGetProperty("embedding", out var embedding))
        {
            throw new EmbeddingException("OpenAI embedding response does not contain an embedding vector.");
        }

        return new EmbeddingVector(ReadEmbeddingValues(embedding));
    }

    private static EmbeddingVector ParseGoogleEmbedding(string body, string? contentType)
    {
        using var doc = ParseJsonResponse(body, EmbeddingApiType.Google, contentType);
        var root = doc.RootElement;

        if (root.TryGetProperty("embedding", out var embedding)
            && embedding.TryGetProperty("values", out var values))
        {
            return new EmbeddingVector(ReadEmbeddingValues(values));
        }

        if (root.TryGetProperty("embeddings", out var embeddings)
            && embeddings.ValueKind == JsonValueKind.Array)
        {
            var first = embeddings.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Undefined
                && first.TryGetProperty("values", out var batchValues))
            {
                return new EmbeddingVector(ReadEmbeddingValues(batchValues));
            }
        }

        throw new EmbeddingException("Google embedding response does not contain an embedding vector.");
    }

    private static JsonDocument ParseJsonResponse(
        string body,
        EmbeddingApiType apiType,
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new EmbeddingException($"{apiType} embedding response body is empty.");

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            var typeDetail = string.IsNullOrWhiteSpace(contentType)
                ? string.Empty
                : $" Content-Type: {contentType}.";

            throw new EmbeddingException(
                $"{apiType} embedding response is not valid JSON.{typeDetail} " +
                "Check that the Base URL points to the provider API endpoint instead of a web page. " +
                $"Response preview: {CreateResponsePreview(body)}",
                ex);
        }
    }

    private static string CreateResponsePreview(string body)
    {
        var preview = body.Trim();
        if (preview.Length > 500)
            preview = preview[..500];

        return preview;
    }

    private static float[] ReadEmbeddingValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(v => v.GetSingle()).ToArray();

        if (element.ValueKind == JsonValueKind.String)
            return ReadBase64FloatVector(element.GetString()!);

        throw new EmbeddingException("Embedding vector must be a numeric array or base64 string.");
    }

    private static float[] ReadBase64FloatVector(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length % sizeof(float) != 0)
            throw new EmbeddingException("Base64 embedding vector length is not a multiple of 4 bytes.");

        var result = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));

        return result;
    }
}
