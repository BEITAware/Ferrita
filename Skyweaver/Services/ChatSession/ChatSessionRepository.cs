using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Skyweaver.Models.ChatSession;
using Skyweaver.Services.Directories;

namespace Skyweaver.Services.ChatSession
{
    public sealed class ChatSessionRepository
    {
        private const string DefaultIconPath = "pack://application:,,,/Resources/NewNodeGraphAlt.png";

        private readonly ChatSessionFlowBindingService _flowBindingService;

        public string RootFolderPath => SkyweaverDirectoryRuntime.Instance.ChatSessionsDirectoryPath;

        public ChatSessionRepository()
            : this(new ChatSessionFlowBindingService())
        {
        }

        public ChatSessionRepository(ChatSessionFlowBindingService flowBindingService)
        {
            _flowBindingService = flowBindingService ?? throw new ArgumentNullException(nameof(flowBindingService));
        }

        public IReadOnlyList<ChatSessionModel> LoadAll()
        {
            EnsureRootFolder();

            if (!Directory.Exists(RootFolderPath))
            {
                return Array.Empty<ChatSessionModel>();
            }

            var sessionDirectories = Directory
                .EnumerateDirectories(RootFolderPath)
                .OrderByDescending(Directory.GetLastWriteTimeUtc);

            var sessions = new List<ChatSessionModel>();
            foreach (var sessionDirectory in sessionDirectories)
            {
                var sessionName = Path.GetFileName(sessionDirectory);
                if (string.IsNullOrWhiteSpace(sessionName))
                {
                    continue;
                }

                var sessionFilePath = Path.Combine(sessionDirectory, $"{sessionName}.xml");
                if (!File.Exists(sessionFilePath))
                {
                    continue;
                }

                sessions.Add(LoadFromFile(sessionFilePath));
            }

            return sessions;
        }

        public ChatSessionModel? LoadBySessionId(string? sessionId)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            if (normalizedSessionId.Length == 0)
            {
                return null;
            }

            return LoadAll().FirstOrDefault(session =>
                string.Equals(
                    session.SessionId?.Trim(),
                    normalizedSessionId,
                    StringComparison.OrdinalIgnoreCase));
        }

        public ChatSessionModel Create(string sessionName, ChatSessionFlowBinding? flowBinding = null)
        {
            EnsureRootFolder();

            var validatedName = ValidateSessionName(sessionName);
            var sessionFolderPath = Path.Combine(RootFolderPath, validatedName);
            if (Directory.Exists(sessionFolderPath))
            {
                throw new InvalidOperationException($"会话“{validatedName}”已存在。");
            }

            var resourcesFolderPath = Path.Combine(sessionFolderPath, ChatSessionResourceLayout.ResourcesFolderName);
            Directory.CreateDirectory(sessionFolderPath);
            Directory.CreateDirectory(resourcesFolderPath);
            Directory.CreateDirectory(Path.Combine(resourcesFolderPath, ChatSessionResourceLayout.ToolCallsFolderName));

            var now = DateTime.UtcNow;
            var session = new ChatSessionModel
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Name = validatedName,
                IconPath = DefaultIconPath,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ContextSummary = "新建会话。",
                MetadataNote = "XML transcript schema 3",
                SessionFolderPath = sessionFolderPath,
                SessionFilePath = Path.Combine(sessionFolderPath, $"{validatedName}.xml"),
                ResourcesFolderPath = resourcesFolderPath
            };

            var resolvedBinding = _flowBindingService.ResolveBinding(flowBinding, ensureDefaultGraph: true);
            session.FlowBinding.GraphId = resolvedBinding.GraphId;
            session.FlowBinding.GraphName = resolvedBinding.GraphName;
            session.FlowBinding.FilePath = resolvedBinding.FilePath;

