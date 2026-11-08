using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UrukCardEditor.Core;

public sealed class CardDocument
{
    private CardDocument() { }

    public required CardSourceFormat Format { get; init; }
    public required string CardKey { get; init; }
    public string SourceText { get; private set; } = "";
    public required string Title { get; set; }
    public required string ManaCost { get; set; }
    public required string Power { get; set; }
    public required string Toughness { get; set; }
    public required string Artist { get; set; }
    public required string ArtId { get; init; }
    public required string AbilitiesText { get; set; }

    public static CardDocument Parse(string cardKey, CardSourceFormat format, string source)
    {
        return format == CardSourceFormat.Xml
            ? ParseXml(cardKey, source)
            : ParseLua(cardKey, source);
    }

    public string ApplyChanges()
    {
        SourceText = Format == CardSourceFormat.Xml ? ApplyXml() : ApplyLua();
        return SourceText;
    }

    public void ValidateAbilities()
    {
        if (Format == CardSourceFormat.Xml)
        {
            _ = XDocument.Parse($"<ROOT>{AbilitiesText}</ROOT>", LoadOptions.PreserveWhitespace);
            return;
        }
        var candidate = LuaTableEditor.ReplaceField(SourceText, "abilities", AbilitiesText);
        LuaDecompiler.Validate(candidate, CardKey + ".lol");
    }

    private static CardDocument ParseLua(string key, string source)
    {
        var fallbackTitle = FriendlyName(key);
        string abilities;
        try { abilities = LuaTableEditor.GetField(source, "abilities"); }
        catch { abilities = "abilities = {\n}"; }

        var title = fallbackTitle;
        try
        {
            var titleField = LuaTableEditor.GetField(source, "title");
            var titleMatch = Regex.Match(titleField, "data\\s*=\\s*T\\(\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"\\)");
            if (titleMatch.Success && !titleMatch.Groups["v"].Value.Contains("\\00", StringComparison.Ordinal))
                title = UnescapeLua(titleMatch.Groups["v"].Value);
        }
        catch { }

        return new CardDocument
        {
            Format = CardSourceFormat.Lua,
            CardKey = key,
            SourceText = source,
            Title = title,
            ManaCost = ReadLuaScalar(source, "casting_cost"),
            Power = ReadLuaScalar(source, "power"),
            Toughness = ReadLuaScalar(source, "toughness"),
            Artist = ReadLuaScalar(source, "artist"),
            ArtId = ReadLuaScalar(source, "art_id"),
            AbilitiesText = abilities
        };
    }

    private static CardDocument ParseXml(string key, string source)
    {
        var document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("The card XML has no root element.");
        var title = root.Element("TITLE")?.Elements("LOCALISED_TEXT")
            .FirstOrDefault(e => (string?)e.Attribute("LanguageCode") == "en-US")?.Value
            ?? root.Element("TITLE")?.Elements("LOCALISED_TEXT").FirstOrDefault()?.Value
            ?? FriendlyName(key);
        var abilities = root.Elements().Where(IsAbilityElement).Select(e => e.ToString()).ToList();
        return new CardDocument
        {
            Format = CardSourceFormat.Xml,
            CardKey = key,
            SourceText = source,
            Title = title,
            ManaCost = (string?)root.Element("CASTING_COST")?.Attribute("cost") ?? "",
            Power = (string?)root.Element("POWER")?.Attribute("value") ?? "",
            Toughness = (string?)root.Element("TOUGHNESS")?.Attribute("value") ?? "",
            Artist = (string?)root.Element("ARTIST")?.Attribute("name") ?? "",
            ArtId = (string?)root.Element("ARTID")?.Attribute("value") ?? "",
            AbilitiesText = string.Join(Environment.NewLine, abilities)
        };
    }

