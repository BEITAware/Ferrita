using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Ferrita.Services.Directories;

namespace Ferrita.Services.ChatSession
{
    public sealed class PromptHistoryAttachmentInfo
    {
        public string? DisplayName { get; set; }
        public string? ResourcePath { get; set; }
        public string? MediaType { get; set; }
        public string? PreservedContentXml { get; set; }
    }

    public sealed class PromptHistoryService
    {
        private static readonly object _syncRoot = new();
        private static PromptHistoryService? _instance;

        public static PromptHistoryService Instance
        {
            get
            {
                lock (_syncRoot)
                {
                    return _instance ??= new PromptHistoryService();
                }
            }
        }

        private PromptHistoryService()
        {
        }

        private static string HistoryPath =>
            Path.Combine(FerritaDirectoryRuntime.Instance.ChatSessionsDirectoryPath, "PromptHistory.xml");

        private static XDocument ReadHistory(string path)
        {
            if (File.Exists(path))
            {
                var history = XDocument.Load(path);
                if (history.Root?.Name != "PromptHistory")
                    throw new InvalidDataException($"Unexpected prompt history format: {path}");
                return history;
            }

            // Older versions used the same directory/file convention as a chat named PromptHistory.
            var legacyPath = Path.Combine(Path.GetDirectoryName(path)!, "PromptHistory", "PromptHistory.xml");
            if (File.Exists(legacyPath))
            {
                try
                {
                    var legacy = XDocument.Load(legacyPath);
                    if (legacy.Root?.Name == "PromptHistory")
                        return legacy;
                    if (legacy.Root?.Name == "ChatSession")
                        return new XDocument(new XElement("PromptHistory", legacy.Root.Elements("PromptEntry")));
                }
                catch (System.Xml.XmlException ex)
                {
                    // Keep the damaged legacy file available for recovery and start a fresh history.
                    System.Diagnostics.Trace.TraceWarning($"Cannot read legacy prompt history '{legacyPath}': {ex.Message}");
                }
            }

            return new XDocument(new XElement("PromptHistory"));
        }

        public void AddHistoryEntry(string text, string sessionType, IEnumerable<PromptHistoryAttachmentInfo>? attachments = null)
        {
            lock (_syncRoot)
            {
                try
                {
                    var filePath = HistoryPath;
                    var doc = ReadHistory(filePath);

                    var entry = new XElement("PromptEntry",
                        new XAttribute("Timestamp", DateTime.UtcNow.ToString("o")),
                        new XAttribute("SessionType", sessionType),
                        new XElement("Text", text ?? string.Empty)
                    );

                    if (attachments != null)
                    {
                        foreach (var att in attachments)
                        {
                            string preservedXml;
                            if (!string.IsNullOrWhiteSpace(att.PreservedContentXml))
                            {
                                preservedXml = att.PreservedContentXml;
                            }
                            else
                            {
                                preservedXml = FerritaPreservedTextContentXml.Build(
                                    string.Empty,
                                    att.DisplayName,
                                    att.ResourcePath,
                                    att.MediaType
                                );
                            }

                            try
                            {
                                var preservedElement = XElement.Parse(preservedXml);
                                entry.Add(preservedElement);
                            }
                            catch
                            {
                                // Ignore parse error
                            }
                        }
                    }

                    doc.Root!.Add(entry);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        doc.Save(tempPath);
                        File.Move(tempPath, filePath, overwrite: true);
                    }
                    finally
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save prompt history: {ex.Message}");
                }
            }
        }

        public List<string> GetHistoryTexts()
        {
            lock (_syncRoot)
            {
                var list = new List<string>();
                try
                {
                    var doc = ReadHistory(HistoryPath);
                    var entries = doc.Root?.Elements("PromptEntry") ?? Enumerable.Empty<XElement>();
                    foreach (var entry in entries)
                    {
                        var textElement = entry.Element("Text");
                        if (textElement != null)
                        {
                            list.Add(textElement.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read prompt history: {ex.Message}");
                }
                return list;
            }
        }
    }
}