            Save(session);
            return session;
        }

        public void Save(ChatSessionModel session)
        {
            ArgumentNullException.ThrowIfNull(session);

            EnsureRootFolder();
            HydratePaths(session);
            Directory.CreateDirectory(session.SessionFolderPath);
            ChatSessionResourceLayout.EnsureResources(session);

            session.UpdatedAtUtc = DateTime.UtcNow;
            XDocument document;
            lock (session.Transcript.SyncRoot)
            {
                session.Transcript.SchemaVersion = 3;
                session.Transcript.RebuildIndex();
                document = CreateDocument(session);
            }

            SaveAtomically(document, session.SessionFilePath);
        }

        public void Delete(ChatSessionModel session)
        {
            ArgumentNullException.ThrowIfNull(session);
            HydratePaths(session);

            if (Directory.Exists(session.SessionFolderPath))
            {
                Directory.Delete(session.SessionFolderPath, recursive: true);
            }
        }

        private ChatSessionModel LoadFromFile(string sessionFilePath)
        {
            var document = XDocument.Load(sessionFilePath, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("ChatSession XML is missing a root element.");
            var metadataElement = root.Element("Metadata");

            var name = (string?)root.Attribute("Name") ?? Path.GetFileNameWithoutExtension(sessionFilePath);
            var session = new ChatSessionModel
            {
                SessionId = FirstNonEmpty(
                    (string?)root.Attribute("SessionId"),
                    (string?)root.Attribute("SessionID"),
                    Guid.NewGuid().ToString("N")),
                Name = name,
                IconPath = (string?)metadataElement?.Element("IconPath") ?? DefaultIconPath,
                CreatedAtUtc = ParseDateTime((string?)metadataElement?.Element("CreatedAtUtc")),
                UpdatedAtUtc = ParseDateTime((string?)metadataElement?.Element("UpdatedAtUtc")),
                MetadataNote = (string?)metadataElement?.Element("Note") ?? string.Empty,
                IsShellSession = ParseBool((string?)metadataElement?.Element("IsShellSession")) ||
                                 string.Equals(
                                     ((string?)metadataElement?.Element("SessionKind") ?? string.Empty).Trim(),
                                     "Shell",
                                     StringComparison.OrdinalIgnoreCase),
                IsScheduledTaskSession = ParseBool((string?)metadataElement?.Element("IsScheduledTaskSession")),
                SessionFilePath = sessionFilePath,
                SessionFolderPath = Path.GetDirectoryName(sessionFilePath) ?? string.Empty,
                ResourcesFolderPath = Path.Combine(
                    Path.GetDirectoryName(sessionFilePath) ?? string.Empty,
                    ChatSessionResourceLayout.ResourcesFolderName)
            };

            var boundFlowElement = metadataElement?.Element("BoundSessionFlow");
            if (boundFlowElement != null)
            {
                session.FlowBinding.GraphId = ((string?)boundFlowElement.Element("GraphId") ?? string.Empty).Trim();
                session.FlowBinding.GraphName = ((string?)boundFlowElement.Element("GraphName") ?? string.Empty).Trim();
                session.FlowBinding.FilePath = ((string?)boundFlowElement.Element("FilePath") ?? string.Empty).Trim();
            }

            LoadResourceManifest(session, root.Element("Resources"));
            LoadTranscript(session.Transcript, root.Element("Transcript"));
            session.ContextSummary = BuildContextSummary(session);
            ChatSessionResourceLayout.EnsureResources(session);
            return session;
        }

        private static XDocument CreateDocument(ChatSessionModel session)
        {
            return new XDocument(
                new XElement(
                    "ChatSession",
                    new XAttribute("SchemaVersion", "3"),
                    new XAttribute("SessionId", session.SessionId),
                    new XAttribute("Name", session.Name),
                    new XElement(
                        "Metadata",
                        new XElement("CreatedAtUtc", session.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                        new XElement("UpdatedAtUtc", session.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                        new XElement("IconPath", session.IconPath),
                        new XElement("Note", session.MetadataNote),
                        session.IsShellSession ? new XElement("IsShellSession", true) : null,
                        session.IsScheduledTaskSession ? new XElement("IsScheduledTaskSession", true) : null,
                        new XElement(
                            "BoundSessionFlow",
                            new XElement("GraphId", session.FlowBinding.GraphId),
                            new XElement("GraphName", session.FlowBinding.GraphName),
                            new XElement("FilePath", session.FlowBinding.FilePath))),
                    CreateResourcesElement(session.Resources),
                    CreateTranscriptElement(session.Transcript)));
        }

        private static XElement CreateResourcesElement(ChatSessionResourceManifest manifest)
        {
            return new XElement(
                "Resources",
                manifest.Resources.Select(resource =>
                    new XElement(
                        "Resource",
                        new XAttribute("Id", resource.Id),
                        new XAttribute("Kind", resource.Kind),
                        new XAttribute("Path", resource.Path),
                        OptionalAttribute("MediaType", resource.MediaType),
                        new XAttribute("CreatedAtUtc", resource.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                        OptionalAttribute("SizeBytes", resource.SizeBytes?.ToString(CultureInfo.InvariantCulture)),
                        OptionalAttribute("Hash", resource.Hash))));
        }

        private static XElement CreateTranscriptElement(ChatSessionTranscript transcript)
        {
            return new XElement(
                "Transcript",
                new XAttribute("TranscriptId", transcript.TranscriptId),
                new XAttribute("SchemaVersion", transcript.SchemaVersion),
                new XAttribute("Revision", transcript.Revision),
                new XElement("Turns", transcript.Turns.Select(CreateTurnElement)),
                new XElement("Entries", transcript.Entries.Select(CreateEntryElement)));
        }

        private static XElement CreateTurnElement(ChatSessionTurnRecord turn)
        {
            return new XElement(
                "Turn",
                new XAttribute("Id", turn.TurnId),
                new XAttribute("Number", turn.TurnNumber),
                new XAttribute("Status", turn.Status),
                new XAttribute("StartedAtUtc", turn.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                OptionalAttribute("CompletedAtUtc", turn.CompletedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                OptionalAttribute("UserEntryId", turn.UserEntryId),
                OptionalAttribute("FinalEntryId", turn.FinalEntryId),
                CreateMetadataElement(turn.Metadata));
        }

        private static XElement CreateEntryElement(ChatSessionTranscriptEntry entry)
        {
            return new XElement(
                "Entry",
                new XAttribute("Id", entry.EntryId),
                OptionalAttribute("TurnId", entry.TurnId),
                OptionalAttribute("ParentEntryId", entry.ParentEntryId),
                new XAttribute("Kind", entry.Kind),
                new XAttribute("Role", entry.Role),
                new XAttribute("TimestampUtc", entry.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                OptionalAttribute("NodeId", entry.NodeId),
                OptionalAttribute("NodeTitle", entry.NodeTitle),
                OptionalAttribute("AgentId", entry.AgentId),
                OptionalAttribute("AgentName", entry.AgentName),
                OptionalAttribute("IterationNumber", entry.IterationNumber?.ToString(CultureInfo.InvariantCulture)),
                OptionalAttribute("ToolCallId", entry.ToolCallId),
                OptionalAttribute("ToolName", entry.ToolName),
                OptionalAttribute("ToolCallIndex", entry.ToolCallIndex?.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Visibility", entry.Visibility),
                new XAttribute("LlmPolicy", entry.LlmPolicy),
                new XAttribute("HandoffPolicy", entry.HandoffPolicy),
                new XAttribute("Status", entry.Status),
                new XAttribute("Revision", entry.Revision),
                new XElement("Blocks", entry.Blocks.Select(CreateBlockElement)),
                CreateMetadataElement(entry.Metadata));
        }

        private static XElement CreateBlockElement(ChatSessionTranscriptBlock block)
        {
            var blockElement = new XElement(
                "Block",
                new XAttribute("Id", block.BlockId),
                new XAttribute("Kind", block.Kind),
                OptionalAttribute("Title", block.Title),
                OptionalAttribute("Language", block.Language),
                OptionalAttribute("MediaType", block.MediaType),
                OptionalAttribute("ResourceId", block.ResourceId),
                OptionalAttribute("ResourcePath", block.ResourcePath),
                new XAttribute("Revision", block.Revision));

            blockElement.Add(CreateContentNode(block.Kind, block.Content));
            blockElement.Add(CreateMetadataElement(block.Metadata));
            return blockElement;
        }

        private static XNode CreateContentNode(ChatSessionTranscriptBlockKind kind, string? content)
        {
            var value = content ?? string.Empty;
            var prefersCData = kind is ChatSessionTranscriptBlockKind.StructuredXml
                or ChatSessionTranscriptBlockKind.ToolInvocationXml
                or ChatSessionTranscriptBlockKind.ToolOutputXml;
            if (prefersCData && !value.Contains("]]>", StringComparison.Ordinal))
            {
                return new XCData(value);
            }

            return new XText(value);
        }

        private static XElement CreateMetadataElement(IReadOnlyDictionary<string, string> metadata)
        {
            return new XElement(
                "Metadata",
                metadata
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new XElement(
                        "Item",
                        new XAttribute("Key", item.Key),
                        item.Value ?? string.Empty)));
        }

        private static void LoadResourceManifest(ChatSessionModel session, XElement? resourcesElement)
        {
            session.Resources.Resources.Clear();
            if (resourcesElement == null)
            {
                return;
            }

            foreach (var resourceElement in resourcesElement.Elements("Resource"))
            {
                var id = ((string?)resourceElement.Attribute("Id") ?? Guid.NewGuid().ToString("N")).Trim();
                var path = ((string?)resourceElement.Attribute("Path") ?? string.Empty).Trim();
                if (id.Length == 0 || path.Length == 0)
                {
                    continue;
                }

                session.Resources.Resources.Add(new ChatSessionResourceManifestEntry
                {
                    Id = id,
                    Kind = ((string?)resourceElement.Attribute("Kind") ?? string.Empty).Trim(),
                    Path = path,
                    MediaType = NullIfWhiteSpace((string?)resourceElement.Attribute("MediaType")),
                    CreatedAtUtc = ParseDateTime((string?)resourceElement.Attribute("CreatedAtUtc")),
                    SizeBytes = ParseNullableLong((string?)resourceElement.Attribute("SizeBytes")),
                    Hash = NullIfWhiteSpace((string?)resourceElement.Attribute("Hash"))
                });
            }
        }

        private static void LoadTranscript(ChatSessionTranscript transcript, XElement? transcriptElement)
        {
            transcript.Turns.Clear();
            transcript.Entries.Clear();

            if (transcriptElement == null)
            {
                transcript.SchemaVersion = 3;
                transcript.RebuildIndex();
                return;
            }

            transcript.TranscriptId = FirstNonEmpty(
                (string?)transcriptElement.Attribute("TranscriptId"),
                Guid.NewGuid().ToString("N"));
            transcript.SchemaVersion = ParseInt((string?)transcriptElement.Attribute("SchemaVersion"), 3);
            transcript.SetRevision(ParseLong((string?)transcriptElement.Attribute("Revision"), 0));

            foreach (var turnElement in transcriptElement.Element("Turns")?.Elements("Turn") ?? Enumerable.Empty<XElement>())
            {
                transcript.Turns.Add(LoadTurnElement(turnElement));
            }

            foreach (var entryElement in transcriptElement.Element("Entries")?.Elements("Entry") ?? Enumerable.Empty<XElement>())
            {
                transcript.Entries.Add(LoadEntryElement(entryElement));
            }

            transcript.RebuildIndex();
        }

        private static ChatSessionTurnRecord LoadTurnElement(XElement turnElement)
        {
            var turn = new ChatSessionTurnRecord
            {
                TurnId = FirstNonEmpty((string?)turnElement.Attribute("Id"), Guid.NewGuid().ToString("N")),
                TurnNumber = ParseInt((string?)turnElement.Attribute("Number"), 0),
                StartedAtUtc = ParseDateTime((string?)turnElement.Attribute("StartedAtUtc")),
                CompletedAtUtc = ParseNullableDateTime((string?)turnElement.Attribute("CompletedAtUtc")),
                Status = ParseEnum((string?)turnElement.Attribute("Status"), ChatSessionTurnStatus.Pending),
                UserEntryId = NullIfWhiteSpace((string?)turnElement.Attribute("UserEntryId")),
                FinalEntryId = NullIfWhiteSpace((string?)turnElement.Attribute("FinalEntryId"))
            };

            LoadMetadata(turn.Metadata, turnElement.Element("Metadata"));
            return turn;
        }

        private static ChatSessionTranscriptEntry LoadEntryElement(XElement entryElement)
        {
            var entry = new ChatSessionTranscriptEntry
            {
                EntryId = FirstNonEmpty((string?)entryElement.Attribute("Id"), Guid.NewGuid().ToString("N")),
                TurnId = ((string?)entryElement.Attribute("TurnId") ?? string.Empty).Trim(),
                ParentEntryId = NullIfWhiteSpace((string?)entryElement.Attribute("ParentEntryId")),
                Kind = ParseEnum((string?)entryElement.Attribute("Kind"), ChatSessionTranscriptEntryKind.UserMessage),
                Role = ParseEnum((string?)entryElement.Attribute("Role"), ChatSessionParticipantRole.User),
                TimestampUtc = ParseDateTime((string?)entryElement.Attribute("TimestampUtc")),
                NodeId = NullIfWhiteSpace((string?)entryElement.Attribute("NodeId")),
                NodeTitle = NullIfWhiteSpace((string?)entryElement.Attribute("NodeTitle")),
                AgentId = NullIfWhiteSpace((string?)entryElement.Attribute("AgentId")),
                AgentName = NullIfWhiteSpace((string?)entryElement.Attribute("AgentName")),
                IterationNumber = ParseNullableInt((string?)entryElement.Attribute("IterationNumber")),
                ToolCallId = NullIfWhiteSpace((string?)entryElement.Attribute("ToolCallId")),
                ToolName = NullIfWhiteSpace((string?)entryElement.Attribute("ToolName")),
                ToolCallIndex = ParseNullableInt((string?)entryElement.Attribute("ToolCallIndex")),
                Visibility = ParseEnum((string?)entryElement.Attribute("Visibility"), TranscriptVisibility.Visible),
                LlmPolicy = ParseEnum((string?)entryElement.Attribute("LlmPolicy"), TranscriptLlmPolicy.Include),
                HandoffPolicy = ParseEnum((string?)entryElement.Attribute("HandoffPolicy"), TranscriptHandoffPolicy.ExcludeByDefault),
                Status = ParseEnum((string?)entryElement.Attribute("Status"), ChatSessionEntryStatus.Completed)
            };
            entry.SetRevision(ParseLong((string?)entryElement.Attribute("Revision"), 0));

            foreach (var blockElement in entryElement.Element("Blocks")?.Elements("Block") ?? Enumerable.Empty<XElement>())
            {
                entry.Blocks.Add(LoadBlockElement(blockElement));
            }

            LoadMetadata(entry.Metadata, entryElement.Element("Metadata"));
            return entry;
        }

        private static ChatSessionTranscriptBlock LoadBlockElement(XElement blockElement)
        {
            var block = new ChatSessionTranscriptBlock
            {
                BlockId = FirstNonEmpty((string?)blockElement.Attribute("Id"), Guid.NewGuid().ToString("N")),
                Kind = ParseEnum((string?)blockElement.Attribute("Kind"), ChatSessionTranscriptBlockKind.Text),
                Content = blockElement.Nodes().OfType<XText>().FirstOrDefault()?.Value
                    ?? blockElement.Nodes().OfType<XCData>().FirstOrDefault()?.Value
                    ?? string.Empty,
                Title = NullIfWhiteSpace((string?)blockElement.Attribute("Title")),
                Language = NullIfWhiteSpace((string?)blockElement.Attribute("Language")),
                MediaType = NullIfWhiteSpace((string?)blockElement.Attribute("MediaType")),
                ResourceId = NullIfWhiteSpace((string?)blockElement.Attribute("ResourceId")),
                ResourcePath = NullIfWhiteSpace((string?)blockElement.Attribute("ResourcePath"))
            };
            block.SetRevision(ParseLong((string?)blockElement.Attribute("Revision"), 0));
            LoadMetadata(block.Metadata, blockElement.Element("Metadata"));
            return block;
        }

        private static void LoadMetadata(IDictionary<string, string> target, XElement? metadataElement)
        {
            target.Clear();
            if (metadataElement == null)
            {
                return;
            }

            foreach (var itemElement in metadataElement.Elements("Item"))
            {
                var key = ((string?)itemElement.Attribute("Key") ?? string.Empty).Trim();
                if (key.Length > 0)
                {
                    target[key] = itemElement.Value ?? string.Empty;
                }
            }
        }

        private static void SaveAtomically(XDocument document, string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{filePath}.tmp";
            document.Save(tempPath);

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }

        private void EnsureRootFolder()
        {
            Directory.CreateDirectory(RootFolderPath);
        }

        private void HydratePaths(ChatSessionModel session)
        {
            if (string.IsNullOrWhiteSpace(session.SessionFolderPath))
            {
                session.SessionFolderPath = Path.Combine(RootFolderPath, session.Name);
            }

            if (string.IsNullOrWhiteSpace(session.ResourcesFolderPath))
            {
                session.ResourcesFolderPath = Path.Combine(
                    session.SessionFolderPath,
                    ChatSessionResourceLayout.ResourcesFolderName);
            }

            if (string.IsNullOrWhiteSpace(session.SessionFilePath))
            {
                session.SessionFilePath = Path.Combine(session.SessionFolderPath, $"{session.Name}.xml");
            }
        }

        private static string ValidateSessionName(string sessionName)
        {
            var trimmedName = sessionName.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                throw new InvalidOperationException("会话名称不能为空。");
            }

            if (trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("会话名称包含无效的文件名字符。");
            }

            return trimmedName;
        }

        private static XAttribute? OptionalAttribute(string name, string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new XAttribute(name, value.Trim());
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        }

        private static DateTime ParseDateTime(string? value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? EnsureUtc(parsed)
                : DateTime.UtcNow;
        }

        private static DateTime? ParseNullableDateTime(string? value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? EnsureUtc(parsed)
                : null;
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static int? ParseNullableInt(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static long ParseLong(string? value, long fallback)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static long? ParseNullableLong(string? value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static bool ParseBool(string? value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (bool.TryParse(normalized, out var parsed))
            {
                return parsed;
            }

            return string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
            where TEnum : struct
        {
            return Enum.TryParse<TEnum>(value, true, out var parsed)
                ? parsed
                : fallback;
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string BuildContextSummary(ChatSessionModel session)
        {
            var visibleCount = session.Transcript.Entries.Count(entry => entry.Visibility == TranscriptVisibility.Visible);
            return visibleCount == 0
                ? "空会话。"
                : $"会话记录 {session.Transcript.Entries.Count} 条，其中 {visibleCount} 条可见。";
        }
    }
}
