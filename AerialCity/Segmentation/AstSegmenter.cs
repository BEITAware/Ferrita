using AerialCity.Core.Primitives;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace AerialCity.Segmentation;

/// <summary>
/// AST-based segmenter that uses Roslyn to parse C# source code into
/// semantically meaningful code blocks (classes, methods, properties, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Each top-level declaration (namespace member) becomes a segment.
/// Methods and properties within a class are extracted as child segments
/// if they exceed <see cref="SegmentationOptions.MinCodeLines"/>.
/// </para>
/// <para>
/// The segmenter populates metadata with structural information:
/// "symbolName", "symbolKind", "parentClass", "parameters", etc.
/// </para>
/// </remarks>
public sealed class AstSegmenter : ISegmenter
{
    private readonly ILogger<AstSegmenter> _logger;

    public SegmentKind OutputKind => SegmentKind.CodeBlock;

    public AstSegmenter(ILogger<AstSegmenter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<Segment> Segment(RawContent content, SegmentationOptions options)
    {
        _logger.LogDebug("AST segmentation: {Length} chars, language={Lang}",
            content.Text.Length, content.LanguageHint ?? "csharp");

        var tree = CSharpSyntaxTree.ParseText(content.Text);
        var root = tree.GetCompilationUnitRoot();
        var segments = new List<Segment>();

        // Extract using directives as context (not a separate segment)
        var usings = string.Join("\n", root.Usings.Select(u => u.ToString()));

        foreach (var member in root.Members)
        {
            ExtractSegments(member, segments, content, options, usings, parentName: null);
        }

        _logger.LogDebug("AST segmentation produced {Count} segments", segments.Count);
        return segments;
    }

    private void ExtractSegments(
        MemberDeclarationSyntax member,
        List<Segment> segments,
        RawContent content,
        SegmentationOptions options,
        string usings,
        string? parentName)
    {
        switch (member)
        {
            case NamespaceDeclarationSyntax ns:
                foreach (var child in ns.Members)
                    ExtractSegments(child, segments, content, options, usings, ns.Name.ToString());
                break;

            case FileScopedNamespaceDeclarationSyntax fsns:
                foreach (var child in fsns.Members)
                    ExtractSegments(child, segments, content, options, usings, fsns.Name.ToString());
                break;

            case TypeDeclarationSyntax typeDecl:
                ExtractTypeSegments(typeDecl, segments, content, options, usings, parentName);
                break;

            case GlobalStatementSyntax:
            case EnumDeclarationSyntax:
            case DelegateDeclarationSyntax:
                AddMemberSegment(member, segments, content, options, usings, parentName, "declaration");
                break;
        }
    }

    private void ExtractTypeSegments(
        TypeDeclarationSyntax typeDecl,
        List<Segment> segments,
        RawContent content,
        SegmentationOptions options,
        string usings,
        string? parentName)
    {
        var typeName = typeDecl.Identifier.Text;
        var fullName = parentName is not null ? $"{parentName}.{typeName}" : typeName;
        var typeKind = typeDecl switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax => "record",
            _ => "type"
        };

        // Add the type declaration itself as a segment (signature + fields)
        var typeText = typeDecl.ToString();
        var lineCount = typeText.Count(c => c == '\n') + 1;

        if (lineCount <= options.MinCodeLines * 3)
        {
            // Small type: emit as one segment
            AddSegment(segments, typeText, content, typeDecl.SpanStart, typeDecl.Span.End,
                fullName, typeKind);
            return;
        }

        // Large type: extract individual members
        foreach (var m in typeDecl.Members)
        {
            var memberText = m.ToString();
            var memberLines = memberText.Count(c => c == '\n') + 1;

            if (memberLines < options.MinCodeLines) continue;

            var memberName = m switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text,
                PropertyDeclarationSyntax prop => prop.Identifier.Text,
                ConstructorDeclarationSyntax ctor => $"{typeName}.ctor",
                _ => null
            };

            if (memberName is null) continue;

            var memberKind = m switch
            {
                MethodDeclarationSyntax => "method",
                PropertyDeclarationSyntax => "property",
                ConstructorDeclarationSyntax => "constructor",
                _ => "member"
            };

            AddSegment(segments, memberText, content, m.SpanStart, m.Span.End,
                $"{fullName}.{memberName}", memberKind);
        }
    }

    private void AddMemberSegment(
        MemberDeclarationSyntax member,
        List<Segment> segments,
        RawContent content,
        SegmentationOptions options,
        string usings,
        string? parentName,
        string kind)
    {
        var text = member.ToString();
        if (text.Count(c => c == '\n') + 1 < options.MinCodeLines) return;
        AddSegment(segments, text, content, member.SpanStart, member.Span.End, parentName, kind);
    }

    private static void AddSegment(
        List<Segment> segments,
        string text,
        RawContent content,
        int startOffset,
        int endOffset,
        string? symbolName,
        string symbolKind)
    {
        var segment = new Segment(SegmentKind.CodeBlock, text)
        {
            SourceUri = content.SourceUri,
            StartOffset = startOffset,
            EndOffset = endOffset,
            Metadata =
            {
                ["symbolName"] = symbolName ?? "unknown",
                ["symbolKind"] = symbolKind,
                ["language"] = content.LanguageHint ?? "csharp"
            }
        };
        segments.Add(segment);
    }
}
