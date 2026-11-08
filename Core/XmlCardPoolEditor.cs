using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UrukCardEditor.Core;

public static class XmlCardPoolEditor
{
    public static string ReplaceCardObjectInBundle(string bundleSource, string cardFileName, string standaloneCardSource)
    {
        var bundle = XDocument.Parse(bundleSource, LoadOptions.PreserveWhitespace);
        var card = XDocument.Parse(standaloneCardSource, LoadOptions.PreserveWhitespace).Root
            ?? throw new InvalidDataException("The standalone XML card has no root element.");
        if (!card.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The standalone XML source is not a CARD_V2 document.");

        var existing = bundle.Root?.Elements("CARD_V2").FirstOrDefault(candidate =>
            string.Equals((string?)candidate.Element("FILENAME")?.Attribute("text"), cardFileName,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Card '{cardFileName}' was not found in the XML card pool.");
        existing.ReplaceWith(new XElement(card));

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineChars = "\r\n",
            OmitXmlDeclaration = false
        };
        using var output = new StringWriterWithEncoding(settings.Encoding);
        using (var writer = XmlWriter.Create(output, settings))
            bundle.Save(writer);
        return output.ToString();
    }

    private sealed class StringWriterWithEncoding(Encoding encoding) : StringWriter
    {
        public override Encoding Encoding { get; } = encoding;
    }
}
