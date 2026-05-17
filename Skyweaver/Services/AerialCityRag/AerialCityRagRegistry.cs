using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Skyweaver.Services.AerialCityRag
{
    public sealed class AerialCityRagRegistry
    {
        public const string FileName = "AerialCity.xml";
        private static readonly char[] s_directoryTrimChars =
        [
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        ];

        public string GetRegistryFilePath(string aerialCityDirectoryPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(aerialCityDirectoryPath);
            return Path.Combine(Path.GetFullPath(aerialCityDirectoryPath), FileName);
        }

        public IReadOnlyList<AerialCityRagFolderMapping> Load(string aerialCityDirectoryPath)
        {
            var registryFilePath = GetRegistryFilePath(aerialCityDirectoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(registryFilePath)!);

            if (!File.Exists(registryFilePath))
            {
                Save(aerialCityDirectoryPath, []);
                return [];
            }

            var document = XDocument.Load(registryFilePath);
            var root = document.Root ?? throw new InvalidDataException("AerialCity.xml is missing its root element.");

            return root.Element("Folders")?.Elements("Folder")
                .Select(ParseMapping)
                .Where(mapping => mapping != null)
                .Cast<AerialCityRagFolderMapping>()
                .ToArray() ?? [];
        }

        public void Upsert(string aerialCityDirectoryPath, AerialCityRagFolderMapping mapping)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            var mappings = Load(aerialCityDirectoryPath)
                .Where(existing => !string.Equals(existing.TargetPath, mapping.TargetPath, StringComparison.OrdinalIgnoreCase))
                .Append(mapping)
                .OrderBy(item => item.TargetPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Save(aerialCityDirectoryPath, mappings);
        }

        public AerialCityRagFolderMapping? FindBestMappingForPath(string aerialCityDirectoryPath, string searchPath)
        {
            var normalizedSearchPath = NormalizePath(searchPath);

            return Load(aerialCityDirectoryPath)
                .Where(mapping => IsSubPathOrEqual(mapping.TargetPath, normalizedSearchPath))
                .OrderByDescending(mapping => NormalizePath(mapping.TargetPath).Length)
                .FirstOrDefault();
        }

        public static string CreateDatabaseFolderName(string targetDirectoryPath)
        {
            var fullPath = NormalizePath(targetDirectoryPath);
            var builder = new char[fullPath.Length];
            for (var index = 0; index < fullPath.Length; index++)
            {
                var current = fullPath[index];
                builder[index] = current switch
                {
                    ':' or '\\' or '/' => '_',
                    _ when Path.GetInvalidFileNameChars().Contains(current) => '_',
                    _ => current
                };
            }

            var compacted = Regex.Replace(new string(builder), "_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(compacted) ? "Root" : compacted;
        }

        public static string NormalizePath(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Path.GetFullPath(path.Trim()).TrimEnd(s_directoryTrimChars);
        }

        public static bool IsSubPathOrEqual(string rootPath, string candidatePath)
        {
            var normalizedRoot = NormalizePath(rootPath);
            var normalizedCandidate = NormalizePath(candidatePath);

            if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static AerialCityRagFolderMapping? ParseMapping(XElement element)
        {
            var targetPath = ((string?)element.Attribute("TargetPath") ?? string.Empty).Trim();
            var databasePath = ((string?)element.Attribute("DatabasePath") ?? string.Empty).Trim();
            var databaseFolderName = ((string?)element.Attribute("DatabaseFolderName") ?? string.Empty).Trim();

            if (targetPath.Length == 0 || databasePath.Length == 0 || databaseFolderName.Length == 0)
            {
                return null;
            }

            return new AerialCityRagFolderMapping
            {
                TargetPath = targetPath,
                DatabaseFolderName = databaseFolderName,
                DatabasePath = databasePath,
                EmbeddingModelKey = ((string?)element.Attribute("EmbeddingModelKey") ?? string.Empty).Trim(),
                EmbeddingModelDisplayName = ((string?)element.Attribute("EmbeddingModelDisplayName") ?? string.Empty).Trim(),
                EmbeddingModelId = ((string?)element.Attribute("EmbeddingModelId") ?? string.Empty).Trim(),
                EmbeddingInterfaceType = ((string?)element.Attribute("EmbeddingInterfaceType") ?? string.Empty).Trim(),
                EmbeddingDimensions = ParseInt((string?)element.Attribute("EmbeddingDimensions")),
                SupportsMultimodalEmbedding = ParseBool((string?)element.Attribute("SupportsMultimodalEmbedding")),
                InitializedAtUtc = ParseDate((string?)element.Attribute("InitializedAtUtc")),
                UpdatedAtUtc = ParseDate((string?)element.Attribute("UpdatedAtUtc"))
            };
        }

        private void Save(string aerialCityDirectoryPath, IReadOnlyList<AerialCityRagFolderMapping> mappings)
        {
            var registryFilePath = GetRegistryFilePath(aerialCityDirectoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(registryFilePath)!);

            var document = new XDocument(
                new XElement("AerialCity",
                    new XAttribute("SchemaVersion", 1),
                    new XElement("Folders",
                        mappings.Select(CreateMappingElement))));

            document.Save(registryFilePath);
        }

        private static XElement CreateMappingElement(AerialCityRagFolderMapping mapping)
        {
            var initializedAtUtc = mapping.InitializedAtUtc == default
                ? DateTimeOffset.UtcNow
                : mapping.InitializedAtUtc;
            var updatedAtUtc = mapping.UpdatedAtUtc == default
                ? initializedAtUtc
                : mapping.UpdatedAtUtc;

            return new XElement("Folder",
                new XAttribute("TargetPath", mapping.TargetPath),
                new XAttribute("DatabaseFolderName", mapping.DatabaseFolderName),
                new XAttribute("DatabasePath", mapping.DatabasePath),
                new XAttribute("EmbeddingModelKey", mapping.EmbeddingModelKey),
                new XAttribute("EmbeddingModelDisplayName", mapping.EmbeddingModelDisplayName),
                new XAttribute("EmbeddingModelId", mapping.EmbeddingModelId),
                new XAttribute("EmbeddingInterfaceType", mapping.EmbeddingInterfaceType),
                new XAttribute("EmbeddingDimensions", mapping.EmbeddingDimensions),
                new XAttribute("SupportsMultimodalEmbedding", mapping.SupportsMultimodalEmbedding),
                new XAttribute("InitializedAtUtc", initializedAtUtc.ToString("O")),
                new XAttribute("UpdatedAtUtc", updatedAtUtc.ToString("O")));
        }

        private static int ParseInt(string? value)
        {
            return int.TryParse(value, out var parsed) ? parsed : 0;
        }

        private static bool ParseBool(string? value)
        {
            return bool.TryParse(value, out var parsed) && parsed;
        }

        private static DateTimeOffset ParseDate(string? value)
        {
            return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
        }
    }

    public sealed class AerialCityRagFolderMapping
    {
        public required string TargetPath { get; init; }

        public required string DatabaseFolderName { get; init; }

        public required string DatabasePath { get; init; }

        public string EmbeddingModelKey { get; init; } = string.Empty;

        public string EmbeddingModelDisplayName { get; init; } = string.Empty;

        public string EmbeddingModelId { get; init; } = string.Empty;

        public string EmbeddingInterfaceType { get; init; } = string.Empty;

        public int EmbeddingDimensions { get; init; }

        public bool SupportsMultimodalEmbedding { get; init; }

        public DateTimeOffset InitializedAtUtc { get; init; }

        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
