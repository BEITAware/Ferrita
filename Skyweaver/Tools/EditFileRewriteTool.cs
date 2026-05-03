using System.Collections.ObjectModel;
using System.IO;
using System.Text;
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
    public sealed class EditFileRewriteTool :
        ISkyweaverTool,
        ISkyweaverToolConfigurationProvider,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "EditFile_Rewrite";

        private static readonly SkyweaverToolDefinition s_definition = BuildDefinition(new EditFileRewriteToolSettings());

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
            return BuildDefinition(EditFileRewriteToolSettings.FromConfiguration(configuration));
        }

        public SkyweaverToolConfigurationPresenter? CreateConfigurationPresenter(SkyweaverToolConfigurationEditorContext context)
        {
            return new EditFileRewriteToolConfigurationPresenter(context);
        }

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            return BuildDescription(EditFileRewriteToolSettings.FromConfiguration(context.ConfigurationState));
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("File", "FilePath", "Waiting for file path..."),
                    new ToolInvocationCardFieldDefinition("Content", "Content", "Waiting for file content..."),
                    new ToolInvocationCardFieldDefinition("Encoding", "Encoding", "Default utf-8 with safe auto-detection")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var settings = EditFileRewriteToolSettings.FromConfiguration(context.CurrentToolConfiguration);
            var requestedPath = arguments.GetString("FilePath") ?? string.Empty;
            var newContent = arguments.GetString("Content") ?? string.Empty;
            WriteTargetPath? targetPath = null;

            try
            {


                targetPath = ResolveWriteTargetPath(requestedPath, context.WorkspacePath, settings.PermissionScope);

                if (Directory.Exists(targetPath.ResolvedPath))
                {
                    return SkyweaverToolResult.Failure(
                        $"Path points to a directory, not a file: {targetPath.ResolvedPath}",
                        BuildData(targetPath, settings, null, null, null, null, null, didWrite: false));
                }

                if (!File.Exists(targetPath.ResolvedPath))
                {
                    return SkyweaverToolResult.Failure(
                        $"File not found: {targetPath.ResolvedPath}",
                        BuildData(targetPath, settings, null, null, null, null, null, didWrite: false));
                }

                var originalBytes = await File.ReadAllBytesAsync(targetPath.ResolvedPath, cancellationToken).ConfigureAwait(false);
                var encodingDecision = ResolveEncodingDecision(originalBytes, GetExplicitEncodingName(arguments));
                var originalContent = DecodeContent(originalBytes, encodingDecision);
                var updatedContent = newContent;
                if (string.Equals(updatedContent, originalContent, StringComparison.Ordinal))
                {
                    return SkyweaverToolResult.Success(
                        "Replacement text is identical to current content. File was not changed.",
                        BuildData(targetPath, settings, encodingDecision, originalBytes.LongLength, originalContent.Length, 0, originalBytes.LongLength, didWrite: false));
                }

                var updatedBytes = EncodeContent(updatedContent, encodingDecision);
                await File.WriteAllBytesAsync(targetPath.ResolvedPath, updatedBytes, cancellationToken).ConfigureAwait(false);

                return SkyweaverToolResult.Success(
                    BuildSuccessContent(targetPath, settings, encodingDecision, originalBytes.LongLength, updatedBytes.LongLength),
                    BuildData(targetPath, settings, encodingDecision, originalBytes.LongLength, originalContent.Length, 0, updatedBytes.LongLength, didWrite: true));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to rewrite file content: {ex.Message}",
                    BuildData(targetPath, settings, null, null, null, null, null, didWrite: false));
            }
        }

        private static SkyweaverToolDefinition BuildDefinition(EditFileRewriteToolSettings settings)
        {
            return new SkyweaverToolDefinition(
                ToolName,
                BuildDescription(settings),
                "Script",
                [
                    new SkyweaverToolParameterDefinition(
                        "FilePath",
                        "Full file path and name. You may also use LateralFS\\NodeName\\relative\\file.ext; the host resolves that shortcut to the node virtual folder and blocks path traversal outside the node.",
                        SkyweaverToolParameterType.String,
                        isRequired: true),
                    new SkyweaverToolParameterDefinition(
                        "Content",
                        "The new content of the file. It will completely overwrite the existing content.",
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

        private static string BuildDescription(EditFileRewriteToolSettings settings)
        {
            var permissionText = settings.PermissionScope == EditFileRewritePermissionScope.FullAccess
                ? "Permission: FullAccess, so the tool may write any file path that the process account can access."
                : "Permission: LateralFileSystemOnly, so the tool may write only inside LateralFS virtual folders.";

            return "Overwrite an entire file with new content with encoding safety. " +
                "The FilePath can be a normal path, or a LateralFS shortcut in the form LateralFS\\NodeName\\relative\\file.ext. " +
                "The shortcut is resolved to that node's actual virtual folder before writing, and '..' traversal outside the node is rejected. " +
                permissionText;
        }

        private static WriteTargetPath ResolveWriteTargetPath(
            string requestedPath,
            string? workspacePath,
            EditFileRewritePermissionScope permissionScope)
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

            if (permissionScope == EditFileRewritePermissionScope.LateralFileSystemOnly &&
                lateralResolution == null)
            {
                throw new InvalidOperationException(
                    "This tool is configured as LateralFileSystemOnly. FilePath must resolve inside a LateralFS virtual folder. Prefer LateralFS\\NodeName\\relative\\file.ext.");
            }

            return new WriteTargetPath(
                resolvedPath,
                lateralResolution?.NodeName,
                lateralResolution?.NodeId,
                lateralResolution?.NodeVirtualRootPath,
                lateralResolution?.RelativePath,
                lateralResolution?.UsedShortcut ?? false);
        }

        private static string BuildSuccessContent(
            WriteTargetPath targetPath,
            EditFileRewriteToolSettings settings,
            EncodingDecision encodingDecision,
            long bytesBefore,
            long bytesAfter)
        {
            var builder = new StringBuilder(512);
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


            builder.AppendLine($"BytesBefore: {bytesBefore}");
            builder.AppendLine($"BytesAfter: {bytesAfter}");
            return builder.ToString().TrimEnd();
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            WriteTargetPath? targetPath,
            EditFileRewriteToolSettings settings,
            EncodingDecision? encodingDecision,
            long? bytesBefore,
            int? charactersBefore,
            int? occurrencesReplaced,
            long? bytesAfter,
            bool didWrite)
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
                ["occurrencesReplaced"] = null,
                ["bytesAfter"] = bytesAfter,
                ["didWrite"] = didWrite
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
    }

    internal enum EditFileRewritePermissionScope
    {
        LateralFileSystemOnly,
        FullAccess
    }

    internal sealed class EditFileRewriteToolSettings
    {
        private const string RootElementName = "EditFileRewriteSettings";

        public EditFileRewritePermissionScope PermissionScope { get; set; } =
            EditFileRewritePermissionScope.LateralFileSystemOnly;

        public XElement ToXElement()
        {
            return new XElement(
                RootElementName,
                new XElement("PermissionScope", PermissionScope.ToString()));
        }

        public static EditFileRewriteToolSettings FromConfiguration(SkyweaverToolConfigurationState? configuration)
        {
            var payload = configuration?.GetPayload();
            if (payload == null)
            {
                return new EditFileRewriteToolSettings();
            }

            var root = string.Equals(payload.Name.LocalName, RootElementName, StringComparison.OrdinalIgnoreCase)
                ? payload
                : payload.Element(RootElementName);

            if (root == null)
            {
                return new EditFileRewriteToolSettings();
            }

            return new EditFileRewriteToolSettings
            {
                PermissionScope = ParsePermissionScope((string?)root.Element("PermissionScope"))
            };
        }

        public static EditFileRewritePermissionScope ParsePermissionScope(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return EditFileRewritePermissionScope.LateralFileSystemOnly;
            }

            if (string.Equals(normalized, "FullAccess", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "FullAuthorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Full", StringComparison.OrdinalIgnoreCase))
            {
                return EditFileRewritePermissionScope.FullAccess;
            }

            return EditFileRewritePermissionScope.LateralFileSystemOnly;
        }
    }

    internal sealed class EditFileRewritePermissionOption
    {
        public EditFileRewritePermissionOption(
            EditFileRewritePermissionScope scope,
            string displayName,
            string description)
        {
            Scope = scope;
            DisplayName = displayName;
            Description = description;
        }

        public EditFileRewritePermissionScope Scope { get; }

        public string DisplayName { get; }

        public string Description { get; }
    }

    internal sealed class EditFileRewriteToolConfigurationViewModel : ObservableObject
    {
        private readonly Action _notifyConfigurationChanged;
        private EditFileRewritePermissionOption? _selectedPermission;

        public EditFileRewriteToolConfigurationViewModel(
            EditFileRewriteToolSettings settings,
            Action notifyConfigurationChanged)
        {
            _notifyConfigurationChanged = notifyConfigurationChanged ?? throw new ArgumentNullException(nameof(notifyConfigurationChanged));
            PermissionOptions = new ObservableCollection<EditFileRewritePermissionOption>
            {
                new(
                    EditFileRewritePermissionScope.LateralFileSystemOnly,
                    "LateralFS only",
                    "The model can write only inside LateralFS virtual folders. LateralFS\\NodeName\\... shortcuts are supported and checked."),
                new(
                    EditFileRewritePermissionScope.FullAccess,
                    "Full access",
                    "The model can write any file path that the Skyweaver process account can access. LateralFS shortcuts still work.")
            };

            _selectedPermission = PermissionOptions.FirstOrDefault(option => option.Scope == settings.PermissionScope)
                ?? PermissionOptions[0];
        }

        public ObservableCollection<EditFileRewritePermissionOption> PermissionOptions { get; }

        public EditFileRewritePermissionOption? SelectedPermission
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
                var scope = SelectedPermission?.Scope ?? EditFileRewritePermissionScope.LateralFileSystemOnly;
                return scope == EditFileRewritePermissionScope.FullAccess
                    ? "Current permission: FullAccess. Normal absolute/relative paths and LateralFS shortcuts are accepted."
                    : "Current permission: LateralFileSystemOnly. Use LateralFS\\NodeName\\relative\\file.ext or an actual path under a LateralFS virtual root.";
            }
        }

        public EditFileRewriteToolSettings ToSettings()
        {
            return new EditFileRewriteToolSettings
            {
                PermissionScope = SelectedPermission?.Scope ?? EditFileRewritePermissionScope.LateralFileSystemOnly
            };
        }
    }

    internal sealed class EditFileRewriteToolConfigurationPresenter : SkyweaverToolConfigurationPresenter
    {
        private readonly EditFileRewriteToolConfigurationViewModel _viewModel;
        private readonly FrameworkElement _view;

        public EditFileRewriteToolConfigurationPresenter(SkyweaverToolConfigurationEditorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var settings = EditFileRewriteToolSettings.FromConfiguration(context.InitialConfiguration);
            _viewModel = new EditFileRewriteToolConfigurationViewModel(settings, RaiseConfigurationChanged);
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
                errorMessage = $"EditFile_Rewrite configuration is invalid: {ex.Message}";
                return false;
            }
        }

        private static FrameworkElement CreateView(EditFileRewriteToolConfigurationViewModel viewModel)
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
                DisplayMemberPath = nameof(EditFileRewritePermissionOption.DisplayName),
                Margin = new Thickness(0, 0, 0, 10)
            };
            comboBox.SetBinding(
                ItemsControl.ItemsSourceProperty,
                new Binding(nameof(EditFileRewriteToolConfigurationViewModel.PermissionOptions)));
            comboBox.SetBinding(
                ComboBox.SelectedItemProperty,
                new Binding(nameof(EditFileRewriteToolConfigurationViewModel.SelectedPermission))
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
                new Binding(nameof(EditFileRewriteToolConfigurationViewModel.PermissionDescription)));
            panel.Children.Add(description);

            var preview = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                Opacity = 0.72
            };
            preview.SetBinding(
                TextBlock.TextProperty,
                new Binding(nameof(EditFileRewriteToolConfigurationViewModel.PreviewText)));
            panel.Children.Add(preview);

            return panel;
        }
    }
}
