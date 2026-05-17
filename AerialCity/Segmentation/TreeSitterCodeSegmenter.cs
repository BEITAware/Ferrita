using AerialCity.Core.Exceptions;
using AerialCity.Core.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace AerialCity.Segmentation;

/// <summary>
/// Tree-sitter based source code segmenter that keeps each chunk under a token limit.
/// </summary>
public sealed class TreeSitterCodeSegmenter : ISegmenter
{
    private static readonly Dictionary<string, string[]> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bash"] = ["Bash"],
        ["c"] = ["C"],
        ["cc"] = ["Cpp", "C++"],
        ["c-sharp"] = ["CSharp", "C#"],
        ["clj"] = ["Clojure"],
        ["clojure"] = ["Clojure"],
        ["cpp"] = ["Cpp", "C++"],
        ["cs"] = ["CSharp", "C#"],
        ["csproj"] = ["Xml", "XML"],
        ["csharp"] = ["CSharp", "C#"],
        ["c#"] = ["CSharp", "C#"],
        ["css"] = ["Css", "CSS"],
        ["go"] = ["Go"],
        ["gomod"] = ["GoMod", "Go"],
        ["h"] = ["C", "Cpp", "C++"],
        ["haskell"] = ["Haskell"],
        ["hs"] = ["Haskell"],
        ["hpp"] = ["Cpp", "C++"],
        ["html"] = ["Html", "HTML"],
        ["java"] = ["Java"],
        ["javascript"] = ["JavaScript", "Javascript"],
        ["js"] = ["JavaScript", "Javascript"],
        ["jsdoc"] = ["JsDoc", "JSDoc"],
        ["json"] = ["Json", "JSON"],
        ["jsx"] = ["JavaScript", "Javascript", "JSX"],
        ["jl"] = ["Julia"],
        ["julia"] = ["Julia"],
        ["lua"] = ["Lua"],
        ["ml"] = ["OCaml", "Ocaml"],
        ["objc"] = ["ObjC", "ObjectiveC"],
        ["ocaml"] = ["OCaml", "Ocaml"],
        ["php"] = ["Php", "PHP"],
        ["py"] = ["Python"],
        ["python"] = ["Python"],
        ["ql"] = ["QL", "Ql"],
        ["razor"] = ["Razor"],
        ["rb"] = ["Ruby"],
        ["rs"] = ["Rust"],
        ["rust"] = ["Rust"],
        ["scala"] = ["Scala"],
        ["sh"] = ["Bash"],
        ["swift"] = ["Swift"],
        ["toml"] = ["Toml", "TOML"],
        ["ts"] = ["TypeScript", "Typescript"],
        ["tsx"] = ["TSX", "TypeScript", "Typescript"],
        ["typescript"] = ["TypeScript", "Typescript"],
        ["v"] = ["Verilog"],
        ["verilog"] = ["Verilog"],
        ["xaml"] = ["Xml", "XML"],
        ["xml"] = ["Xml", "XML"],
        ["yaml"] = ["Yaml", "YAML"],
        ["yml"] = ["Yaml", "YAML"]
    };

    private readonly ILogger<TreeSitterCodeSegmenter> _logger;

    public SegmentKind OutputKind => SegmentKind.CodeBlock;

    public TreeSitterCodeSegmenter(ILogger<TreeSitterCodeSegmenter>? logger = null)
    {
        _logger = logger ?? NullLogger<TreeSitterCodeSegmenter>.Instance;
    }

    public IReadOnlyList<Segment> Segment(RawContent content, SegmentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        return SegmentCode(
            content.Text,
            content.LanguageHint,
            content.SourceUri,
            Math.Max(1, options.MaxTokensPerSegment),
            content.Metadata,
            tokenCounter: null,
            _logger);
    }

    public static IReadOnlyList<Segment> SegmentCode(
        string sourceCode,
        string? languageHint,
        string? sourceUri,
        int maxInputTokens,
        IReadOnlyDictionary<string, object>? metadata = null,
        Func<string, int>? tokenCounter = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);

        if (maxInputTokens <= 0)
            throw new SegmentationException("Max input tokens must be greater than zero.");

        if (sourceCode.Length == 0)
            return [];

        var countTokens = tokenCounter ?? EstimateTokens;
        if (!TryLoadLanguage(languageHint, sourceUri, out var language, out var resolvedLanguage, out var loadFailure))
        {
            return SegmentAsPlainTextFallback(
                sourceCode,
                languageHint,
                sourceUri,
                maxInputTokens,
                metadata,
                countTokens,
                logger,
                loadFailure);
        }

        using (language)
        using (var parser = new Parser(language))
        {
            using var tree = parser.Parse(sourceCode)
                ?? throw new SegmentationException("Tree-sitter parser returned no parse tree.");

            var root = tree.RootNode;
            var slices = CreateSlices(root, sourceCode, maxInputTokens, countTokens);
            var chunks = PackSlices(slices, sourceCode, maxInputTokens, countTokens);
            var materializedChunks = chunks
                .Select(chunk => (Chunk: chunk, Text: sourceCode[chunk.Start..chunk.End]))
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .ToArray();

            var segments = new List<Segment>(materializedChunks.Length);

            for (var i = 0; i < materializedChunks.Length; i++)
            {
                var (chunk, text) = materializedChunks[i];

                var estimatedTokens = countTokens(text);
                var segment = new Segment(SegmentKind.CodeBlock, text)
                {
                    SourceUri = sourceUri,
                    StartOffset = chunk.Start,
                    EndOffset = chunk.End
                };

                if (metadata is not null)
                {
                    foreach (var (key, value) in metadata)
                        segment.Metadata[key] = value;
                }

                segment.Metadata["language"] = resolvedLanguage;
                segment.Metadata["parser"] = "tree-sitter";
                segment.Metadata["astNodeType"] = chunk.NodeType;
                segment.Metadata["chunkIndex"] = i;
                segment.Metadata["chunkCount"] = materializedChunks.Length;
                segment.Metadata["estimatedTokens"] = estimatedTokens;
                segment.Metadata["maxInputTokens"] = maxInputTokens;

                segments.Add(segment);
            }

            logger?.LogDebug(
                "Tree-sitter segmented {SourceUri} as {Language} into {Count} chunks.",
                sourceUri ?? "(inline)",
                resolvedLanguage,
                segments.Count);

            return segments;
        }
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var lexicalTokens = 0;
        var inIdentifier = false;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                if (!inIdentifier)
                    lexicalTokens++;

                inIdentifier = true;
                continue;
            }

            inIdentifier = false;

            if (!char.IsWhiteSpace(ch))
                lexicalTokens++;
        }

        var charBasedTokens = (text.Length + 3) / 4;
        return Math.Max(1, Math.Max(lexicalTokens, charBasedTokens));
    }

    public static IReadOnlyList<string> GetLanguageCandidates(string? languageHint, string? sourceUri)
    {
        var key = NormalizeLanguageKey(languageHint);

        if (key is null && !string.IsNullOrWhiteSpace(sourceUri))
            key = NormalizeLanguageKey(GetExtensionKey(sourceUri));

        if (key is not null && LanguageAliases.TryGetValue(key, out var aliases))
            return aliases;

        if (key is not null)
            return [ToPascalCaseLanguageName(key), key];

        return ["CSharp", "C#"];
    }

    private static bool TryLoadLanguage(
        string? languageHint,
        string? sourceUri,
        out Language language,
        out string resolvedLanguage,
        out string failureMessage)
    {
        var candidates = GetLanguageCandidates(languageHint, sourceUri)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                resolvedLanguage = candidate;
                language = new Language(candidate);
                failureMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate}: {ex.Message}");
            }
        }

        language = null!;
        resolvedLanguage = ResolveFallbackLanguage(languageHint, sourceUri);
        failureMessage =
            "Could not load a Tree-sitter language for this code file. " +
            $"Tried: {string.Join(", ", candidates)}. " +
            $"Errors: {string.Join(" | ", errors)}";
        return false;
    }

    private static IReadOnlyList<Segment> SegmentAsPlainTextFallback(
        string sourceCode,
        string? languageHint,
        string? sourceUri,
        int maxInputTokens,
        IReadOnlyDictionary<string, object>? metadata,
        Func<string, int> countTokens,
        ILogger? logger,
        string reason)
    {
        logger?.LogWarning(
            "Tree-sitter language loading failed for {SourceUri}; falling back to plain text segmentation. {Reason}",
            sourceUri ?? "(inline)",
            reason);

        var segments = TextFileSegmenter.SegmentText(
            sourceCode,
            sourceUri,
            maxInputTokens,
            TextFileSegmenter.DefaultOverlapRatio,
            metadata,
            countTokens);

        var resolvedLanguage = ResolveFallbackLanguage(languageHint, sourceUri);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            segment.Metadata["language"] = resolvedLanguage;
            segment.Metadata["parser"] = "plain-text";
            segment.Metadata["treeSitterFallback"] = true;
            segment.Metadata["treeSitterFallbackReason"] = reason;
        }

        return segments;
    }

    private static string ResolveFallbackLanguage(string? languageHint, string? sourceUri)
    {
        var key = NormalizeLanguageKey(languageHint);

        if (key is null && !string.IsNullOrWhiteSpace(sourceUri))
            key = NormalizeLanguageKey(GetExtensionKey(sourceUri));

        return key ?? "unknown";
    }

    private static List<CodeSlice> CreateSlices(
        Node node,
        string sourceCode,
        int maxInputTokens,
        Func<string, int> countTokens)
    {
        var start = ClampIndex(node.StartIndex, sourceCode.Length);
        var end = ClampIndex(node.EndIndex, sourceCode.Length);
        if (end <= start)
            return [];

        var text = sourceCode[start..end];
        if (countTokens(text) <= maxInputTokens)
            return [new CodeSlice(start, end, node.Type)];

        var children = node.NamedChildren
            .Where(child =>
                child.EndIndex > child.StartIndex &&
                child.StartIndex >= start &&
                child.EndIndex <= end &&
                (child.StartIndex > start || child.EndIndex < end))
            .OrderBy(child => child.StartIndex)
            .ToArray();

        if (children.Length == 0)
            return SplitTextRange(sourceCode, start, end, "text", maxInputTokens, countTokens);

        var slices = new List<CodeSlice>();
        var cursor = start;

        foreach (var child in children)
        {
            var childStart = ClampIndex(child.StartIndex, sourceCode.Length);
            var childEnd = ClampIndex(child.EndIndex, sourceCode.Length);

            if (childStart > cursor)
                slices.AddRange(SplitTextRange(sourceCode, cursor, childStart, "trivia", maxInputTokens, countTokens));

            slices.AddRange(CreateSlices(child, sourceCode, maxInputTokens, countTokens));
            cursor = Math.Max(cursor, childEnd);
        }

        if (cursor < end)
            slices.AddRange(SplitTextRange(sourceCode, cursor, end, "trivia", maxInputTokens, countTokens));

        return slices;
    }

    private static List<CodeSlice> SplitTextRange(
        string sourceCode,
        int start,
        int end,
        string nodeType,
        int maxInputTokens,
        Func<string, int> countTokens)
    {
        var slices = new List<CodeSlice>();
        var cursor = start;

        while (cursor < end)
        {
            var nextBreak = sourceCode.IndexOf('\n', cursor, end - cursor);
            var lineEnd = nextBreak < 0 ? end : nextBreak + 1;

            AddBoundedTextSlices(sourceCode, cursor, lineEnd, nodeType, maxInputTokens, countTokens, slices);
            cursor = lineEnd;
        }

        return slices;
    }

    private static void AddBoundedTextSlices(
        string sourceCode,
        int start,
        int end,
        string nodeType,
        int maxInputTokens,
        Func<string, int> countTokens,
        List<CodeSlice> slices)
    {
        var cursor = start;
        while (cursor < end)
        {
            var text = sourceCode[cursor..end];
            if (countTokens(text) <= maxInputTokens)
            {
                slices.Add(new CodeSlice(cursor, end, nodeType));
                return;
            }

            var take = FindLargestPrefixWithinLimit(text, maxInputTokens, countTokens);
            if (take <= 0)
                take = 1;

            slices.Add(new CodeSlice(cursor, cursor + take, nodeType));
            cursor += take;
        }
    }

    private static int FindLargestPrefixWithinLimit(
        string text,
        int maxInputTokens,
        Func<string, int> countTokens)
    {
        var low = 1;
        var high = text.Length;
        var best = 0;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var tokens = countTokens(text[..mid]);
            if (tokens <= maxInputTokens)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static List<CodeSlice> PackSlices(
        IReadOnlyList<CodeSlice> slices,
        string sourceCode,
        int maxInputTokens,
        Func<string, int> countTokens)
    {
        var chunks = new List<CodeSlice>();
        var currentStart = -1;
        var currentEnd = -1;
        var currentTypes = new List<string>();

        void Flush()
        {
            if (currentStart < 0 || currentEnd <= currentStart)
                return;

            chunks.Add(new CodeSlice(currentStart, currentEnd, CollapseNodeTypes(currentTypes)));
            currentStart = -1;
            currentEnd = -1;
            currentTypes.Clear();
        }

        foreach (var slice in slices)
        {
            if (currentStart < 0)
            {
                currentStart = slice.Start;
                currentEnd = slice.End;
                currentTypes.Add(slice.NodeType);
                continue;
            }

            var candidateEnd = slice.End;
            var candidateText = sourceCode[currentStart..candidateEnd];
            if (countTokens(candidateText) > maxInputTokens)
            {
                Flush();
                currentStart = slice.Start;
                currentEnd = slice.End;
                currentTypes.Add(slice.NodeType);
                continue;
            }

            currentEnd = candidateEnd;
            currentTypes.Add(slice.NodeType);
        }

        Flush();
        return chunks;
    }

    private static string CollapseNodeTypes(IReadOnlyList<string> nodeTypes)
    {
        if (nodeTypes.Count == 0)
            return "unknown";

        var first = nodeTypes[0];
        return nodeTypes.All(type => string.Equals(type, first, StringComparison.Ordinal))
            ? first
            : "mixed";
    }

    private static string? NormalizeLanguageKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().TrimStart('.').ToLowerInvariant();
    }

    private static string? GetExtensionKey(string sourceUri)
    {
        var text = sourceUri;
        var queryIndex = text.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
            text = text[..queryIndex];

        var extension = Path.GetExtension(text);
        return string.IsNullOrWhiteSpace(extension)
            ? null
            : extension.TrimStart('.');
    }

    private static string ToPascalCaseLanguageName(string key)
    {
        var parts = key.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return key;

        return string.Concat(parts.Select(part =>
            char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..] : string.Empty)));
    }

    private static int ClampIndex(int index, int length) =>
        Math.Clamp(index, 0, length);

    private readonly record struct CodeSlice(int Start, int End, string NodeType);
}
