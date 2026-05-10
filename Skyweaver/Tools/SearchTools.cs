using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class GrepSearchTool : ISkyweaverTool, ISkyweaverToolInvocationPresentationProvider, ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "GrepSearch";

        private const int MaximumMatches = 200;
        private const int MaximumFilesScanned = 10000;
        private const long MaximumFileBytes = 2 * 1024 * 1024;

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Searches text file contents with a .NET regular expression. Does not require grep to be installed. Directory may be absolute or relative to the current workspace.",
            "Script",
            [
                new SkyweaverToolParameterDefinition(
                    "Pattern",
                    "Regular expression pattern to search for in text file contents.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Directory",
                    "Directory to search. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Context",
                    "Context line count around each match. Default is 1, meaning one line before and one line after the match. Use +N for the match line plus N following lines, or -N for the match line plus N preceding lines.",
                    SkyweaverToolParameterType.String,
                    isRequired: false,
                    defaultValue: "1")
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["Investigate"]);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "Searches text file contents with a .NET regular expression and returns matching file paths plus context lines. It does not require grep to be installed. Pattern is a regex. Directory is required and may be absolute or relative to the current workspace. Context defaults to 1, which means one line before and one line after each match. Use +N for match line plus N following lines, or -N for match line plus N preceding lines. Results are capped to keep output manageable.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Pattern", "Pattern", "Waiting for search pattern..."),
                    new ToolInvocationCardFieldDefinition("Directory", "Directory", "Waiting for directory..."),
                    new ToolInvocationCardFieldDefinition("Context", "Context", "Default 1 line before and after")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pattern = arguments.GetString("Pattern") ?? string.Empty;
            var requestedDirectory = arguments.GetString("Directory") ?? string.Empty;
            var requestedContext = arguments.GetString("Context") ?? "1";
            string? resolvedDirectory = null;

            try
            {
                resolvedDirectory = ToolSearchSupport.ResolveSearchDirectory(requestedDirectory, context.WorkspacePath);
                if (!Directory.Exists(resolvedDirectory))
                {
                    return SkyweaverToolResult.Failure(
                        $"Directory not found: {resolvedDirectory}",
                        BuildData(resolvedDirectory, pattern, requestedContext, filesScanned: 0, filesSkipped: 0, matchCount: 0, truncated: false));
                }

                var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                var contextSpec = ParseContextSpec(requestedContext);
                var matches = new List<GrepMatch>();
                var filesScanned = 0;
                var filesSkipped = 0;
                var truncated = false;

                foreach (var filePath in ToolSearchSupport.EnumerateFiles(resolvedDirectory, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (filesScanned >= MaximumFilesScanned)
                    {
                        truncated = true;
                        break;
                    }

                    filesScanned++;

                    if (IsFileTooLarge(filePath) || await IsBinaryFileAsync(filePath, cancellationToken).ConfigureAwait(false))
                    {
                        filesSkipped++;
                        continue;
                    }

                    try
                    {
                        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
                        for (var index = 0; index < lines.Length; index++)
                        {
                            if (!regex.IsMatch(lines[index]))
                            {
                                continue;
                            }

                            matches.Add(new GrepMatch(
                                filePath,
                                ToolFileSystemHelper.TryGetWorkspaceRelativePath(context.WorkspacePath, filePath),
                                index + 1,
                                BuildContextSnippet(lines, index, contextSpec),
                                contextSpec));

                            if (matches.Count >= MaximumMatches)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException or NotSupportedException)
                    {
                        filesSkipped++;
                    }

                    if (matches.Count >= MaximumMatches)
                    {
                        break;
                    }
                }

                return SkyweaverToolResult.Success(
                    BuildContent(resolvedDirectory, pattern, requestedContext, filesScanned, filesSkipped, matches, truncated),
                    BuildData(resolvedDirectory, pattern, requestedContext, filesScanned, filesSkipped, matches.Count, truncated));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or RegexMatchTimeoutException or NotSupportedException)
            {
                return SkyweaverToolResult.Failure(
                    $"Failed to search file contents: {ex.Message}",
                    BuildData(resolvedDirectory, pattern, requestedContext, filesScanned: null, filesSkipped: null, matchCount: null, truncated: null));
            }
        }

        private static bool IsFileTooLarge(string filePath)
        {
            try
            {
                return new FileInfo(filePath).Length > MaximumFileBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        }

        private static async Task<bool> IsBinaryFileAsync(string filePath, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, useAsync: true);
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildContent(
            string resolvedDirectory,
            string pattern,
            string context,
            int filesScanned,
            int filesSkipped,
            IReadOnlyList<GrepMatch> matches,
            bool truncated)
        {
            var builder = new StringBuilder(4096);
            builder.AppendLine($"Directory: {resolvedDirectory}");
            builder.AppendLine($"Pattern: {pattern}");
            builder.AppendLine($"Context: {context}");
            builder.AppendLine($"FilesScanned: {filesScanned}");
            builder.AppendLine($"FilesSkipped: {filesSkipped}");
            builder.AppendLine($"Matches: {matches.Count}");
            builder.AppendLine($"Truncated: {truncated}");
            builder.AppendLine();

            if (matches.Count == 0)
            {
                builder.AppendLine("No matches found.");
                return builder.ToString().TrimEnd();
            }

            foreach (var match in matches)
            {
                var displayPath = string.IsNullOrWhiteSpace(match.WorkspaceRelativePath)
                    ? match.FilePath
                    : match.WorkspaceRelativePath;
                builder.AppendLine($"{displayPath}:{match.LineNumber}");
                builder.Append(match.LineText);
                builder.AppendLine();
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            string? resolvedDirectory,
            string pattern,
            string context,
            int? filesScanned,
            int? filesSkipped,
            int? matchCount,
            bool? truncated)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["resolvedDirectory"] = resolvedDirectory,
                ["pattern"] = pattern,
                ["context"] = context,
                ["filesScanned"] = filesScanned,
                ["filesSkipped"] = filesSkipped,
                ["matchCount"] = matchCount,
                ["truncated"] = truncated,
                ["maximumMatches"] = MaximumMatches,
                ["maximumFilesScanned"] = MaximumFilesScanned,
                ["maximumFileBytes"] = MaximumFileBytes
            };
        }

        private static string BuildContextSnippet(string[] lines, int matchIndex, ContextSpec contextSpec)
        {
            var startIndex = matchIndex;
            var endIndex = matchIndex;

            switch (contextSpec.Direction)
            {
                case ContextDirection.Both:
                    startIndex = Math.Max(0, matchIndex - contextSpec.Count);
                    endIndex = Math.Min(lines.Length - 1, matchIndex + contextSpec.Count);
                    break;

                case ContextDirection.Forward:
                    startIndex = matchIndex;
                    endIndex = Math.Min(lines.Length - 1, matchIndex + contextSpec.Count);
                    break;

                case ContextDirection.Backward:
                    startIndex = Math.Max(0, matchIndex - contextSpec.Count);
                    endIndex = matchIndex;
                    break;
            }

            var builder = new StringBuilder();
            for (var index = startIndex; index <= endIndex; index++)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                var linePrefix = index == matchIndex ? ">" : " ";
                builder.Append(linePrefix);
                builder.Append(index + 1);
                builder.Append(": ");
                builder.Append(ToolSearchSupport.TrimForOutput(lines[index]));
            }

            return builder.ToString();
        }

        private static ContextSpec ParseContextSpec(string rawContext)
        {
            var normalized = (rawContext ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException("Context cannot be empty.");
            }

            if (normalized.StartsWith('+') || normalized.StartsWith('-'))
            {
                var direction = normalized[0] == '+' ? ContextDirection.Forward : ContextDirection.Backward;
                if (!int.TryParse(normalized[1..], out var count) || count < 0)
                {
                    throw new InvalidOperationException("Context must be a non-negative integer, or a signed integer like +5 or -5.");
                }

                return new ContextSpec(direction, count, normalized);
            }

            if (!int.TryParse(normalized, out var symmetricCount) || symmetricCount < 0)
            {
                throw new InvalidOperationException("Context must be a non-negative integer, or a signed integer like +5 or -5.");
            }

            return new ContextSpec(ContextDirection.Both, symmetricCount, normalized);
        }

        private sealed record ContextSpec(ContextDirection Direction, int Count, string RawValue);

        private enum ContextDirection
        {
            Both,
            Forward,
            Backward
        }

        private sealed record GrepMatch(
            string FilePath,
            string? WorkspaceRelativePath,
            int LineNumber,
            string LineText,
            ContextSpec ContextSpec);
    }

    public sealed class GlobSearchTool : ISkyweaverTool, ISkyweaverToolInvocationPresentationProvider, ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "GlobSearch";

        private const int MaximumMatches = 500;
        private const int MaximumFilesScanned = 50000;

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Searches file names with a glob pattern. Directory may be absolute or relative to the current workspace. Only file names and relative paths are searched; file contents are not read.",
            "Script",
            [
                new SkyweaverToolParameterDefinition(
                    "Pattern",
                    "Glob pattern for file names or relative paths, for example *.cs, **/*.xaml, or *Tool*.cs.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Directory",
                    "Directory to search. Relative paths resolve against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true)
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["Investigate"]);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "Searches file names with a glob pattern and returns matching paths. It only searches file names and relative paths; it does not read file contents. Pattern supports *, ?, and ** path recursion. Directory is required and may be absolute or relative to the current workspace.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Pattern", "Pattern", "Waiting for glob pattern..."),
                    new ToolInvocationCardFieldDefinition("Directory", "Directory", "Waiting for directory...")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pattern = arguments.GetString("Pattern") ?? string.Empty;
            var requestedDirectory = arguments.GetString("Directory") ?? string.Empty;
            string? resolvedDirectory = null;

            try
            {
                resolvedDirectory = ToolSearchSupport.ResolveSearchDirectory(requestedDirectory, context.WorkspacePath);
                if (!Directory.Exists(resolvedDirectory))
                {
                    return Task.FromResult(SkyweaverToolResult.Failure(
                        $"Directory not found: {resolvedDirectory}",
                        BuildData(resolvedDirectory, pattern, filesScanned: 0, matchCount: 0, truncated: false)));
                }

                var regex = ToolSearchSupport.CreateGlobRegex(pattern);
                var matches = new List<string>();
                var filesScanned = 0;
                var truncated = false;

                foreach (var filePath in ToolSearchSupport.EnumerateFiles(resolvedDirectory, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (filesScanned >= MaximumFilesScanned)
                    {
                        truncated = true;
                        break;
                    }

                    filesScanned++;
                    var relativePath = ToolFileSystemHelper.NormalizePathForPrompt(Path.GetRelativePath(resolvedDirectory, filePath));
                    var fileName = Path.GetFileName(filePath);

                    if (!regex.IsMatch(relativePath) && !regex.IsMatch(fileName))
                    {
                        continue;
                    }

                    matches.Add(relativePath);
                    if (matches.Count >= MaximumMatches)
                    {
                        truncated = true;
                        break;
                    }
                }

                return Task.FromResult(SkyweaverToolResult.Success(
                    BuildContent(resolvedDirectory, pattern, filesScanned, matches, truncated),
                    BuildData(resolvedDirectory, pattern, filesScanned, matches.Count, truncated)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or RegexMatchTimeoutException or NotSupportedException)
            {
                return Task.FromResult(SkyweaverToolResult.Failure(
                    $"Failed to search file names: {ex.Message}",
                    BuildData(resolvedDirectory, pattern, filesScanned: null, matchCount: null, truncated: null)));
            }
        }

        private static string BuildContent(
            string resolvedDirectory,
            string pattern,
            int filesScanned,
            IReadOnlyList<string> matches,
            bool truncated)
        {
            var builder = new StringBuilder(4096);
            builder.AppendLine($"Directory: {resolvedDirectory}");
            builder.AppendLine($"Pattern: {pattern}");
            builder.AppendLine($"FilesScanned: {filesScanned}");
            builder.AppendLine($"Matches: {matches.Count}");
            builder.AppendLine($"Truncated: {truncated}");
            builder.AppendLine();

            if (matches.Count == 0)
            {
                builder.AppendLine("No files found.");
                return builder.ToString().TrimEnd();
            }

            foreach (var match in matches.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(match);
            }

            return builder.ToString().TrimEnd();
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            string? resolvedDirectory,
            string pattern,
            int? filesScanned,
            int? matchCount,
            bool? truncated)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["resolvedDirectory"] = resolvedDirectory,
                ["pattern"] = pattern,
                ["filesScanned"] = filesScanned,
                ["matchCount"] = matchCount,
                ["truncated"] = truncated,
                ["maximumMatches"] = MaximumMatches,
                ["maximumFilesScanned"] = MaximumFilesScanned
            };
        }
    }

    internal static class ToolSearchSupport
    {
        public static string ResolveSearchDirectory(string requestedDirectory, string? workspacePath)
        {
            return ToolFileSystemHelper.ResolvePath(requestedDirectory, workspacePath);
        }

        public static IEnumerable<string> EnumerateFiles(string rootDirectory, CancellationToken cancellationToken)
        {
            var pending = new Stack<string>();
            pending.Push(rootDirectory);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directory = pending.Pop();
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return file;
                }

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(directory).OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    continue;
                }

                foreach (var childDirectory in childDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push(childDirectory);
                }
            }
        }

        public static Regex CreateGlobRegex(string pattern)
        {
            var normalizedPattern = (pattern ?? string.Empty).Trim().Replace('\\', '/');
            if (normalizedPattern.Length == 0)
            {
                throw new InvalidOperationException("Pattern cannot be empty.");
            }

            var builder = new StringBuilder(normalizedPattern.Length * 2 + 2);
            builder.Append('^');

            for (var index = 0; index < normalizedPattern.Length; index++)
            {
                var character = normalizedPattern[index];
                if (character == '*')
                {
                    var isDoubleStar = index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '*';
                    if (isDoubleStar)
                    {
                        builder.Append(".*");
                        index++;
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    continue;
                }

                if (character == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                if (character == '/')
                {
                    builder.Append('/');
                    continue;
                }

                builder.Append(Regex.Escape(character.ToString()));
            }

            builder.Append('$');
            return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }

        public static string TrimForOutput(string value)
        {
            const int maximumLength = 500;

            if (value.Length <= maximumLength)
            {
                return value.TrimEnd();
            }

            return string.Concat(value.AsSpan(0, maximumLength), "...").TrimEnd();
        }

    }
}
