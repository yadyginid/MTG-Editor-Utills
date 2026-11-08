using System.Text;
using System.Xml.Linq;

namespace Adding2013VersionCards;

public sealed record SourceCard(
    string SourcePath,
    string FileName,
    string Rarity,
    XElement Xml)
{
    public bool IsToken => Rarity.Equals("T", StringComparison.OrdinalIgnoreCase);
}

public static class Magic2013CardConverter
{
    public static SourceCard Read(string path)
    {
        var root = XDocument.Load(path, LoadOptions.PreserveWhitespace).Root
            ?? throw new InvalidDataException($"Card XML has no root: {path}");
        if (!root.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not a CARD_V2 document: {path}");
        var fileName = Attribute(root, "FILENAME", "text");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException($"Card has no FILENAME: {path}");
        return new SourceCard(path, fileName, Attribute(root, "RARITY", "metaname") ?? "C", root);
    }

    public static string BuildCardObject(XElement card)
    {
        var output = new StringBuilder();
        output.AppendLine("{");
        AddScalar(output, 2, "export_version", "1");
        AddScalar(output, 2, "file_name", Attribute(card, "FILENAME", "text"));
        AddScalar(output, 2, "card_name", Attribute(card, "CARDNAME", "text"));
        AddScalar(output, 2, "multiverse_id", Attribute(card, "MULTIVERSEID", "value"));
        AddScalar(output, 2, "casting_cost", Attribute(card, "CASTING_COST", "cost"));
        AddScalar(output, 2, "art_id", Attribute(card, "ARTID", "value"));
        AddScalar(output, 2, "artist", Attribute(card, "ARTIST", "name"));
        AddScalar(output, 2, "rarity", Attribute(card, "RARITY", "metaname"));
        AddScalar(output, 2, "set_id", "DPM_SBX");
        AddScalar(output, 2, "expansion", "DPM_SBX");
        AddScalar(output, 2, "power", Attribute(card, "POWER", "value"), optional: true);
        AddScalar(output, 2, "toughness", Attribute(card, "TOUGHNESS", "value"), optional: true);
        AddMetanameList(output, card, "TYPE", "types");
        AddMetanameList(output, card, "SUPERTYPE", "super_types");
        AddMetanameList(output, card, "SUB_TYPE", "sub_types");
        AddLocalizedContainer(output, 2, "title", card.Element("TITLE")?.Elements("LOCALISED_TEXT"));
        AddLocalizedContainer(output, 2, "flavour_text", card.Element("FLAVOURTEXT")?.Elements("LOCALISED_TEXT"), optional: true);

        var abilities = card.Elements().Where(element => AbilityType(element.Name.LocalName) is not null).ToList();
        if (abilities.Count > 0)
        {
            output.AppendLine("  abilities = {");
            foreach (var ability in abilities)
            {
                output.AppendLine("    {");
                AddScalar(output, 6, "type", AbilityType(ability.Name.LocalName)!);
                output.AppendLine("      {");
                AddTextTable(output, 8, ability.Elements("LOCALISED_TEXT"));
                output.AppendLine("      }");
                output.AppendLine("    },");
            }
            output.AppendLine("  },");
        }
        output.Append('}');
        return output.ToString();
    }

    public static string BuildRegistration(IReadOnlyList<SourceCard> cards, int startId)
    {
        var collectible = cards.Where(card => !card.IsToken).ToList();
        var output = new StringBuilder();
        output.AppendLine("module((...), package.seeall)");
        output.AppendLine("card_pool = {");
        output.AppendLine("  id = \"1\",");
        output.AppendLine("  content_id = \"0\",");
        output.AppendLine("  plane_id = \"1\",");
        output.AppendLine("  collection_logo_location = \"Art_Assets\\\\Frontend\\\\Collection\\\\shoebox_filter_button\",");
        output.AppendLine("  cards = {");
        for (var index = 0; index < collectible.Count; index++)
        {
            var card = collectible[index];
            output.AppendLine("    {");
            AddScalar(output, 6, "name", card.FileName);
            AddScalar(output, 6, "id", (startId + index).ToString());
            output.Append("      rarity = ").Append(LuaString(card.Rarity)).AppendLine();
            output.Append(index + 1 == collectible.Count ? "    }\n" : "    },\n");
        }
        output.AppendLine("  }");
        output.AppendLine("}");
        return output.ToString();
    }

    private static string? AbilityType(string elementName) => elementName switch
    {
        "STATIC_ABILITY" => "static",
        "TRIGGERED_ABILITY" => "triggered",
        "ACTIVATED_ABILITY" => "activated",
        "SPELL_ABILITY" => "spell",
        "UTILITY_ABILITY" => "utility",
        "MANA_ABILITY" => "mana",
        _ => null
    };

    private static void AddMetanameList(StringBuilder output, XElement card, string xmlName, string luaName)
    {
        var values = card.Elements(xmlName)
            .Select(element => (string?)element.Attribute("metaname"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (values.Count == 0)
            return;
        output.Append("  ").Append(luaName).AppendLine(" = {");
        foreach (var value in values)
            output.Append("    {metaname = ").Append(LuaString(value!)).AppendLine("},");
        output.AppendLine("  },");
    }

    private static void AddLocalizedContainer(
        StringBuilder output,
        int indent,
        string name,
        IEnumerable<XElement>? texts,
        bool optional = false)
    {
        var values = texts?.ToList() ?? [];
        if (values.Count == 0 && optional)
            return;
        output.Append(' ', indent).Append(name).AppendLine(" = {");
        AddTextTable(output, indent + 2, values);
        output.Append(' ', indent).AppendLine("},");
    }

    private static void AddTextTable(StringBuilder output, int indent, IEnumerable<XElement> texts)
    {
        output.Append(' ', indent).AppendLine("text = {");
        foreach (var text in texts)
        {
            output.Append(' ', indent + 2).AppendLine("{");
            AddScalar(output, indent + 4, "language_code", (string?)text.Attribute("LanguageCode") ?? "en-US");
            output.Append(' ', indent + 4).Append("data = T(").Append(LuaString(text.Value)).AppendLine("),");
            output.Append(' ', indent + 2).AppendLine("},");
        }
        output.Append(' ', indent).AppendLine("},");
    }

    private static void AddScalar(StringBuilder output, int indent, string name, string? value, bool optional = false)
    {
        if (string.IsNullOrEmpty(value) && optional)
            return;
        output.Append(' ', indent).Append(name).Append(" = ").Append(LuaString(value ?? "")).AppendLine(",");
    }

    private static string? Attribute(XElement card, string elementName, string attributeName) =>
        (string?)card.Element(elementName)?.Attribute(attributeName);

    private static string LuaString(string value)
    {
        var output = new StringBuilder(value.Length + 16).Append('"');
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            switch (valueByte)
            {
                case (byte)'"': output.Append("\\\""); break;
                case (byte)'\\': output.Append("\\\\"); break;
                case (byte)'\n': output.Append("\\n"); break;
                case (byte)'\r': output.Append("\\r"); break;
                case (byte)'\t': output.Append("\\t"); break;
                case >= 0x20 and <= 0x7E: output.Append((char)valueByte); break;
                default: output.Append('\\').Append(valueByte.ToString("D3")); break;
            }
        }
        return output.Append('"').ToString();
    }
}
