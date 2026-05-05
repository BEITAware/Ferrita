using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class EditFileAdvancedTool :
        ISkyweaverTool,
        ISkyweaverToolConfigurationProvider,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "EditFile_Advanced";

        private const int MaximumAnchorLines = 16;
        private const string BeginningOfFileMarker = "[BOF]";
        private const string EndOfFileMarker = "[EOF]";

        private static readonly Regex s_editMarkerPattern = new(
            @"^[ \t]*\[Existing Code\][ \t]*\r?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly SkyweaverToolDefinition s_definition = BuildDefinition(new EditFileAdvancedToolSettings());

        private static readonly UTF8Encoding s_utf8WithoutBomStrict = new(false, true);
        private static readonly UnicodeEncoding s_utf16LittleEndianStrict = new(false, false, true);
        private static readonly UnicodeEncoding s_utf16BigEndianStrict = new(true, false, true);
        private static readonly UTF32Encoding s_utf32LittleEndianStrict = new(false, false, true);
        private static readonly UTF32Encoding s_utf32BigEndianStrict = new(true, false, true);

        private static readonly byte[] s_utf8Preamble = [0xEF, 0xBB, 0xBF];
        private static readonly byte[] s_utf16LittleEndianPreamble = [0xFF, 0xFE];
        private static readonly byte[] s_utf16BigEndianPreamble = [0xFE, 0xFF];
        private static readonly byte[] s_utf32LittleEndianPreamble = [0xFF, 0xFE, 0x00, 0x00];
        private static readonly byte[] s_utf32BigEndianPreamble = [0x00, 0x00, 0xFE, 0xFF];

        public SkyweaverToolDefinition Definition => s_definition;

        public SkyweaverToolDefinition GetEffectiveDefinition(SkyweaverToolConfigurationState configuration)
        {
            return BuildDefinition(EditFileAdvancedToolSettings.FromConfiguration(configuration));
        }

        public SkyweaverToolConfigurationPresenter? CreateConfigurationPresenter(SkyweaverToolConfigurationEditorContext context)
        {
            return new EditFileAdvancedToolConfigurationPresenter(context);
        }

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            return BuildDescription(EditFileAdvancedToolSettings.FromConfiguration(context.ConfigurationState));
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("File", "FilePath", "Waiting for file path..."),
                    new ToolInvocationCardFieldDefinition("Edit text", "EditText", "Waiting for anchored edit text..."),
                    new ToolInvocationCardFieldDefinition("Encoding", "Encoding", "Default utf-8 with safe auto-detection")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var settings = EditFileAdvancedToolSettings.FromConfiguration(context.CurrentToolConfiguration);
            var requestedPath = arguments.GetString("FilePath") ?? string.Empty;
            var rawEditText = arguments.GetString("EditText") ?? string.Empty;
            WriteTargetPath? targetPath = null;

            try
            {
                targetPath = ResolveWriteTargetPath(requestedPath, context.WorkspacePath, settings.PermissionScope);

                if (Directory.Exists(targetPath.ResolvedPath))
                {
                    return SkyweaverToolResult.Failure(
                        $"Path points to a directory, not a file: {targetPath.ResolvedPath}",
                        BuildData(targetPath, settings, null, null, null, null, null, 0, didWrite: false, blockSummaries: []));
                }

                if (!File.Exists(targetPath.ResolvedPath))
                {
                    return SkyweaverToolResult.Failure(
                        $"File not found: {targetPath.ResolvedPath}",
                        BuildData(targetPath, settings, null, null, null, null, null, 0, didWrite: false, blockSummaries: []));
                }

                var editText = NormalizeEditTextArgument(rawEditText);
                var editBlocks = ParseEditBlocks(editText);
                var originalBytes = await File.ReadAllBytesAsync(targetPath.ResolvedPath, cancellationToken).ConfigureAwait(false);
                var encodingDecision = ResolveEncodingDecision(originalBytes, GetExplicitEncodingName(arguments));
                var originalContent = DecodeContent(originalBytes, encodingDecision);
                var newline = DetectDominantNewline(originalContent);
                var currentContent = originalContent;
                var blockResults = new List<AppliedEditBlock>(editBlocks.Count);

                foreach (var block in editBlocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var application = ApplyEditBlock(currentContent, block, newline);
                    blockResults.Add(application);
                    currentContent = application.UpdatedContent;
                }

                if (string.Equals(currentContent, originalContent, StringComparison.Ordinal))
                {
                    return SkyweaverToolResult.Success(
                        BuildNoChangeContent(targetPath, settings, encodingDecision, originalBytes.LongLength, originalContent.Length, blockResults),
                        BuildData(
                            targetPath,
                            settings,
                            encodingDecision,
                            originalBytes.LongLength,
                            originalContent.Length,
                            originalBytes.LongLength,
                            currentContent.Length,
                            blockResults.Count,
                            didWrite: false,
                            blockSummaries: blockResults.Select(item => item.Summary).ToArray()));
                }

                var updatedBytes = EncodeContent(currentContent, encodingDecision);
                await File.WriteAllBytesAsync(targetPath.ResolvedPath, updatedBytes, cancellationToken).ConfigureAwait(false);

                return SkyweaverToolResult.Success(
                    SkyweaverLineDiffPresentation.BuildContent(originalContent, currentContent),
                    BuildData(
                        targetPath,
                        settings,
                        encodingDecision,
                        originalBytes.LongLength,
                        originalContent.Length,
                        updatedBytes.LongLength,
                        currentContent.Length,
                        blockResults.Count,
                        didWrite: true,
                        blockSummaries: blockResults.Select(item => item.Summary).ToArray()),
                    SkyweaverToolResultPresentationHints.CreateLineDiff());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to apply advanced file edit: {ex.Message}",
                    BuildData(targetPath, settings, null, null, null, null, null, 0, didWrite: false, blockSummaries: []));
            }
        }

        private static SkyweaverToolDefinition BuildDefinition(EditFileAdvancedToolSettings settings)
        {
            return new SkyweaverToolDefinition(
                ToolName,
                BuildDescription(settings),
                "Script",
                [
                    new SkyweaverToolParameterDefinition(
                        "FilePath",
                        "目标文件路径。当前工具常见配置是 LateralFileSystemOnly：这种配置下必须使用 LateralFS\\NodeName\\relative\\file.ext，或使用已经位于 LateralFS 虚拟根目录下的实际路径；不要用原始投影源路径。FullAccess 配置下也可使用普通绝对/相对路径。LateralFS 快捷路径会被解析到节点虚拟目录，并阻止 '..' 越界。",
                        SkyweaverToolParameterType.String,
                        isRequired: true),
                    new SkyweaverToolParameterDefinition(
                        "EditText",
                        BuildEditTextParameterDescription(),
                        SkyweaverToolParameterType.String,
                        isRequired: true),
                    new SkyweaverToolParameterDefinition(
                        "Encoding",
                        "Optional text encoding name. Default is utf-8. When utf-8 is requested, the tool safely auto-detects UTF BOMs plus strong UTF-16/UTF-32 zero-byte patterns. If bytes are not valid UTF-8 and cannot be identified, pass the correct encoding explicitly.",
                        SkyweaverToolParameterType.String,
                        isRequired: false,
                        defaultValue: "utf-8")
                ]);
        }

        #pragma warning disable CS0162
        private static string BuildDescription(EditFileAdvancedToolSettings settings)
        {
            var permissionText = settings.PermissionScope == EditFileAdvancedPermissionScope.FullAccess
                ? "Permission: FullAccess, so the tool may write any file path that the process account can access."
                : "Permission: LateralFileSystemOnly, so the tool may write only inside LateralFS virtual folders. In this mode, use LateralFS\\NodeName\\relative\\file.ext or an actual path under a LateralFS virtual root; do not use the original projected source path.";

            return "Advanced existing-file editing tool. EditText is plain text, not a diff. " +
                "Primary protocol: use one or more full-line [Existing Code] ... [Existing Code] blocks. As a relaxed edge form, the first block may start with [BOF] instead of [Existing Code], and the last block may end with [EOF] instead of [Existing Code]. Inside each block, the first real line(s) are unchanged top anchors, the middle is the final edited text, and the last real line(s) are unchanged bottom anchors. [Existing Code] only marks block boundaries; it is never an anchor. " +
                "Single-block shorthand: if you omit [Existing Code] entirely, the whole EditText body is treated as one edit block. This is the fallback form for simple edits. " +
                "You may use the literal marker [BOF] as the first line of a block to anchor at the beginning of the file, and/or [EOF] as the last line of a block to anchor at the end of the file. Those marker lines are virtual anchors and are not written into the file. Use [BOF] when editing the first line, [EOF] when editing the last line, and [BOF] plus [EOF] together when rewriting the whole file. " +
                "Prefer 2-5 exact unchanged context lines around the real edit when possible. The tool considers at most 16 anchor lines on each side. " +
                "If EditText may contain '<', '>', or '&' such as XML, HTML, or generic code, wrap the entire EditText value in CDATA in the outer XML tool call; the host/parser unwraps that CDATA automatically, so do not leave literal <![CDATA[ or ]]> markers inside the edit body. " +
                "Few-shot samples: first-line edit => [BOF]\\nnew first line\\nunchanged second line. Last-line edit => unchanged previous line\\nnew last line\\n[EOF]. Relaxed first block => [BOF]\\nnew first line\\nunchanged second line\\n[Existing Code]. Relaxed last block => [Existing Code]\\nunchanged previous line\\nnew last line\\n[EOF]. Multi-edit => [Existing Code]\\nunchanged top\\nnew middle\\nunchanged bottom\\n[Existing Code]\\n\\n[Existing Code]\\nsecond top\\nsecond new middle\\nsecond bottom\\n[Existing Code]. Whole-file rewrite => [BOF]\\nfull final file content\\n[EOF]. " +
                "Only whitespace is allowed outside complete edit blocks. The standard multi-block form is [Existing Code] ... [Existing Code], with [BOF] ... [Existing Code] and [Existing Code] ... [EOF] also accepted for the outermost first/last block. If anchors are missing, duplicated, or not unique, the tool fails without writing the file. " +
                "FilePath can be a LateralFS shortcut in the form LateralFS\\NodeName\\relative\\file.ext; the shortcut is resolved to that node's virtual folder before writing, and '..' traversal outside the node is rejected. " +
                permissionText;

            return "主文件编辑工具。用于对已存在文本文件做行级别编辑；不是 diff 工具，不使用 XML 编辑标签。 " +
                "EditText 是纯文本锚点协议：固定哨兵行必须是一整行 [Existing Code]。一个 EditText 可以一次包含一个或多个编辑块；块与块之间只允许空白。 " +
                "每个编辑块的唯一合法形态是：[Existing Code]\\n未修改的上锚点行\\n编辑后的最终内容行...\\n未修改的下锚点行\\n[Existing Code]。 " +
                "注意：[Existing Code] 只是块边界，不是锚点。真正的锚点是块内最前面和最后面的原文行；它们必须仍存在于文件中、逐字符相同、并且本次不能修改或删除。 " +
                "实际改动必须发生在这两侧锚点之间：替换时把中间写成最终文本；删除时省略要删除的行；插入时把新行放在上下锚点之间。 " +
                "为了避免歧义，优先选择更大的范围：在改动前后各放 2-5 行稳定且唯一的未修改原文，重复代码多时继续扩大；工具最多会利用每侧 16 行锚点。 " +
                "不要只给待修改的一行；每个块至少需要两行内容，也就是至少一个上锚点和一个下锚点。不能把会变化的行放在块首或块尾。 " +
                "删除多行时，下锚点必须位于所有被删除行之后；若要删除/修改相隔较远的多处内容，可以在同一个 EditText 中连续写多个 [Existing Code]... [Existing Code] 编辑块。 " +
                "外层调用仍是 XML；只要 EditText 中可能出现 '<'、'>'、'&'、项目文件 XML、HTML 或泛型代码，就把整个 EditText 参数放进 CDATA。 " +
                "推荐调用形态：<EditText><![CDATA[[Existing Code]\\n未修改的上锚点行\\n编辑后的最终内容\\n未修改的下锚点行\\n[Existing Code]\\n\\n[Existing Code]\\n第二处未修改的上锚点行\\n第二处编辑后的最终内容\\n第二处未修改的下锚点行\\n[Existing Code]]]></EditText>。 " +
                "如果锚点缺失、重复、不唯一、空白不一致，工具会失败且不会写文件。 " +
                "FilePath can be a LateralFS shortcut in the form LateralFS\\NodeName\\relative\\file.ext; the shortcut is resolved to that node's virtual folder before writing, and '..' traversal outside the node is rejected. " +
                permissionText;
        }

        private static string BuildEditTextParameterDescription()
        {
            return "Anchored final-text editing parameter. EditText is plain text, not a diff. " +
                "Preferred form: one or more [Existing Code] ... [Existing Code] blocks with only whitespace outside those blocks. Relaxed edge forms [BOF] ... [Existing Code] and [Existing Code] ... [EOF] are also accepted for the outermost first/last block. Shorthand fallback: if no [Existing Code] sentinel appears, the entire EditText body becomes one edit block. " +
                "Within a block, the default form is unchanged top anchor line(s), final edited content, unchanged bottom anchor line(s). To edit the first line, you may use [BOF] as the first line inside the block. To edit the last line, you may use [EOF] as the last line inside the block. [BOF] and [EOF] are virtual file-boundary anchors and are not written into the file. " +
                "Without [BOF] or [EOF], the first and last lines inside the block must be unchanged original lines. Prefer 2-5 exact unchanged context lines when possible; repeated code may require larger anchors. " +
                "Few-shot samples: [BOF]\\nnew first line\\nunchanged second line ; unchanged previous line\\nnew last line\\n[EOF] ; [BOF]\\nnew first line\\nunchanged second line\\n[Existing Code] ; [Existing Code]\\nunchanged previous line\\nnew last line\\n[EOF] ; [BOF]\\nfull final file content\\n[EOF]. " +
                "If the content contains '<', '>', or '&', especially XML, HTML, or generic code, wrap the entire EditText value in CDATA in the outer XML tool call. The host/parser unwraps the outer CDATA automatically.";

            return "锚点式最终文本编辑参数。EditText 是纯文本，不是 diff，不要写 XML 编辑标签。固定哨兵行是一整行 [Existing Code]。 " +
                "一个 EditText 可以一次包含多个编辑块，每个块都是一对 [Existing Code] 哨兵包住若干行最终文本；块外只能有空白。 " +
                "每个块内，第一行必须是未修改的原文上锚点，最后一行必须是未修改的原文下锚点；这两行用于定位，不能编辑、删除或改空白。 " +
                "中间写编辑后的最终内容：替换就写替换后的行，删除就省略被删行，插入就把新增行放在两个锚点之间。 " +
                "不要只提供要修改的单行；至少提供上锚点 + 下锚点。优先在改动前后各包含 2-5 行稳定且唯一的未修改原文，重复代码多时扩大范围。 " +
                "删除多行时，下锚点必须放在所有被删除行之后；多个相距较远的修改请在同一个 EditText 中写多个完整编辑块。 " +
                "外层工具调用是 XML；如果内容包含 '<'、'>' 或 '&'，尤其是 XML/HTML/泛型代码，请把整个 EditText 参数包进 CDATA。";
        }

        #pragma warning restore CS0162
        private static WriteTargetPath ResolveWriteTargetPath(
            string requestedPath,
            string? workspacePath,
            EditFileAdvancedPermissionScope permissionScope)
        {
            ToolFileSystemHelper.LateralFileSystemPathResolution? lateralResolution = null;
            string resolvedPath;

            if (ToolFileSystemHelper.TryResolveLateralFileSystemShortcut(requestedPath, out var shortcutResolution))
            {
                lateralResolution = shortcutResolution;
                resolvedPath = shortcutResolution.ResolvedPath;
            }
            else
            {
                resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, workspacePath);
                if (ToolFileSystemHelper.TryGetContainingLateralFileSystemNode(resolvedPath, out var containingResolution))
                {
                    lateralResolution = containingResolution;
                }
            }

            if (permissionScope == EditFileAdvancedPermissionScope.LateralFileSystemOnly &&
                lateralResolution == null)
            {
                throw new InvalidOperationException(
                    "This tool is configured as LateralFileSystemOnly. FilePath must resolve inside a LateralFS virtual folder. Use LateralFS\\NodeName\\relative\\file.ext or an actual path under a LateralFS virtual root; do not use the original projected source path.");
            }

            return new WriteTargetPath(
                resolvedPath,
                lateralResolution?.NodeName,
                lateralResolution?.NodeId,
                lateralResolution?.NodeVirtualRootPath,
                lateralResolution?.RelativePath,
                lateralResolution?.UsedShortcut ?? false);
        }

        private static string NormalizeEditTextArgument(string rawEditText)
        {
            var normalized = rawEditText?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException("EditText cannot be empty.");
            }

            normalized = UnwrapOuterCData(normalized);

            if (!StartsWithElement(normalized, "EditText"))
            {
                return normalized;
            }

            try
            {
                var element = XElement.Parse(normalized, LoadOptions.PreserveWhitespace);
                if (!string.Equals(element.Name.LocalName, "EditText", StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }

                return UnwrapOuterCData(ExtractElementText(element).Trim());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
            {
                return normalized;
            }
        }

        private static IReadOnlyList<EditBlock> ParseEditBlocks(string editText)
        {
            editText = NormalizeRelaxedBoundarySentinels(editText);
            var matches = s_editMarkerPattern.Matches(editText);
            if (matches.Count == 0)
            {
                return ParseSingleBlockFallback(editText);
            }

            if (matches.Count % 2 != 0)
            {
                throw new InvalidOperationException(BuildUnmatchedSentinelMessage(editText));
            }

            var blocks = new List<EditBlock>(matches.Count / 2);
            var previousEnd = 0;
            for (var index = 0; index < matches.Count; index += 2)
            {
                var openMarker = matches[index];
                var closeMarker = matches[index + 1];

                if (!IsWhitespaceOnly(editText, previousEnd, openMarker.Index))
                {
                    throw new InvalidOperationException(BuildOutsideBlockTextMessage(editText));
                }

                var blockStart = openMarker.Index + openMarker.Length;
                var blockLength = closeMarker.Index - blockStart;
                var blockText = TrimSingleBoundaryLineBreak(editText.Substring(blockStart, blockLength));
                if (string.IsNullOrWhiteSpace(blockText))
                {
                    throw new InvalidOperationException("Edit block cannot be empty.");
                }

                blocks.Add(ParseEditBlock(blocks.Count + 1, blockText));
                previousEnd = closeMarker.Index + closeMarker.Length;
            }

            if (!IsWhitespaceOnly(editText, previousEnd, editText.Length))
            {
                throw new InvalidOperationException(BuildOutsideBlockTextMessage(editText));
            }

            return blocks;
        }

        private static string NormalizeRelaxedBoundarySentinels(string editText)
        {
            if (string.IsNullOrEmpty(editText))
            {
                return editText;
            }

            var split = SplitLines(editText);
            if (split.Lines.Count == 0 || !split.LineTexts.Any(IsExistingCodeSentinelLine))
            {
                return editText;
            }

            var firstContentLineIndex = FindFirstNonWhitespaceLineIndex(split.Lines);
            var lastContentLineIndex = FindLastNonWhitespaceLineIndex(split.Lines);
            if (firstContentLineIndex < 0 || lastContentLineIndex < 0)
            {
                return editText;
            }

            var rewriteStart = string.Equals(
                split.Lines[firstContentLineIndex].Text,
                BeginningOfFileMarker,
                StringComparison.Ordinal);
            var rewriteEnd = string.Equals(
                split.Lines[lastContentLineIndex].Text,
                EndOfFileMarker,
                StringComparison.Ordinal);
            if (!rewriteStart && !rewriteEnd)
            {
                return editText;
            }

            var newline = DetectDominantNewline(editText);
            var builder = new StringBuilder(editText.Length + 32);
            for (var index = 0; index < split.Lines.Count; index++)
            {
                var line = split.Lines[index];
                if (rewriteStart && index == firstContentLineIndex)
                {
                    AppendInsertedExistingCodeSentinel(builder, line.Newline, newline);
                }

                builder.Append(line.Text);
                builder.Append(line.Newline);

                if (rewriteEnd && index == lastContentLineIndex)
                {
                    if (line.Newline.Length == 0)
                    {
                        builder.Append(newline);
                    }

                    builder.Append("[Existing Code]");
                }
            }

            return builder.ToString();
        }

        private static void AppendInsertedExistingCodeSentinel(
            StringBuilder builder,
            string preferredNewline,
            string fallbackNewline)
        {
            builder.Append("[Existing Code]");
            builder.Append(preferredNewline.Length > 0 ? preferredNewline : fallbackNewline);
        }

        private static int FindFirstNonWhitespaceLineIndex(IReadOnlyList<TextLine> lines)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(lines[index].Text))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int FindLastNonWhitespaceLineIndex(IReadOnlyList<TextLine> lines)
        {
            for (var index = lines.Count - 1; index >= 0; index--)
            {
                if (!string.IsNullOrWhiteSpace(lines[index].Text))
                {
                    return index;
                }
            }

            return -1;
        }

        private static IReadOnlyList<EditBlock> ParseSingleBlockFallback(string editText)
        {
            var blockText = TrimSingleBoundaryLineBreak(editText.Trim());
            if (string.IsNullOrWhiteSpace(blockText))
            {
                throw new InvalidOperationException("EditText cannot be empty.");
            }

            try
            {
                var block = ParseEditBlock(1, blockText);
                ValidateReplacementLineCount(block, SplitLines(block.FinalText).Lines.Count);
                return [block];
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(BuildFallbackBlockFailureMessage(editText, ex.Message));
            }
        }

        private static AppliedEditBlock ApplyEditBlock(string content, EditBlock block, string newline)
        {
            var fileLines = SplitLines(content);
            var replacementLines = SplitLines(block.FinalText);
            var replacementLineTexts = replacementLines.Lines.Select(line => line.Text).ToArray();
            ValidateReplacementLineCount(block, replacementLines.Lines.Count);
            var match = ResolveUniqueBlockMatch(
                fileLines.LineTexts,
                replacementLineTexts,
                block.AnchoredAtBeginningOfFile,
                block.AnchoredAtEndOfFile,
                block.Index);
            var replacementText = NormalizeReplacementLineEndings(block.FinalText, newline);
            var startOffset = GetLineStartOffset(fileLines.Lines, match.StartLineIndex);
            var endOffset = GetLineEndOffset(fileLines.Lines, match.EndLineExclusive);
            var originalSpan = BuildTextFromLines(fileLines.Lines, match.StartLineIndex, match.EndLineExclusive);
            replacementText = PreserveOriginalTrailingLineBreak(replacementText, fileLines.Lines, match);
            var updatedContent = content[..startOffset] + replacementText + content[endOffset..];
            var changed = !string.Equals(originalSpan, replacementText, StringComparison.Ordinal);
            var summaryStartLine = fileLines.Lines.Count == 0 ? 0 : match.StartLineIndex + 1;

            return new AppliedEditBlock(
                updatedContent,
                new AppliedEditBlockSummary(
                    block.Index,
                    summaryStartLine,
                    match.EndLineExclusive,
                    match.PrefixLineCount,
                    match.SuffixLineCount,
                    match.EndLineExclusive - match.StartLineIndex,
                    replacementLines.Lines.Count,
                    changed));
        }

        private static BlockMatch ResolveUniqueBlockMatch(
            IReadOnlyList<string> fileLineTexts,
            IReadOnlyList<string> replacementLineTexts,
            bool anchoredAtBeginningOfFile,
            bool anchoredAtEndOfFile,
            int blockIndex)
        {
            var replacementLineCount = replacementLineTexts.Count;
            var minimumPrefixLines = anchoredAtBeginningOfFile ? 0 : 1;
            var minimumSuffixLines = anchoredAtEndOfFile ? 0 : 1;
            var maximumPrefixLines = Math.Min(MaximumAnchorLines, replacementLineCount - minimumSuffixLines);
            var candidatesBySpan = new Dictionary<(int Start, int End), BlockMatch>();
            var bestAnchorLineScore = 0;

            for (var prefixLineCount = maximumPrefixLines; prefixLineCount >= minimumPrefixLines; prefixLineCount--)
            {
                var maximumSuffixLines = Math.Min(MaximumAnchorLines, replacementLineCount - prefixLineCount);
                for (var suffixLineCount = maximumSuffixLines; suffixLineCount >= minimumSuffixLines; suffixLineCount--)
                {
                    var matches = FindMatches(
                        fileLineTexts,
                        replacementLineTexts,
                        prefixLineCount,
                        suffixLineCount,
                        anchoredAtBeginningOfFile,
                        anchoredAtEndOfFile);

                    if (matches.Count == 1)
                    {
                        var anchorLineScore = prefixLineCount + suffixLineCount;
                        if (anchorLineScore < bestAnchorLineScore)
                        {
                            continue;
                        }

                        if (anchorLineScore > bestAnchorLineScore)
                        {
                            candidatesBySpan.Clear();
                            bestAnchorLineScore = anchorLineScore;
                        }

                        var match = matches[0];
                        candidatesBySpan.TryAdd((match.StartLineIndex, match.EndLineExclusive), match);
                    }
                }
            }

            if (candidatesBySpan.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Edit block {blockIndex} could not be uniquely located. Use unchanged original anchor lines, or [BOF]/[EOF] file-boundary markers when editing the first or last line. Add more exact unchanged context around the real edit and make sure changed/deleted lines stay between the anchors.");
            }

            if (candidatesBySpan.Count > 1)
            {
                var spans = string.Join(
                    ", ",
                    candidatesBySpan.Values
                        .OrderBy(item => item.StartLineIndex)
                        .Take(5)
                        .Select(item => $"{item.StartLineIndex + 1}-{item.EndLineExclusive}"));
                throw new InvalidOperationException(
                    $"Edit block {blockIndex} is ambiguous across different line spans ({spans}). Use a larger block with more unchanged original anchor lines at the top and bottom until the target span is unique.");
            }

            return candidatesBySpan.Values.Single();
        }

        private static List<BlockMatch> FindMatches(
            IReadOnlyList<string> fileLineTexts,
            IReadOnlyList<string> replacementLineTexts,
            int prefixLineCount,
            int suffixLineCount,
            bool anchoredAtBeginningOfFile,
            bool anchoredAtEndOfFile)
        {
            var matches = new List<BlockMatch>();
            var prefix = replacementLineTexts.Take(prefixLineCount).ToArray();
            var suffix = replacementLineTexts.Skip(replacementLineTexts.Count - suffixLineCount).ToArray();

            var maximumStart = anchoredAtBeginningOfFile
                ? 0
                : fileLineTexts.Count - prefixLineCount;

            for (var start = 0; start <= maximumStart; start++)
            {
                if (prefixLineCount > 0 && !SequenceEquals(fileLineTexts, start, prefix))
                {
                    continue;
                }

                if (anchoredAtEndOfFile)
                {
                    var suffixStart = fileLineTexts.Count - suffixLineCount;
                    if (suffixStart < start + prefixLineCount)
                    {
                        continue;
                    }

                    if (suffixLineCount > 0 && !SequenceEquals(fileLineTexts, suffixStart, suffix))
                    {
                        continue;
                    }

                    matches.Add(new BlockMatch(
                        start,
                        suffixStart + suffixLineCount,
                        prefixLineCount,
                        suffixLineCount));
                    continue;
                }

                var suffixSearchStart = start + prefixLineCount;
                for (var suffixStart = suffixSearchStart; suffixStart <= fileLineTexts.Count - suffixLineCount; suffixStart++)
                {
                    if (suffixLineCount > 0 && !SequenceEquals(fileLineTexts, suffixStart, suffix))
                    {
                        continue;
                    }

                    matches.Add(new BlockMatch(
                        start,
                        suffixStart + suffixLineCount,
                        prefixLineCount,
                        suffixLineCount));
                }
            }

            return matches;
        }

        private static bool SequenceEquals(IReadOnlyList<string> lines, int startIndex, IReadOnlyList<string> expected)
        {
            if (startIndex < 0 || startIndex + expected.Count > lines.Count)
            {
                return false;
            }

            for (var index = 0; index < expected.Count; index++)
            {
                if (!string.Equals(lines[startIndex + index], expected[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static SplitLineResult SplitLines(string text)
        {
            var lines = new List<TextLine>();
            var offset = 0;
            var index = 0;

            while (index < text.Length)
            {
                var lineStart = index;
                while (index < text.Length && text[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                var lineText = text[lineStart..index];
                var newline = string.Empty;
                if (index < text.Length)
                {
                    if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        newline = "\r\n";
                        index += 2;
                    }
                    else
                    {
                        newline = text[index].ToString();
                        index++;
                    }
                }

                lines.Add(new TextLine(lineText, newline, offset));
                offset = index;
            }

            if (text.Length == 0)
            {
                return new SplitLineResult([], []);
            }

            return new SplitLineResult(lines, lines.Select(line => line.Text).ToArray());
        }

        private static string BuildTextFromLines(IReadOnlyList<TextLine> lines, int startLineIndex, int endLineExclusive)
        {
            if (startLineIndex < 0 || endLineExclusive > lines.Count || startLineIndex >= endLineExclusive)
            {
                return string.Empty;
            }

            var startOffset = lines[startLineIndex].StartOffset;
            var endOffset = GetLineEndOffset(lines, endLineExclusive);
            var builder = new StringBuilder(endOffset - startOffset);
            for (var index = startLineIndex; index < endLineExclusive; index++)
            {
                builder.Append(lines[index].Text);
                builder.Append(lines[index].Newline);
            }

            return builder.ToString();
        }

        private static int GetLineStartOffset(IReadOnlyList<TextLine> lines, int startLineIndex)
        {
            if (startLineIndex <= 0 || lines.Count == 0)
            {
                return 0;
            }

            if (startLineIndex >= lines.Count)
            {
                return GetLineEndOffset(lines, lines.Count);
            }

            return lines[startLineIndex].StartOffset;
        }

        private static int GetLineEndOffset(IReadOnlyList<TextLine> lines, int endLineExclusive)
        {
            if (endLineExclusive <= 0)
            {
                return 0;
            }

            var lastLine = lines[endLineExclusive - 1];
            return lastLine.StartOffset + lastLine.Text.Length + lastLine.Newline.Length;
        }

        private static string NormalizeReplacementLineEndings(string text, string newline)
        {
            var split = SplitLines(text);
            if (split.Lines.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            for (var index = 0; index < split.Lines.Count; index++)
            {
                var line = split.Lines[index];
                builder.Append(line.Text);
                if (line.Newline.Length > 0)
                {
                    builder.Append(newline);
                }
            }

            return builder.ToString();
        }

        private static string PreserveOriginalTrailingLineBreak(
            string replacementText,
            IReadOnlyList<TextLine> fileLines,
            BlockMatch match)
        {
            if (replacementText.Length == 0)
            {
                return replacementText;
            }

            if (match.EndLineExclusive <= 0 || match.EndLineExclusive > fileLines.Count)
            {
                return replacementText;
            }

            var originalTrailingLineBreak = fileLines[match.EndLineExclusive - 1].Newline;
            if (originalTrailingLineBreak.Length == 0 || EndsWithLineBreak(replacementText))
            {
                return replacementText;
            }

            return replacementText + originalTrailingLineBreak;
        }

        private static bool EndsWithLineBreak(string text)
        {
            return text.EndsWith('\n') || text.EndsWith('\r');
        }

        private static string DetectDominantNewline(string text)
        {
            var crlf = 0;
            var lf = 0;
            var cr = 0;

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        crlf++;
                        index++;
                    }
                    else
                    {
                        cr++;
                    }
                }
                else if (text[index] == '\n')
                {
                    lf++;
                }
            }

            if (crlf >= lf && crlf >= cr && crlf > 0)
            {
                return "\r\n";
            }

            if (lf >= cr && lf > 0)
            {
                return "\n";
            }

            if (cr > 0)
            {
                return "\r";
            }

            return Environment.NewLine;
        }

        private static string TrimSingleBoundaryLineBreak(string text)
        {
            var result = text;
            if (result.StartsWith("\r\n", StringComparison.Ordinal))
            {
                result = result[2..];
            }
            else if (result.StartsWith('\n') || result.StartsWith('\r'))
            {
                result = result[1..];
            }

            if (result.EndsWith("\r\n", StringComparison.Ordinal))
            {
                result = result[..^2];
            }
            else if (result.EndsWith('\n') || result.EndsWith('\r'))
            {
                result = result[..^1];
            }

            return result;
        }

        private static EditBlock ParseEditBlock(int blockIndex, string blockText)
        {
            var split = SplitLines(blockText);
            var lineTexts = split.LineTexts;
            var anchoredAtBeginningOfFile = lineTexts.Count > 0 &&
                string.Equals(lineTexts[0], BeginningOfFileMarker, StringComparison.Ordinal);
            var anchoredAtEndOfFile = lineTexts.Count > 0 &&
                string.Equals(lineTexts[^1], EndOfFileMarker, StringComparison.Ordinal);

            ValidateBoundaryMarkers(blockIndex, lineTexts, anchoredAtBeginningOfFile, anchoredAtEndOfFile);

            var startLineIndex = anchoredAtBeginningOfFile ? 1 : 0;
            var endLineExclusive = split.Lines.Count - (anchoredAtEndOfFile ? 1 : 0);
            var finalText = BuildBlockFinalText(split.Lines, startLineIndex, endLineExclusive);

            return new EditBlock(blockIndex, finalText, anchoredAtBeginningOfFile, anchoredAtEndOfFile);
        }

        private static void ValidateBoundaryMarkers(
            int blockIndex,
            IReadOnlyList<string> lineTexts,
            bool anchoredAtBeginningOfFile,
            bool anchoredAtEndOfFile)
        {
            for (var index = 0; index < lineTexts.Count; index++)
            {
                var lineText = lineTexts[index];
                if (string.Equals(lineText, BeginningOfFileMarker, StringComparison.Ordinal) &&
                    (!anchoredAtBeginningOfFile || index != 0))
                {
                    throw new InvalidOperationException(
                        $"Edit block {blockIndex} uses {BeginningOfFileMarker} in an invalid position. {BeginningOfFileMarker} is only allowed as the first line inside a block.");
                }

                if (string.Equals(lineText, EndOfFileMarker, StringComparison.Ordinal) &&
                    (!anchoredAtEndOfFile || index != lineTexts.Count - 1))
                {
                    throw new InvalidOperationException(
                        $"Edit block {blockIndex} uses {EndOfFileMarker} in an invalid position. {EndOfFileMarker} is only allowed as the last line inside a block.");
                }
            }
        }

        private static string BuildBlockFinalText(IReadOnlyList<TextLine> lines, int startLineIndex, int endLineExclusive)
        {
            if (startLineIndex >= endLineExclusive)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (var index = startLineIndex; index < endLineExclusive; index++)
            {
                builder.Append(lines[index].Text);
                if (index + 1 < endLineExclusive)
                {
                    builder.Append(lines[index].Newline);
                }
            }

            return builder.ToString();
        }

        private static void ValidateReplacementLineCount(EditBlock block, int replacementLineCount)
        {
            if (block.AnchoredAtBeginningOfFile && block.AnchoredAtEndOfFile)
            {
                return;
            }

            if (block.AnchoredAtBeginningOfFile || block.AnchoredAtEndOfFile)
            {
                if (replacementLineCount == 0)
                {
                    throw new InvalidOperationException(
                        $"Edit block {block.Index} uses a file-boundary marker and must still include at least one real line of final text or unchanged anchor text.");
                }

                return;
            }

            if (replacementLineCount < 2)
            {
                throw new InvalidOperationException(
                    $"Edit block {block.Index} must contain at least two lines: an unchanged top anchor and an unchanged bottom anchor. To edit the first or last line, use {BeginningOfFileMarker} or {EndOfFileMarker} as the file-boundary anchor.");
            }
        }

        private static bool StartsWithElement(string text, string elementName)
        {
            var trimmed = text.TrimStart();
            var prefix = $"<{elementName}";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return trimmed.Length == prefix.Length || char.IsWhiteSpace(trimmed[prefix.Length]) || trimmed[prefix.Length] is '>' or '/';
        }

        private static string ExtractElementText(XElement element)
        {
            var builder = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XCData cdata:
                        builder.Append(cdata.Value);
                        break;
                    case XText text:
                        builder.Append(text.Value);
                        break;
                    default:
                        builder.Append(node.ToString(SaveOptions.DisableFormatting));
                        break;
                }
            }

            return builder.ToString();
        }

        private static string UnwrapOuterCData(string text)
        {
            var current = text.Trim();
            while (TryUnwrapOuterCData(current, out var innerText))
            {
                current = innerText.Trim();
            }

            return current;
        }

        private static bool TryUnwrapOuterCData(string text, out string innerText)
        {
            const string cdataPrefix = "<![CDATA[";
            const string cdataSuffix = "]]>";

            if (text.StartsWith(cdataPrefix, StringComparison.Ordinal) &&
                text.EndsWith(cdataSuffix, StringComparison.Ordinal) &&
                text.Length >= cdataPrefix.Length + cdataSuffix.Length)
            {
                innerText = text[cdataPrefix.Length..^cdataSuffix.Length];
                return true;
            }

            innerText = string.Empty;
            return false;
        }

        private static bool IsWhitespaceOnly(string text, int startIndex, int endIndex)
        {
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildUnmatchedSentinelMessage(string editText)
        {
            if (ContainsNonStandaloneExistingCodeLine(editText))
            {
                return "EditText contains [Existing Code] on a non-sentinel line. [Existing Code] must appear alone on its own line. Put [BOF], [EOF], or any real code on the next/previous line instead. Example: [Existing Code]\\n[BOF]\\n...\\n[Existing Code]. If you only need one block, you may also omit [Existing Code] entirely and let the whole EditText body act as the block.";
            }

            return "EditText contains unmatched edit-block sentinels. The standard multi-block form is [Existing Code] ... [Existing Code]. The outermost first block may also start with [BOF], and the outermost last block may also end with [EOF]. If you only need one block, you may omit [Existing Code] entirely and use the whole EditText body as a single shorthand block.";
        }

        private static string BuildOutsideBlockTextMessage(string editText)
        {
            if (ContainsLiteralCDataMarker(editText))
            {
                return "Only whitespace is allowed outside [Existing Code] edit blocks. Literal <![CDATA[ or ]]> markers were found in EditText. CDATA should wrap the outer XML parameter only; do not leave the CDATA markers themselves inside the edit body.";
            }

            if (ContainsNonStandaloneExistingCodeLine(editText))
            {
                return "Only whitespace is allowed outside [Existing Code] edit blocks. [Existing Code] must be alone on its own line; do not combine it with [BOF], [EOF], or real code on the same line.";
            }

            return "Only whitespace is allowed outside edit blocks. Put all source/final text inside complete blocks; the standard multi-block form is [Existing Code] ... [Existing Code], and the outermost first/last block may also use [BOF] ... [Existing Code] or [Existing Code] ... [EOF]. If you only need one block, you may omit [Existing Code] entirely and let the whole EditText body be that block.";
        }

        private static string BuildFallbackBlockFailureMessage(string editText, string reason)
        {
            if (ContainsNonStandaloneExistingCodeLine(editText))
            {
                return $"{reason} [Existing Code] was found, but not as a standalone sentinel line. Put [Existing Code] on its own line, or omit it entirely and use the whole EditText body as one shorthand block.";
            }

            if (ContainsLiteralCDataMarker(editText))
            {
                return $"{reason} Literal <![CDATA[ or ]]> markers were found in EditText. CDATA should wrap the outer XML parameter only; do not keep those markers inside the edit body.";
            }

            return $"{reason} No complete edit block was found, so the whole EditText body was treated as one shorthand block. For multiple distant edits, wrap each block in matched sentinels: usually [Existing Code] ... [Existing Code], with [BOF] ... [Existing Code] and [Existing Code] ... [EOF] also accepted for the outermost first/last block. For a full-file fallback rewrite, use [BOF] as the first line and [EOF] as the last line.";
        }

        private static bool ContainsNonStandaloneExistingCodeLine(string text)
        {
            foreach (var line in SplitLines(text).LineTexts)
            {
                if (line.IndexOf("[Existing Code]", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !IsExistingCodeSentinelLine(line))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsExistingCodeSentinelLine(string line)
        {
            return string.Equals(line.Trim(), "[Existing Code]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsLiteralCDataMarker(string text)
        {
            return text.Contains("<![CDATA[", StringComparison.Ordinal) ||
                text.Contains("]]>", StringComparison.Ordinal);
        }

        private static string BuildSuccessContent(
            WriteTargetPath targetPath,
            EditFileAdvancedToolSettings settings,
            EncodingDecision encodingDecision,
            long bytesBefore,
            long bytesAfter,
            int charactersBefore,
            int charactersAfter,
            IReadOnlyList<AppliedEditBlock> blockResults)
        {
            var builder = new StringBuilder(768);
            AppendCommonContent(builder, targetPath, settings, encodingDecision);
            builder.AppendLine($"BlocksApplied: {blockResults.Count}");
            AppendBlockSummaries(builder, blockResults);
            builder.AppendLine($"CharactersBefore: {charactersBefore}");
            builder.AppendLine($"CharactersAfter: {charactersAfter}");
            builder.AppendLine($"BytesBefore: {bytesBefore}");
            builder.AppendLine($"BytesAfter: {bytesAfter}");
            return builder.ToString().TrimEnd();
        }

        private static string BuildNoChangeContent(
            WriteTargetPath targetPath,
            EditFileAdvancedToolSettings settings,
            EncodingDecision encodingDecision,
            long bytesBefore,
            int charactersBefore,
            IReadOnlyList<AppliedEditBlock> blockResults)
        {
            var builder = new StringBuilder(768);
            AppendCommonContent(builder, targetPath, settings, encodingDecision);
            builder.AppendLine("All edit blocks were located, but the requested final text is identical. File was not changed.");
            builder.AppendLine($"BlocksChecked: {blockResults.Count}");
            AppendBlockSummaries(builder, blockResults);
            builder.AppendLine($"Characters: {charactersBefore}");
            builder.AppendLine($"Bytes: {bytesBefore}");
            return builder.ToString().TrimEnd();
        }

        private static void AppendCommonContent(
            StringBuilder builder,
            WriteTargetPath targetPath,
            EditFileAdvancedToolSettings settings,
            EncodingDecision encodingDecision)
        {
            builder.AppendLine($"Path: {targetPath.ResolvedPath}");
            builder.AppendLine($"PermissionScope: {settings.PermissionScope}");
            if (!string.IsNullOrWhiteSpace(targetPath.LateralNodeName))
            {
                builder.AppendLine($"LateralFSNode: {targetPath.LateralNodeName}");
                builder.AppendLine($"LateralFSRelativePath: {targetPath.LateralRelativePath}");
                builder.AppendLine($"UsedLateralFSShortcut: {targetPath.UsedLateralShortcut}");
            }

            builder.AppendLine($"RequestedEncoding: {encodingDecision.RequestedEncodingDisplayName}");
            builder.AppendLine($"Encoding: {encodingDecision.EffectiveEncodingName}");
            if (encodingDecision.IsAutoDetected)
            {
                builder.AppendLine($"DetectedEncoding: {encodingDecision.DetectedEncodingName}");
                builder.AppendLine($"DetectedEncodingReason: {encodingDecision.DetectionReason}");
            }
        }

        private static void AppendBlockSummaries(StringBuilder builder, IReadOnlyList<AppliedEditBlock> blockResults)
        {
            foreach (var result in blockResults)
            {
                var summary = result.Summary;
                builder.AppendLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Block {0}: lines {1}-{2}, anchors {3}+{4}, replaced {5} lines with {6} lines, changed={7}",
                        summary.BlockIndex,
                        summary.StartLine,
                        summary.EndLine,
                        summary.PrefixAnchorLines,
                        summary.SuffixAnchorLines,
                        summary.ReplacedLineCount,
                        summary.ReplacementLineCount,
                        summary.Changed));
            }
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            WriteTargetPath? targetPath,
            EditFileAdvancedToolSettings settings,
            EncodingDecision? encodingDecision,
            long? bytesBefore,
            int? charactersBefore,
            long? bytesAfter,
            int? charactersAfter,
            int blocksApplied,
            bool didWrite,
            IReadOnlyList<AppliedEditBlockSummary> blockSummaries)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["resolvedPath"] = targetPath?.ResolvedPath,
                ["permissionScope"] = settings.PermissionScope.ToString(),
                ["lateralNodeName"] = targetPath?.LateralNodeName,
                ["lateralNodeId"] = targetPath?.LateralNodeId,
                ["lateralNodeVirtualRootPath"] = targetPath?.LateralNodeVirtualRootPath,
                ["lateralRelativePath"] = targetPath?.LateralRelativePath,
                ["usedLateralShortcut"] = targetPath?.UsedLateralShortcut,
                ["requestedEncoding"] = encodingDecision?.RequestedEncodingName,
                ["requestedEncodingWasDefault"] = encodingDecision?.RequestedEncodingWasDefault,
                ["encoding"] = encodingDecision?.EffectiveEncodingName,
                ["encodingAutoDetected"] = encodingDecision?.IsAutoDetected,
                ["detectedEncoding"] = encodingDecision?.DetectedEncodingName,
                ["detectedEncodingReason"] = encodingDecision?.DetectionReason,
                ["bytesBefore"] = bytesBefore,
                ["charactersBefore"] = charactersBefore,
                ["bytesAfter"] = bytesAfter,
                ["charactersAfter"] = charactersAfter,
                ["blocksApplied"] = blocksApplied,
                ["didWrite"] = didWrite,
                ["blockSummaries"] = blockSummaries.Select(summary => summary.ToString()).ToArray()
            };
        }

        private static string? GetExplicitEncodingName(SkyweaverToolArguments arguments)
        {
            return arguments.RawArguments.TryGetValue("Encoding", out var rawEncoding) &&
                !string.IsNullOrWhiteSpace(rawEncoding)
                ? rawEncoding.Trim()
                : null;
        }

        private static EncodingDecision ResolveEncodingDecision(byte[] fileBytes, string? explicitEncodingName)
        {
            var requestedEncodingWasDefault = string.IsNullOrWhiteSpace(explicitEncodingName);
            var requestedEncodingName = requestedEncodingWasDefault
                ? "utf-8"
                : explicitEncodingName!.Trim();

            if (ShouldAutoDetectForUtf8Request(requestedEncodingName))
            {
                if (TryDetectUnicodeEncoding(fileBytes, out var detectedEncoding, out var detectedEncodingName, out var detectionReason, out var preamble))
                {
                    return new EncodingDecision(
                        detectedEncoding,
                        detectedEncodingName,
                        requestedEncodingName,
                        requestedEncodingWasDefault,
                        IsAutoDetected: true,
                        detectedEncodingName,
                        detectionReason,
                        preamble);
                }

                if (!TryDecodeStrict(fileBytes, 0, fileBytes.Length, s_utf8WithoutBomStrict, out _))
                {
                    throw new InvalidOperationException(
                        "The file is not valid UTF-8 and no UTF BOM/UTF-16/UTF-32 pattern was detected. Provide the correct Encoding parameter to avoid corrupting the file.");
                }

                return new EncodingDecision(
                    s_utf8WithoutBomStrict,
                    s_utf8WithoutBomStrict.WebName,
                    requestedEncodingName,
                    requestedEncodingWasDefault,
                    IsAutoDetected: false,
                    null,
                    null,
                    Array.Empty<byte>());
            }

            var resolvedEncoding = ResolveStrictEncoding(requestedEncodingName);
            return new EncodingDecision(
                resolvedEncoding,
                resolvedEncoding.WebName,
                requestedEncodingName,
                requestedEncodingWasDefault,
                IsAutoDetected: false,
                null,
                null,
                ResolveMatchingPreamble(fileBytes, resolvedEncoding));
        }

        private static bool ShouldAutoDetectForUtf8Request(string requestedEncodingName)
        {
            return string.Equals(
                ResolveStrictEncoding(requestedEncodingName).WebName,
                s_utf8WithoutBomStrict.WebName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static Encoding ResolveStrictEncoding(string encodingName)
        {
            var normalized = string.IsNullOrWhiteSpace(encodingName)
                ? "utf-8"
                : encodingName.Trim();
            var lower = normalized.ToLowerInvariant();

            return lower switch
            {
                "utf8" or "utf-8" => s_utf8WithoutBomStrict,
                "unicode" or "utf-16" or "utf-16le" or "utf-16-le" => s_utf16LittleEndianStrict,
                "unicodefffe" or "utf-16be" or "utf-16-be" => s_utf16BigEndianStrict,
                "utf-32" or "utf-32le" or "utf-32-le" => s_utf32LittleEndianStrict,
                "utf-32be" or "utf-32-be" => s_utf32BigEndianStrict,
                _ => Encoding.GetEncoding(
                    ToolFileSystemHelper.ResolveEncoding(normalized).CodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback)
            };
        }

        private static bool TryDetectUnicodeEncoding(
            byte[] fileBytes,
            out Encoding encoding,
            out string encodingName,
            out string detectionReason,
            out byte[] preamble)
        {
            encoding = s_utf8WithoutBomStrict;
            encodingName = s_utf8WithoutBomStrict.WebName;
            detectionReason = string.Empty;
            preamble = Array.Empty<byte>();

            if (fileBytes.Length == 0)
            {
                return false;
            }

            if (HasPrefix(fileBytes, s_utf32BigEndianPreamble))
            {
                encoding = s_utf32BigEndianStrict;
                encodingName = "utf-32-be";
                detectionReason = "byte-order mark 00 00 FE FF";
                preamble = s_utf32BigEndianPreamble;
                return true;
            }

            if (HasPrefix(fileBytes, s_utf32LittleEndianPreamble))
            {
                encoding = s_utf32LittleEndianStrict;
                encodingName = "utf-32-le";
                detectionReason = "byte-order mark FF FE 00 00";
                preamble = s_utf32LittleEndianPreamble;
                return true;
            }

            if (HasPrefix(fileBytes, s_utf8Preamble))
            {
                encoding = s_utf8WithoutBomStrict;
                encodingName = "utf-8";
                detectionReason = "byte-order mark EF BB BF";
                preamble = s_utf8Preamble;
                return true;
            }

            if (HasPrefix(fileBytes, s_utf16LittleEndianPreamble))
            {
                encoding = s_utf16LittleEndianStrict;
                encodingName = "utf-16-le";
                detectionReason = "byte-order mark FF FE";
                preamble = s_utf16LittleEndianPreamble;
                return true;
            }

            if (HasPrefix(fileBytes, s_utf16BigEndianPreamble))
            {
                encoding = s_utf16BigEndianStrict;
                encodingName = "utf-16-be";
                detectionReason = "byte-order mark FE FF";
                preamble = s_utf16BigEndianPreamble;
                return true;
            }

            if (TryDetectUtf32WithoutBom(fileBytes, out encoding, out encodingName, out detectionReason))
            {
                return true;
            }

            if (TryDetectUtf16WithoutBom(fileBytes, out encoding, out encodingName, out detectionReason))
            {
                return true;
            }

            return false;
        }

        private static bool TryDetectUtf32WithoutBom(
            byte[] fileBytes,
            out Encoding encoding,
            out string encodingName,
            out string detectionReason)
        {
            encoding = s_utf8WithoutBomStrict;
            encodingName = s_utf8WithoutBomStrict.WebName;
            detectionReason = string.Empty;

            var sampleLength = Math.Min(fileBytes.Length - (fileBytes.Length % 4), 512);
            var quartetCount = sampleLength / 4;
            if (quartetCount < 4)
            {
                return false;
            }

            var likelyLittleEndian = 0;
            var likelyBigEndian = 0;
            for (var index = 0; index < sampleLength; index += 4)
            {
                if (fileBytes[index] != 0 &&
                    fileBytes[index + 1] == 0 &&
                    fileBytes[index + 2] == 0 &&
                    fileBytes[index + 3] == 0)
                {
                    likelyLittleEndian++;
                }

                if (fileBytes[index] == 0 &&
                    fileBytes[index + 1] == 0 &&
                    fileBytes[index + 2] == 0 &&
                    fileBytes[index + 3] != 0)
                {
                    likelyBigEndian++;
                }
            }

            if (likelyLittleEndian >= quartetCount * 0.6 && likelyBigEndian == 0)
            {
                encoding = s_utf32LittleEndianStrict;
                encodingName = "utf-32-le";
                detectionReason = "strong UTF-32 little-endian zero-byte pattern without BOM";
                return true;
            }

            if (likelyBigEndian >= quartetCount * 0.6 && likelyLittleEndian == 0)
            {
                encoding = s_utf32BigEndianStrict;
                encodingName = "utf-32-be";
                detectionReason = "strong UTF-32 big-endian zero-byte pattern without BOM";
                return true;
            }

            return false;
        }

        private static bool TryDetectUtf16WithoutBom(
            byte[] fileBytes,
            out Encoding encoding,
            out string encodingName,
            out string detectionReason)
        {
            encoding = s_utf8WithoutBomStrict;
            encodingName = s_utf8WithoutBomStrict.WebName;
            detectionReason = string.Empty;

            var sampleLength = Math.Min(fileBytes.Length - (fileBytes.Length % 2), 512);
            var pairCount = sampleLength / 2;
            if (pairCount < 8)
            {
                return false;
            }

            var likelyLittleEndian = 0;
            var likelyBigEndian = 0;
            for (var index = 0; index < sampleLength; index += 2)
            {
                var first = fileBytes[index];
                var second = fileBytes[index + 1];

                if (first != 0 && second == 0)
                {
                    likelyLittleEndian++;
                }
                else if (first == 0 && second != 0)
                {
                    likelyBigEndian++;
                }
            }

            if (likelyLittleEndian >= pairCount * 0.6 && likelyBigEndian <= pairCount * 0.1)
            {
                encoding = s_utf16LittleEndianStrict;
                encodingName = "utf-16-le";
                detectionReason = "strong UTF-16 little-endian zero-byte pattern without BOM";
                return true;
            }

            if (likelyBigEndian >= pairCount * 0.6 && likelyLittleEndian <= pairCount * 0.1)
            {
                encoding = s_utf16BigEndianStrict;
                encodingName = "utf-16-be";
                detectionReason = "strong UTF-16 big-endian zero-byte pattern without BOM";
                return true;
            }

            return false;
        }

        private static byte[] ResolveMatchingPreamble(byte[] fileBytes, Encoding encoding)
        {
            if (encoding.CodePage == s_utf32BigEndianStrict.CodePage && HasPrefix(fileBytes, s_utf32BigEndianPreamble))
            {
                return s_utf32BigEndianPreamble;
            }

            if (encoding.CodePage == s_utf32LittleEndianStrict.CodePage && HasPrefix(fileBytes, s_utf32LittleEndianPreamble))
            {
                return s_utf32LittleEndianPreamble;
            }

            if (encoding.CodePage == s_utf8WithoutBomStrict.CodePage && HasPrefix(fileBytes, s_utf8Preamble))
            {
                return s_utf8Preamble;
            }

            if (encoding.CodePage == s_utf16LittleEndianStrict.CodePage && HasPrefix(fileBytes, s_utf16LittleEndianPreamble))
            {
                return s_utf16LittleEndianPreamble;
            }

            if (encoding.CodePage == s_utf16BigEndianStrict.CodePage && HasPrefix(fileBytes, s_utf16BigEndianPreamble))
            {
                return s_utf16BigEndianPreamble;
            }

            return Array.Empty<byte>();
        }

        private static string DecodeContent(byte[] fileBytes, EncodingDecision encodingDecision)
        {
            var offset = encodingDecision.Preamble.Length;
            return encodingDecision.Encoding.GetString(fileBytes, offset, fileBytes.Length - offset);
        }

        private static bool TryDecodeStrict(
            byte[] fileBytes,
            int offset,
            int count,
            Encoding encoding,
            out string content)
        {
            try
            {
                content = encoding.GetString(fileBytes, offset, count);
                return true;
            }
            catch (DecoderFallbackException)
            {
                content = string.Empty;
                return false;
            }
        }

        private static byte[] EncodeContent(string content, EncodingDecision encodingDecision)
        {
            var encodedContent = encodingDecision.Encoding.GetBytes(content);
            if (encodingDecision.Preamble.Length == 0)
            {
                return encodedContent;
            }

            var bytes = new byte[encodingDecision.Preamble.Length + encodedContent.Length];
            Buffer.BlockCopy(encodingDecision.Preamble, 0, bytes, 0, encodingDecision.Preamble.Length);
            Buffer.BlockCopy(encodedContent, 0, bytes, encodingDecision.Preamble.Length, encodedContent.Length);
            return bytes;
        }

        private static bool HasPrefix(byte[] fileBytes, byte[] prefix)
        {
            if (fileBytes.Length < prefix.Length)
            {
                return false;
            }

            for (var index = 0; index < prefix.Length; index++)
            {
                if (fileBytes[index] != prefix[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsExpectedException(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or DecoderFallbackException
                or EncoderFallbackException;
        }

        private sealed record WriteTargetPath(
            string ResolvedPath,
            string? LateralNodeName,
            string? LateralNodeId,
            string? LateralNodeVirtualRootPath,
            string? LateralRelativePath,
            bool UsedLateralShortcut);

        private sealed record EncodingDecision(
            Encoding Encoding,
            string EffectiveEncodingName,
            string RequestedEncodingName,
            bool RequestedEncodingWasDefault,
            bool IsAutoDetected,
            string? DetectedEncodingName,
            string? DetectionReason,
            byte[] Preamble)
        {
            public string RequestedEncodingDisplayName => RequestedEncodingWasDefault
                ? $"{RequestedEncodingName} (default)"
                : RequestedEncodingName;
        }

        private sealed record EditBlock(
            int Index,
            string FinalText,
            bool AnchoredAtBeginningOfFile,
            bool AnchoredAtEndOfFile);

        private sealed record TextLine(string Text, string Newline, int StartOffset);

        private sealed record SplitLineResult(IReadOnlyList<TextLine> Lines, IReadOnlyList<string> LineTexts);

        private sealed record BlockMatch(
            int StartLineIndex,
            int EndLineExclusive,
            int PrefixLineCount,
            int SuffixLineCount);

        private sealed record AppliedEditBlock(string UpdatedContent, AppliedEditBlockSummary Summary);

        private sealed record AppliedEditBlockSummary(
            int BlockIndex,
            int StartLine,
            int EndLine,
            int PrefixAnchorLines,
            int SuffixAnchorLines,
            int ReplacedLineCount,
            int ReplacementLineCount,
            bool Changed);
    }

    internal enum EditFileAdvancedPermissionScope
    {
        LateralFileSystemOnly,
        FullAccess
    }

    internal sealed class EditFileAdvancedToolSettings
    {
        private const string RootElementName = "EditFileAdvancedSettings";

        public EditFileAdvancedPermissionScope PermissionScope { get; set; } =
            EditFileAdvancedPermissionScope.LateralFileSystemOnly;

        public XElement ToXElement()
        {
            return new XElement(
                RootElementName,
                new XElement("PermissionScope", PermissionScope.ToString()));
        }

        public static EditFileAdvancedToolSettings FromConfiguration(SkyweaverToolConfigurationState? configuration)
        {
            var payload = configuration?.GetPayload();
            if (payload == null)
            {
                return new EditFileAdvancedToolSettings();
            }

            var root = string.Equals(payload.Name.LocalName, RootElementName, StringComparison.OrdinalIgnoreCase)
                ? payload
                : payload.Element(RootElementName);

            if (root == null)
            {
                return new EditFileAdvancedToolSettings();
            }

            return new EditFileAdvancedToolSettings
            {
                PermissionScope = ParsePermissionScope((string?)root.Element("PermissionScope"))
            };
        }

        public static EditFileAdvancedPermissionScope ParsePermissionScope(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return EditFileAdvancedPermissionScope.LateralFileSystemOnly;
            }

            if (string.Equals(normalized, "FullAccess", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "FullAuthorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Full", StringComparison.OrdinalIgnoreCase))
            {
                return EditFileAdvancedPermissionScope.FullAccess;
            }

            return EditFileAdvancedPermissionScope.LateralFileSystemOnly;
        }
    }

    internal sealed class EditFileAdvancedPermissionOption
    {
        public EditFileAdvancedPermissionOption(
            EditFileAdvancedPermissionScope scope,
            string displayName,
            string description)
        {
            Scope = scope;
            DisplayName = displayName;
            Description = description;
        }

        public EditFileAdvancedPermissionScope Scope { get; }

        public string DisplayName { get; }

        public string Description { get; }
    }

    internal sealed class EditFileAdvancedToolConfigurationViewModel : ObservableObject
    {
        private readonly Action _notifyConfigurationChanged;
        private EditFileAdvancedPermissionOption? _selectedPermission;

        public EditFileAdvancedToolConfigurationViewModel(
            EditFileAdvancedToolSettings settings,
            Action notifyConfigurationChanged)
        {
            _notifyConfigurationChanged = notifyConfigurationChanged ?? throw new ArgumentNullException(nameof(notifyConfigurationChanged));
            PermissionOptions = new ObservableCollection<EditFileAdvancedPermissionOption>
            {
                new(
                    EditFileAdvancedPermissionScope.LateralFileSystemOnly,
                    "LateralFS only",
                    "The model can write only inside LateralFS virtual folders. LateralFS\\NodeName\\... shortcuts are supported and checked."),
                new(
                    EditFileAdvancedPermissionScope.FullAccess,
                    "Full access",
                    "The model can write any file path that the Skyweaver process account can access. LateralFS shortcuts still work.")
            };

            _selectedPermission = PermissionOptions.FirstOrDefault(option => option.Scope == settings.PermissionScope)
                ?? PermissionOptions[0];
        }

        public ObservableCollection<EditFileAdvancedPermissionOption> PermissionOptions { get; }

        public EditFileAdvancedPermissionOption? SelectedPermission
        {
            get => _selectedPermission;
            set
            {
                if (SetProperty(ref _selectedPermission, value))
                {
                    OnPropertyChanged(nameof(PermissionDescription));
                    OnPropertyChanged(nameof(PreviewText));
                    _notifyConfigurationChanged();
                }
            }
        }

        public string PermissionDescription => SelectedPermission?.Description ?? string.Empty;

        public string PreviewText
        {
            get
            {
                var scope = SelectedPermission?.Scope ?? EditFileAdvancedPermissionScope.LateralFileSystemOnly;
                return scope == EditFileAdvancedPermissionScope.FullAccess
                    ? "Current permission: FullAccess. Normal absolute/relative paths and LateralFS shortcuts are accepted."
                    : "Current permission: LateralFileSystemOnly. Use LateralFS\\NodeName\\relative\\file.ext or an actual path under a LateralFS virtual root.";
            }
        }

        public EditFileAdvancedToolSettings ToSettings()
        {
            return new EditFileAdvancedToolSettings
            {
                PermissionScope = SelectedPermission?.Scope ?? EditFileAdvancedPermissionScope.LateralFileSystemOnly
            };
        }
    }

    internal sealed class EditFileAdvancedToolConfigurationPresenter : SkyweaverToolConfigurationPresenter
    {
        private readonly EditFileAdvancedToolConfigurationViewModel _viewModel;
        private readonly FrameworkElement _view;

        public EditFileAdvancedToolConfigurationPresenter(SkyweaverToolConfigurationEditorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var settings = EditFileAdvancedToolSettings.FromConfiguration(context.InitialConfiguration);
            _viewModel = new EditFileAdvancedToolConfigurationViewModel(settings, RaiseConfigurationChanged);
            _view = CreateView(_viewModel);
        }

        public override FrameworkElement View => _view;

        public override bool TryCaptureConfiguration(out XElement? configuration, out string? errorMessage)
        {
            try
            {
                configuration = _viewModel.ToSettings().ToXElement();
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                configuration = null;
                errorMessage = $"EditFile_Advanced configuration is invalid: {ex.Message}";
                return false;
            }
        }

        private static FrameworkElement CreateView(EditFileAdvancedToolConfigurationViewModel viewModel)
        {
            var panel = new StackPanel
            {
                DataContext = viewModel
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Permission",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var comboBox = new ComboBox
            {
                MinWidth = 220,
                DisplayMemberPath = nameof(EditFileAdvancedPermissionOption.DisplayName),
                Margin = new Thickness(0, 0, 0, 10)
            };
            comboBox.SetBinding(
                ItemsControl.ItemsSourceProperty,
                new Binding(nameof(EditFileAdvancedToolConfigurationViewModel.PermissionOptions)));
            comboBox.SetBinding(
                ComboBox.SelectedItemProperty,
                new Binding(nameof(EditFileAdvancedToolConfigurationViewModel.SelectedPermission))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
            panel.Children.Add(comboBox);

            var description = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.LightCyan,
                Margin = new Thickness(0, 0, 0, 10)
            };
            description.SetBinding(
                TextBlock.TextProperty,
                new Binding(nameof(EditFileAdvancedToolConfigurationViewModel.PermissionDescription)));
            panel.Children.Add(description);

            var preview = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                Opacity = 0.72
            };
            preview.SetBinding(
                TextBlock.TextProperty,
                new Binding(nameof(EditFileAdvancedToolConfigurationViewModel.PreviewText)));
            panel.Children.Add(preview);

            return panel;
        }
    }
}