    private string ApplyLua()
    {
        ValidateAbilities();
        var source = SourceText;
        source = ReplaceLuaScalar(source, "casting_cost", ManaCost);
        source = ReplaceLuaScalar(source, "power", Power);
        source = ReplaceLuaScalar(source, "toughness", Toughness);
        source = ReplaceLuaScalar(source, "artist", Artist);
        var escapedTitle = EscapeLua(Title);
        var titleField = "title = {\n" +
                         "  text = {\n" +
                         $"    {{ language_code = \"en-US\", data = T(\"{escapedTitle}\") }},\n" +
                         $"    {{ language_code = \"ru-RU\", data = T(\"{escapedTitle}\") }}\n" +
                         "  }\n" +
                         "}";
        source = LuaTableEditor.ReplaceField(source, "title", titleField);
        source = LuaTableEditor.ReplaceField(source, "abilities", AbilitiesText);
        LuaDecompiler.Validate(source, CardKey + ".lol");
        return source;
    }

    private string ApplyXml()
    {
        var document = XDocument.Parse(SourceText, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("The card XML has no root element.");
        SetXmlAttribute(root, "CASTING_COST", "cost", ManaCost);
        SetXmlAttribute(root, "POWER", "value", Power);
        SetXmlAttribute(root, "TOUGHNESS", "value", Toughness);
        SetXmlAttribute(root, "ARTIST", "name", Artist);

        var title = root.Element("TITLE") ?? new XElement("TITLE");
        if (title.Parent is null)
            root.AddFirst(title);
        var titleNodes = title.Elements("LOCALISED_TEXT").ToList();
        foreach (var code in new[] { "en-US", "ru-RU" })
        {
            var node = titleNodes.FirstOrDefault(e => (string?)e.Attribute("LanguageCode") == code);
            if (node is null)
            {
                node = new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", code));
                title.Add(node);
            }
            node.Value = Title;
        }

        var parsedAbilities = XDocument.Parse($"<ROOT>{AbilitiesText}</ROOT>", LoadOptions.PreserveWhitespace)
            .Root!.Elements().Select(e => new XElement(e)).ToList();
        var oldAbilities = root.Elements().Where(IsAbilityElement).ToList();
        var insertionPoint = oldAbilities.LastOrDefault()?.ElementsAfterSelf().FirstOrDefault()
            ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "SFX");
        foreach (var old in oldAbilities) old.Remove();
        if (insertionPoint?.Parent is not null)
            insertionPoint.AddBeforeSelf(parsedAbilities);
        else
            root.Add(parsedAbilities);
        return document.ToString();
    }

    private static bool IsAbilityElement(XElement element) =>
        element.Name.LocalName.EndsWith("_ABILITY", StringComparison.OrdinalIgnoreCase);

    private static void SetXmlAttribute(XElement root, string elementName, string attributeName, string value)
    {
        var element = root.Element(elementName);
        if (element is null)
        {
            element = new XElement(elementName);
            root.Add(element);
        }
        element.SetAttributeValue(attributeName, value);
    }

    private static string ReadLuaScalar(string source, string name)
    {
        var match = Regex.Match(source, $"(?m)^\\s*{Regex.Escape(name)}\\s*=\\s*\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"");
        return match.Success ? UnescapeLua(match.Groups["v"].Value) : "";
    }

    private static string ReplaceLuaScalar(string source, string name, string value)
    {
        var pattern = $"(?m)^(?<indent>\\s*){Regex.Escape(name)}\\s*=\\s*\\\"(?:\\\\.|[^\\\"])*\\\"";
        if (!Regex.IsMatch(source, pattern))
            return source;
        return new Regex(pattern).Replace(source, m => $"{m.Groups["indent"].Value}{name} = \"{EscapeLua(value)}\"", 1);
    }

    private static string FriendlyName(string key)
    {
        var withoutId = Regex.Replace(key, @"_\d+$", "");
        return string.Join(' ', withoutId.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }

    private static string EscapeLua(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
    private static string UnescapeLua(string value) => value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
}
