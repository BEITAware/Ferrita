using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace Skyweaver.Services.SkyweaverTools
{
    public sealed class SkyweaverToolInvocationStreamingParser
    {
        private static readonly Regex s_attributePattern = new(
            @"(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IReadOnlyDictionary<string, SkyweaverToolDefinition> _toolDefinitions;

        public SkyweaverToolInvocationStreamingParser(IEnumerable<SkyweaverToolDefinition> toolDefinitions)
        {
            _toolDefinitions = (toolDefinitions ?? Array.Empty<SkyweaverToolDefinition>())
                .Where(definition => definition != null)
                .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<SkyweaverStreamingToolCallSnapshot> Parse(string? rawContent)
        {
            if (string.IsNullOrEmpty(rawContent))
            {
                return Array.Empty<SkyweaverStreamingToolCallSnapshot>();
            }

            var snapshots = new List<SkyweaverStreamingToolCallSnapshot>();
            var searchIndex = 0;
            var partIndex = 0;
            var toolCallIndex = 0;

            while (searchIndex < rawContent.Length)
            {
                var toolStartIndex = IndexOfOpeningTag(rawContent, "Tool", searchIndex);
                if (toolStartIndex < 0)
                {
                    break;
                }

                if (toolStartIndex > searchIndex)
                {
                    partIndex++;
                }

                var toolOpenTagEndIndex = FindTagEnd(rawContent, toolStartIndex);
                var hasCompleteOpenTag = toolOpenTagEndIndex >= 0;
                var toolOpenTag = hasCompleteOpenTag
                    ? rawContent[toolStartIndex..toolOpenTagEndIndex]
                    : rawContent[toolStartIndex..];
                toolCallIndex++;
                var currentPartIndex = partIndex;
                var toolName = GetAttributeValue(toolOpenTag, "ToolName") ?? GetAttributeValue(toolOpenTag, "Name");
                var normalizedToolName = (toolName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(normalizedToolName))
                {
                    _toolDefinitions.TryGetValue(normalizedToolName, out var toolDefinition);

                    IReadOnlyList<SkyweaverStreamingToolParameterSnapshot> parameters;
                    int toolEndIndex;
                    bool isInvocationClosed;

                    if (!hasCompleteOpenTag)
                    {
                        parameters = Array.Empty<SkyweaverStreamingToolParameterSnapshot>();
                        toolEndIndex = rawContent.Length;
                        isInvocationClosed = false;
                    }
                    else if (IsSelfClosingTag(toolOpenTag))
                    {
                        parameters = Array.Empty<SkyweaverStreamingToolParameterSnapshot>();
                        toolEndIndex = toolOpenTagEndIndex;
                        isInvocationClosed = true;
                    }
                    else
                    {
                        parameters = ParseParameterizedToolBody(
                            rawContent,
                            toolDefinition?.Parameters ?? Array.Empty<SkyweaverToolParameterDefinition>(),
                            toolOpenTagEndIndex,
                            out toolEndIndex,
                            out isInvocationClosed);
                    }

                    snapshots.Add(new SkyweaverStreamingToolCallSnapshot
                    {
                        PartIndex = currentPartIndex,
                        ToolCallIndex = toolCallIndex,
                        ToolName = normalizedToolName,
                        ToolXmlFragment = rawContent[toolStartIndex..Math.Min(toolEndIndex, rawContent.Length)],
                        IsInvocationClosed = isInvocationClosed,
                        Parameters = parameters
                    });

                    partIndex++;
                    if (!isInvocationClosed)
                    {
                        break;
                    }

                    searchIndex = Math.Max(toolEndIndex, toolOpenTagEndIndex);
                    continue;
                }

                if (!hasCompleteOpenTag)
                {
                    break;
                }

                var fallbackToolEndIndex = toolOpenTagEndIndex;
                var isFallbackInvocationClosed = true;
                if (!IsSelfClosingTag(toolOpenTag) &&
                    !TryFindToolClose(rawContent, toolOpenTagEndIndex, out fallbackToolEndIndex))
                {
                    fallbackToolEndIndex = rawContent.Length;
                    isFallbackInvocationClosed = false;
                }

                partIndex++;
                if (!isFallbackInvocationClosed)
                {
                    break;
                }

                searchIndex = Math.Max(fallbackToolEndIndex, toolOpenTagEndIndex);
            }

            return snapshots;
        }

        private static IReadOnlyList<SkyweaverStreamingToolParameterSnapshot> ParseParameterizedToolBody(
            string rawContent,
            IReadOnlyList<SkyweaverToolParameterDefinition> parameterDefinitions,
            int bodyStartIndex,
            out int toolEndIndex,
            out bool isInvocationClosed)
        {
            var parameterDefinitionsByName = parameterDefinitions
                .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var orderedParameters = new List<ParameterBuilder>();
            var parametersByName = new Dictionary<string, ParameterBuilder>(StringComparer.OrdinalIgnoreCase);
            ActiveParameterContext? activeParameter = null;
            var cursor = bodyStartIndex;

            while (cursor < rawContent.Length)
            {
                var nextTagIndex = rawContent.IndexOf('<', cursor);
                if (nextTagIndex < 0)
                {
                    AppendTrailingParameterText(activeParameter, rawContent, cursor);
                    toolEndIndex = rawContent.Length;
                    isInvocationClosed = false;
                    return BuildParameterSnapshots(orderedParameters);
                }

                if (activeParameter != null && nextTagIndex > cursor)
                {
                    activeParameter.Parameter.ValueBuilder.Append(rawContent, cursor, nextTagIndex - cursor);
                }

                cursor = nextTagIndex;

                if (activeParameter == null && StartsWithClosingTag(rawContent, "Tool", cursor))
                {
                    if (TryReadTag(rawContent, cursor, out var toolCloseTag))
                    {
                        toolEndIndex = toolCloseTag.EndIndex;
                        isInvocationClosed = true;
                        return BuildParameterSnapshots(orderedParameters);
                    }

                    toolEndIndex = rawContent.Length;
                    isInvocationClosed = false;
                    return BuildParameterSnapshots(orderedParameters);
                }

                if (!TryReadTag(rawContent, cursor, out var tag))
                {
                    if (activeParameter == null)
                    {
                        AddPartialParameterStart(
                            rawContent,
                            cursor,
                            parametersByName,
                            orderedParameters);
                    }
                    else
                    {
                        AppendTrailingParameterText(activeParameter, rawContent, cursor);
                    }

                    toolEndIndex = rawContent.Length;
                    isInvocationClosed = false;
                    return BuildParameterSnapshots(orderedParameters);
                }

                cursor = tag.EndIndex;

                if (tag.Kind == ParsedTagKind.Special)
                {
                    if (activeParameter != null)
                    {
                        AppendSpecialTagText(activeParameter.Parameter.ValueBuilder, tag.RawText);
                    }

                    continue;
                }

                if (activeParameter == null)
                {
                    if (tag.Kind != ParsedTagKind.Start || string.Equals(tag.Name, "Tool", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parameterName = ResolveParameterName(tag);
                    if (parameterName.Length == 0)
                    {
                        continue;
                    }

                    parameterDefinitionsByName.TryGetValue(parameterName, out var definition);
                    var parameter = GetOrCreateParameterBuilder(parametersByName, orderedParameters, parameterName);
                    parameter.ValueBuilder.Clear();
                    parameter.IsClosed = false;

                    var attributeValue = GetAttributeValue(tag.RawText, "Value");
                    if (!string.IsNullOrWhiteSpace(attributeValue))
                    {
                        parameter.ValueBuilder.Append(attributeValue);
                    }

                    if (!tag.IsSelfClosing)
                    {
                        activeParameter = new ActiveParameterContext(
                            parameter,
                            string.IsNullOrWhiteSpace(definition?.Name) ? tag.Name : tag.Name.Trim());
                    }
                    else
                    {
                        parameter.IsClosed = true;
                    }

                    continue;
                }

                if (tag.Kind == ParsedTagKind.End)
                {
                    if (activeParameter.NestedElementNames.Count == 0 &&
                        string.Equals(tag.Name, activeParameter.RootElementName, StringComparison.OrdinalIgnoreCase))
                    {
                        activeParameter.Parameter.IsClosed = true;
                        activeParameter = null;
                    }
                    else
                    {
                        activeParameter.Parameter.ValueBuilder.Append(tag.RawText);
                        if (activeParameter.NestedElementNames.Count > 0)
                        {
                            activeParameter.NestedElementNames.Pop();
                        }
                    }

                    continue;
                }

                activeParameter.Parameter.ValueBuilder.Append(tag.RawText);
                if (!tag.IsSelfClosing)
                {
                    activeParameter.NestedElementNames.Push(tag.Name);
                }
            }

            toolEndIndex = rawContent.Length;
            isInvocationClosed = false;
            return BuildParameterSnapshots(orderedParameters);
        }

        private static IReadOnlyList<SkyweaverStreamingToolParameterSnapshot> BuildParameterSnapshots(
            IEnumerable<ParameterBuilder> parameters)
        {
            return parameters.Select(parameter => new SkyweaverStreamingToolParameterSnapshot
                {
                    Name = parameter.Name,
                    Value = parameter.ValueBuilder.ToString(),
                    IsClosed = parameter.IsClosed
                })
                .ToArray();
        }

        private static ParameterBuilder GetOrCreateParameterBuilder(
            IDictionary<string, ParameterBuilder> parametersByName,
            ICollection<ParameterBuilder> orderedParameters,
            string parameterName)
        {
            if (parametersByName.TryGetValue(parameterName, out var existing))
            {
                return existing;
            }

            var parameter = new ParameterBuilder(parameterName);
            parametersByName[parameterName] = parameter;
            orderedParameters.Add(parameter);
            return parameter;
        }

        private static void AppendTrailingParameterText(
            ActiveParameterContext? activeParameter,
            string rawContent,
            int startIndex)
        {
            if (activeParameter == null || startIndex >= rawContent.Length)
            {
                return;
            }

            activeParameter.Parameter.ValueBuilder.Append(rawContent[startIndex..]);
        }

        private static string ResolveParameterName(ParsedTag tag)
        {
            if (string.Equals(tag.Name, "Parameter", StringComparison.OrdinalIgnoreCase))
            {
                return GetAttributeValue(tag.RawText, "Name")
                       ?? GetAttributeValue(tag.RawText, "ParameterName")
                       ?? GetAttributeValue(tag.RawText, "Key")
                       ?? string.Empty;
            }

            return tag.Name;
        }

        private static void AddPartialParameterStart(
            string rawContent,
            int tagStartIndex,
            IDictionary<string, ParameterBuilder> parametersByName,
            ICollection<ParameterBuilder> orderedParameters)
        {
            if (!TryReadPartialStartTag(rawContent, tagStartIndex, out var tag) ||
                string.Equals(tag.Name, "Tool", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var parameterName = ResolveParameterName(tag);
            if (parameterName.Length == 0)
            {
                return;
            }

            var parameter = GetOrCreateParameterBuilder(parametersByName, orderedParameters, parameterName);
            parameter.IsClosed = false;

            var attributeValue = GetAttributeValue(tag.RawText, "Value");
            if (!string.IsNullOrWhiteSpace(attributeValue))
            {
                parameter.ValueBuilder.Clear();
                parameter.ValueBuilder.Append(attributeValue);
            }
        }

        private static bool TryReadPartialStartTag(string rawContent, int tagStartIndex, out ParsedTag tag)
        {
            tag = ParsedTag.Empty;

            if (tagStartIndex < 0 ||
                tagStartIndex >= rawContent.Length ||
                rawContent[tagStartIndex] != '<' ||
                tagStartIndex + 1 >= rawContent.Length ||
                rawContent[tagStartIndex + 1] is '/' or '!' or '?')
            {
                return false;
            }

            var nameStartIndex = tagStartIndex + 1;
            while (nameStartIndex < rawContent.Length && char.IsWhiteSpace(rawContent[nameStartIndex]))
            {
                nameStartIndex++;
            }

            var nameEndIndex = nameStartIndex;
            while (nameEndIndex < rawContent.Length && !IsXmlNameBoundary(rawContent[nameEndIndex]))
            {
                nameEndIndex++;
            }

            if (nameEndIndex <= nameStartIndex)
            {
                return false;
            }

            tag = new ParsedTag(
                rawContent[tagStartIndex..],
                rawContent[nameStartIndex..nameEndIndex],
                ParsedTagKind.Start,
                IsSelfClosing: false,
                tagStartIndex,
                rawContent.Length);
            return true;
        }

        private static bool TryFindToolClose(string rawContent, int searchStartIndex, out int toolEndIndex)
        {
            var toolCloseStartIndex = IndexOfClosingTagStart(rawContent, "Tool", searchStartIndex);
            if (toolCloseStartIndex < 0)
            {
                toolEndIndex = rawContent.Length;
                return false;
            }

            toolEndIndex = FindTagEnd(rawContent, toolCloseStartIndex);
            return toolEndIndex >= 0;
        }

        private static bool TryReadTag(string rawContent, int tagStartIndex, out ParsedTag tag)
        {
            tag = ParsedTag.Empty;

            if (tagStartIndex < 0 || tagStartIndex >= rawContent.Length || rawContent[tagStartIndex] != '<')
            {
                return false;
            }

            if (rawContent.AsSpan(tagStartIndex).StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
            {
                var commentEndIndex = rawContent.IndexOf("-->", tagStartIndex, StringComparison.Ordinal);
                if (commentEndIndex < 0)
                {
                    return false;
                }

                var endIndex = commentEndIndex + 3;
                tag = ParsedTag.CreateSpecial(rawContent[tagStartIndex..endIndex], tagStartIndex, endIndex);
                return true;
            }

            if (rawContent.AsSpan(tagStartIndex).StartsWith("<![CDATA[".AsSpan(), StringComparison.Ordinal))
            {
                var cdataEndIndex = rawContent.IndexOf("]]>", tagStartIndex, StringComparison.Ordinal);
                if (cdataEndIndex < 0)
                {
                    return false;
                }

                var endIndex = cdataEndIndex + 3;
                tag = ParsedTag.CreateSpecial(rawContent[tagStartIndex..endIndex], tagStartIndex, endIndex);
                return true;
            }

            if (rawContent.AsSpan(tagStartIndex).StartsWith("<?".AsSpan(), StringComparison.Ordinal))
            {
                var processingInstructionEndIndex = rawContent.IndexOf("?>", tagStartIndex, StringComparison.Ordinal);
                if (processingInstructionEndIndex < 0)
                {
                    return false;
                }

                var endIndex = processingInstructionEndIndex + 2;
                tag = ParsedTag.CreateSpecial(rawContent[tagStartIndex..endIndex], tagStartIndex, endIndex);
                return true;
            }

            var tagEndIndex = FindTagEnd(rawContent, tagStartIndex);
            if (tagEndIndex < 0)
            {
                return false;
            }

            var rawTag = rawContent[tagStartIndex..tagEndIndex];
            var isEndTag = rawTag.Length > 2 && rawTag[1] == '/';
            var nameStartIndex = isEndTag ? 2 : 1;

            while (nameStartIndex < rawTag.Length && char.IsWhiteSpace(rawTag[nameStartIndex]))
            {
                nameStartIndex++;
            }

            var nameEndIndex = nameStartIndex;
            while (nameEndIndex < rawTag.Length && !IsXmlNameBoundary(rawTag[nameEndIndex]))
            {
                nameEndIndex++;
            }

            if (nameEndIndex <= nameStartIndex)
            {
                return false;
            }

            var name = rawTag[nameStartIndex..nameEndIndex];
            var isSelfClosing = !isEndTag && IsSelfClosingTag(rawTag);
            tag = new ParsedTag(
                rawTag,
                name,
                isEndTag ? ParsedTagKind.End : ParsedTagKind.Start,
                isSelfClosing,
                tagStartIndex,
                tagEndIndex);
            return true;
        }

        private static string? GetAttributeValue(string tagText, string attributeName)
        {
            if (string.IsNullOrWhiteSpace(tagText) || string.IsNullOrWhiteSpace(attributeName))
            {
                return null;
            }

            foreach (Match match in s_attributePattern.Matches(tagText))
            {
                if (!string.Equals(match.Groups["name"].Value, attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var doubleQuotedValue = match.Groups["double"].Value;
                if (doubleQuotedValue.Length > 0 || match.Groups["double"].Success)
                {
                    return doubleQuotedValue;
                }

                return match.Groups["single"].Value;
            }

            return null;
        }

        private static bool IsSelfClosingTag(string rawTag)
        {
            return rawTag.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
        }

        private static void AppendSpecialTagText(StringBuilder builder, string rawText)
        {
            ArgumentNullException.ThrowIfNull(builder);

            const string cdataPrefix = "<![CDATA[";
            const string cdataSuffix = "]]>";
            if (rawText.StartsWith(cdataPrefix, StringComparison.Ordinal) &&
                rawText.EndsWith(cdataSuffix, StringComparison.Ordinal) &&
                rawText.Length >= cdataPrefix.Length + cdataSuffix.Length)
            {
                builder.Append(rawText, cdataPrefix.Length, rawText.Length - cdataPrefix.Length - cdataSuffix.Length);
                return;
            }

            builder.Append(rawText);
        }

        private static int IndexOfOpeningTag(string text, string elementName, int startIndex = 0)
        {
            var needle = $"<{elementName}";
            var searchIndex = Math.Max(0, startIndex);

            while (searchIndex < text.Length)
            {
                var matchIndex = text.IndexOf(needle, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return -1;
                }

                var nameEndIndex = matchIndex + needle.Length;
                if (nameEndIndex >= text.Length || IsXmlNameBoundary(text[nameEndIndex]))
                {
                    return matchIndex;
                }

                searchIndex = matchIndex + 1;
            }

            return -1;
        }

        private static bool StartsWithClosingTag(string text, string elementName, int startIndex)
        {
            return IndexOfClosingTagStart(text, elementName, startIndex) == startIndex;
        }

        private static int IndexOfClosingTagStart(string text, string elementName, int startIndex = 0)
        {
            var needle = $"</{elementName}";
            var searchIndex = Math.Max(0, startIndex);

            while (searchIndex < text.Length)
            {
                var matchIndex = text.IndexOf(needle, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return -1;
                }

                var nameEndIndex = matchIndex + needle.Length;
                if (nameEndIndex >= text.Length || IsXmlNameBoundary(text[nameEndIndex]))
                {
                    return matchIndex;
                }

                searchIndex = matchIndex + 1;
            }

            return -1;
        }

        private static int FindTagEnd(string text, int tagStartIndex)
        {
            if (tagStartIndex < 0)
            {
                return -1;
            }

            var insideDoubleQuotes = false;
            var insideSingleQuotes = false;
            for (var index = tagStartIndex + 1; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '"' when !insideSingleQuotes:
                        insideDoubleQuotes = !insideDoubleQuotes;
                        break;
                    case '\'' when !insideDoubleQuotes:
                        insideSingleQuotes = !insideSingleQuotes;
                        break;
                    case '>' when !insideDoubleQuotes && !insideSingleQuotes:
                        return index + 1;
                }
            }

            return -1;
        }

        private static bool IsXmlNameBoundary(char character)
        {
            return char.IsWhiteSpace(character) || character is '>' or '/' or '?';
        }

        private sealed class ParameterBuilder
        {
            public ParameterBuilder(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public StringBuilder ValueBuilder { get; } = new();

            public bool IsClosed { get; set; }
        }

        private sealed class ActiveParameterContext
        {
            public ActiveParameterContext(ParameterBuilder parameter, string rootElementName)
            {
                Parameter = parameter;
                RootElementName = rootElementName;
            }

            public ParameterBuilder Parameter { get; }

            public string RootElementName { get; }

            public Stack<string> NestedElementNames { get; } = new();
        }

        private enum ParsedTagKind
        {
            Start = 0,
            End = 1,
            Special = 2
        }

        private readonly record struct ParsedTag(
            string RawText,
            string Name,
            ParsedTagKind Kind,
            bool IsSelfClosing,
            int StartIndex,
            int EndIndex)
        {
            public static ParsedTag Empty => new(string.Empty, string.Empty, ParsedTagKind.Special, false, -1, -1);

            public static ParsedTag CreateSpecial(string rawText, int startIndex, int endIndex)
            {
                return new ParsedTag(rawText, string.Empty, ParsedTagKind.Special, false, startIndex, endIndex);
            }
        }
    }
}
