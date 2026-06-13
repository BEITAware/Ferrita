using System.Xml;
using System.Xml.Linq;

namespace Skyweaver.Services.ChatSession
{
    internal sealed class PreservedTextContent
    {
        public string Text { get; init; } = string.Empty;

        public string? Name { get; init; }

        public string? Path { get; init; }

        public string? MediaType { get; init; }

        public string DisplayName => FirstNonEmpty(Name, System.IO.Path.GetFileName(Path), "Text");

        private static string FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        }
    }

    internal static class PreservedTextContentXml
    {
        public const string RootElementName = "PreservedContent";
        public const string TextElementName = "Text";
        public const string MetadataKindKey = "PreservedContentKind";
        public const string TextKindValue = "Text";

        public static string Build(
            string text,
            string? name = null,
            string? path = null,
            string? mediaType = null)
        {
            var textElement = new XElement(
                TextElementName,
                OptionalAttribute("Name", name),
                OptionalAttribute("Path", path),
                OptionalAttribute("MediaType", mediaType),
                SanitizeXmlText(text));

            return new XElement(RootElementName, textElement).ToString(SaveOptions.DisableFormatting);
        }

        public static bool TryParse(string? xml, out PreservedTextContent content)
        {
            content = new PreservedTextContent();
            if (string.IsNullOrWhiteSpace(xml))
            {
                return false;
            }

            try
            {
                var root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
                if (!string.Equals(root.Name.LocalName, RootElementName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var textElement = root.Elements()
                    .FirstOrDefault(element => string.Equals(
                        element.Name.LocalName,
                        TextElementName,
                        StringComparison.OrdinalIgnoreCase));
                if (textElement == null)
                {
                    return false;
                }

                content = new PreservedTextContent
                {
                    Text = textElement.Value ?? string.Empty,
                    Name = GetAttributeValue(textElement, "Name") ?? GetAttributeValue(textElement, "DisplayName"),
                    Path = GetAttributeValue(textElement, "Path") ?? GetAttributeValue(textElement, "SourcePath"),
                    MediaType = GetAttributeValue(textElement, "MediaType") ?? GetAttributeValue(textElement, "MimeType")
                };
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public static bool IsTextContent(string? xml)
        {
            return TryParse(xml, out _);
        }

        private static XAttribute? OptionalAttribute(string name, string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new XAttribute(name, value.Trim());
        }

        private static string? GetAttributeValue(XElement element, string attributeName)
        {
            return element.Attributes()
                .FirstOrDefault(attribute => string.Equals(
                    attribute.Name.LocalName,
                    attributeName,
                    StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
        }

        private static string SanitizeXmlText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                builder.Append(XmlConvert.IsXmlChar(ch) ? ch : '\uFFFD');
            }

            return builder.ToString();
        }
    }
}
