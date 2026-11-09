using System.IO;
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
    private static readonly IReadOnlyDictionary<string, string> RootScalars =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FILENAME"] = "file_name",
            ["CARDNAME"] = "card_name",
            ["MULTIVERSEID"] = "multiverse_id",
            ["CASTING_COST"] = "casting_cost",
            ["ARTID"] = "art_id",
            ["ARTIST"] = "artist",
            ["RARITY"] = "rarity",
            ["SETID"] = "set_id",
            ["EXPANSION"] = "expansion",
            ["POWER"] = "power",
            ["TOUGHNESS"] = "toughness",
            ["COLOUR"] = "colour",
            ["ARTUV"] = "artuv"
        };

    private static readonly IReadOnlyDictionary<string, string> ScalarAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FILENAME"] = "text",
            ["CARDNAME"] = "text",
            ["MULTIVERSEID"] = "value",
            ["CASTING_COST"] = "cost",
            ["ARTID"] = "value",
            ["ARTIST"] = "name",
            ["RARITY"] = "metaname",
            ["SETID"] = "value",
            ["EXPANSION"] = "value",
            ["POWER"] = "value",
            ["TOUGHNESS"] = "value",
            ["COLOUR"] = "value",
            ["ARTUV"] = "value"
        };

    private static readonly IReadOnlyDictionary<string, string> AbilityTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["STATIC_ABILITY"] = "static",
            ["TRIGGERED_ABILITY"] = "triggered",
            ["ACTIVATED_ABILITY"] = "activated",
            ["SPELL_ABILITY"] = "spell",
            ["UTILITY_ABILITY"] = "utility",
            ["MANA_ABILITY"] = "mana"
        };

    private static readonly IReadOnlyDictionary<string, string> ActionTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLAY_TIME_ACTION"] = "play_time",
            ["PLAYTIME"] = "play_time",
            ["RESOLUTION_TIME_ACTION"] = "resolution_time",
            ["CONTINUOUS_ACTION"] = "continuous",
            ["PERMANENT_TIME_ACTION"] = "permanent_time"
        };

    public static SourceCard Read(string path)
    {
        var root = XDocument.Load(path, LoadOptions.PreserveWhitespace).Root
            ?? throw new InvalidDataException($"Card XML has no root: {path}");
        if (!root.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not a CARD_V2 document: {path}");
        var fileName = RootValue(root, "FILENAME", "text");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException($"Card has no FILENAME: {path}");
        return new SourceCard(path, fileName, RootValue(root, "RARITY", "metaname") ?? "C", root);
    }

    public static string BuildStandaloneModule(XElement card, bool forceShoeboxSet = true)
    {
        if (!card.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The document root is not CARD_V2.");

        var writer = new LuaWriter();
        writer.Line("module((...), package.seeall)");
        writer.Line("card = {");
        writer.Push();
        writer.Field("export_version", card.Attribute("ExportVersion")?.Value ?? "1");

        foreach (var pair in RootScalars)
        {
            var element = card.Elements().FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            var value = element?.Attribute(ScalarAttributes[pair.Key])?.Value;
            if (forceShoeboxSet && pair.Key is "SETID" or "EXPANSION")
                value = "DPM_SBX";
            if (pair.Key is "SETID" or "EXPANSION" && string.IsNullOrEmpty(value) && forceShoeboxSet)
                value = "DPM_SBX";
            if (value is not null)
                writer.Field(pair.Value, value);
        }

        WriteSimpleElementList(writer, "types", card.ElementsByLocalName("TYPE"));
        WriteSimpleElementList(writer, "super_types", card.ElementsByLocalName("SUPERTYPE"));
        WriteSimpleElementList(writer, "sub_types", card.ElementsByLocalName("SUB_TYPE"));

        WriteNamedContainer(writer, "title", card.ElementsByLocalName("TITLE").FirstOrDefault());
        WriteNamedContainer(writer, "flavour_text", card.ElementsByLocalName("FLAVOURTEXT").FirstOrDefault());

        var abilities = card.Elements().Where(element => AbilityTypes.ContainsKey(element.Name.LocalName)).ToList();
        if (abilities.Count > 0)
        {
            writer.Line("abilities = {");
            writer.Push();
            foreach (var ability in abilities)
                WriteTypedElement(writer, ability, AbilityTypes[ability.Name.LocalName]);
            writer.Pop();
            writer.Line("},");
        }

        WriteRootElements(writer, card);
        writer.Pop();
        writer.Line("}");
        return writer.ToString();
    }

    public static string BuildCardObject(XElement card)
    {
        const string marker = "card = ";
        var module = BuildStandaloneModule(card);
        var start = module.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException("Generated card module has no card table.");
        return module[(start + marker.Length)..].Trim();
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
            output.Append("      name = ").Append(LuaString(card.FileName)).AppendLine(",");
            output.Append("      id = ").Append(LuaString((startId + index).ToString())).AppendLine(",");
            output.Append("      rarity = ").Append(LuaString(card.Rarity)).AppendLine();
            output.Append(index + 1 == collectible.Count ? "    }\n" : "    },\n");
        }
        output.AppendLine("  }");
        output.AppendLine("}");
        return output.ToString();
    }

    private static string? RootValue(XElement card, string elementName, string attributeName) =>
        card.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase))?.Attribute(attributeName)?.Value;

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

    private static void WriteRootElements(LuaWriter writer, XElement card)
    {
        var consumed = new HashSet<string>(RootScalars.Keys, StringComparer.OrdinalIgnoreCase)
        {
            "TYPE", "SUPERTYPE", "SUB_TYPE", "TITLE", "FLAVOURTEXT"
        };
        foreach (var abilityName in AbilityTypes.Keys)
            consumed.Add(abilityName);

        var remaining = card.Elements().Where(element => !consumed.Contains(element.Name.LocalName)).ToList();
        var helps = remaining.Where(element => element.Name.LocalName.Equals("HELP", StringComparison.OrdinalIgnoreCase)).ToList();
        if (helps.Count > 0)
        {
            writer.Line("more_info = {");
            writer.Push();
            for (var index = 0; index < helps.Count; index++)
            {
                writer.RawFieldName($"help_{index}");
                WriteElementTable(writer, helps[index], trailingComma: true, omitLocalisedText: false);
            }
            writer.Pop();
            writer.Line("},");
            remaining.RemoveAll(element => element.Name.LocalName.Equals("HELP", StringComparison.OrdinalIgnoreCase));
        }

        var queryText = remaining.Where(element => element.Name.LocalName.Equals("QUERYTEXT", StringComparison.OrdinalIgnoreCase)).ToList();
        if (queryText.Count > 0)
        {
            WriteTableList(writer, "query_text", queryText);
            remaining.RemoveAll(element => element.Name.LocalName.Equals("QUERYTEXT", StringComparison.OrdinalIgnoreCase));
        }

        var explain = remaining.FirstOrDefault(element => element.Name.LocalName.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase));
        if (explain is not null)
        {
            writer.RawFieldName("explain");
            WriteElementTable(writer, explain, trailingComma: true, omitLocalisedText: false);
            remaining.Remove(explain);
        }

        foreach (var marker in remaining.Where(IsMarker).ToList())
        {
            writer.Field(ToSnakeCase(marker.Name.LocalName), "True");
            remaining.Remove(marker);
        }

        foreach (var group in remaining.GroupBy(element => RootCollectionName(element.Name.LocalName), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Equals("sfx", StringComparison.OrdinalIgnoreCase))
                WriteTableList(writer, group.Key, group.ToList(), renameSfxText: true);
            else
                WriteTableList(writer, group.Key, group.ToList());
        }
    }

    private static bool IsMarker(XElement element) =>
        !element.HasAttributes && !element.HasElements && string.IsNullOrWhiteSpace(element.Value);

    private static string RootCollectionName(string xmlName) => xmlName.ToUpperInvariant() switch
    {
        "SFX" => "sfx",
        "TOKEN_REGISTRATION" => "token_registrations",
        "AI_BASE_SCORE" => "ai_base_scores",
        "AI_COUNTER_SCORE" => "ai_counter_scores",
        "AI_CUSTOM_SCORE" => "ai_custom_scores",
        "AI_AVAILABILITY" => "ai_availabilitys",
        "AI_PLANAR_DIE_BEHAVIOUR" => "ai_planar_die_behaviours",
        _ => ToSnakeCase(xmlName) + "s"
    };

    private static void WriteNamedContainer(LuaWriter writer, string name, XElement? element)
    {
        if (element is null)
            return;
        writer.Line($"{name} = {{");
        writer.Push();
        WriteChildren(writer, element);
        writer.Pop();
        writer.Line("},");
    }

    private static void WriteSimpleElementList(LuaWriter writer, string name, IEnumerable<XElement> elements)
    {
        var list = elements.ToList();
        if (list.Count == 0)
            return;
        writer.Line($"{name} = {{");
        writer.Push();
        foreach (var element in list)
            WriteElementTable(writer, element, trailingComma: true, omitLocalisedText: false);
        writer.Pop();
        writer.Line("},");
    }

    private static void WriteTableList(LuaWriter writer, string name, IReadOnlyList<XElement> elements, bool renameSfxText = false)
    {
        if (elements.Count == 0)
            return;
        writer.Line($"{name} = {{");
        writer.Push();
        foreach (var element in elements)
            WriteElementTable(writer, element, trailingComma: true, omitLocalisedText: false, renameSfxText: renameSfxText);
        writer.Pop();
        writer.Line("},");
    }

    private static void WriteTypedElement(LuaWriter writer, XElement element, string type)
    {
        writer.Line("{");
        writer.Push();
        writer.Field("type", type);
        writer.Line("{");
        writer.Push();
        WriteAttributes(writer, element, renameSfxText: false);
        WriteChildren(writer, element);
        WriteCode(writer, element);
        writer.Pop();
        writer.Line("},");
        writer.Pop();
        writer.Line("},");
    }

    private static void WriteElementTable(LuaWriter writer, XElement element, bool trailingComma,
        bool omitLocalisedText, bool renameSfxText = false)
    {
        writer.Line("{");
        writer.Push();
        WriteAttributes(writer, element, renameSfxText);
        if (!omitLocalisedText)
            WriteChildren(writer, element);
        WriteCode(writer, element);
        writer.Pop();
        writer.Line(trailingComma ? "}," : "}");
    }

    private static void WriteChildren(LuaWriter writer, XElement parent)
    {
        var children = parent.Elements().ToList();
        if (children.Count == 0)
            return;

        var texts = children.Where(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)).ToList();
        if (texts.Count > 0)
        {
            writer.Line("text = {");
            writer.Push();
            foreach (var text in texts)
            {
                writer.Line("{");
                writer.Push();
                writer.Field("language_code", text.Attribute("LanguageCode")?.Value ?? "en-US");
                writer.TField("data", text.Value);
                writer.Pop();
                writer.Line("},");
            }
            writer.Pop();
            writer.Line("},");
            children.RemoveAll(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase));
        }

        var actions = children.Where(element => ActionTypes.ContainsKey(element.Name.LocalName)).ToList();
        if (actions.Count > 0)
        {
            writer.Line("actions = {");
            writer.Push();
            foreach (var action in actions)
                WriteTypedElement(writer, action, ActionTypes[action.Name.LocalName]);
            writer.Pop();
            writer.Line("},");
            children.RemoveAll(element => ActionTypes.ContainsKey(element.Name.LocalName));
        }

        foreach (var group in children.GroupBy(element => ChildCollectionName(element.Name.LocalName), StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            WriteTableList(writer, group.Key, list,
                renameSfxText: group.Key.Equals("sfx", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string ChildCollectionName(string xmlName) => xmlName.ToUpperInvariant() switch
    {
        "SFX" => "sfx",
        "LOCALISED_TEXT" => "text",
        "AI_AVAILABILITY" => "ai_availabilitys",
        "AI_SIMPLIFIED_TARGETING" => "ai_simplified_targetings",
        "AVAILABILITY" => "availabilitys",
        _ => ToSnakeCase(xmlName) + "s"
    };

    private static void WriteAttributes(LuaWriter writer, XElement element, bool renameSfxText)
    {
        foreach (var attribute in element.Attributes())
        {
            var name = AttributeName(attribute.Name.LocalName);
            if (renameSfxText && name.Equals("text", StringComparison.OrdinalIgnoreCase))
                name = "name";
            writer.Field(name, attribute.Value);
        }
    }

    private static string AttributeName(string name)
    {
        if (name.Equals("LanguageCode", StringComparison.OrdinalIgnoreCase))
            return "language_code";
        if (name.StartsWith("order_", StringComparison.OrdinalIgnoreCase))
            return name.Replace("-", string.Empty, StringComparison.Ordinal);
        return name.Contains('-') ? name.Replace('-', '_') : name;
    }

    private static void WriteCode(LuaWriter writer, XElement element)
    {
        if (element.HasElements)
            return;
        var value = element.Value;
        if (string.IsNullOrWhiteSpace(value))
            return;
        writer.Field("code", NormalizeCode(value));
    }

    private static string NormalizeCode(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        while (lines.Length > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines = lines[1..];
        while (lines.Length > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines = lines[..^1];
        return string.Join("\n", lines);
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character == '-')
            {
                builder.Append('_');
                continue;
            }
            if (char.IsUpper(character))
            {
                if (index > 0 && name[index - 1] != '_' && char.IsLower(name[index - 1]))
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString().ToLowerInvariant();
    }

    private sealed class LuaWriter
    {
        private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function",
            "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true",
            "until", "while"
        };
        private readonly StringBuilder _builder = new();
        private int _indent;

        public void Push() => _indent += 2;
        public void Pop() => _indent -= 2;

        public void Line(string text)
        {
            _builder.Append(' ', _indent).AppendLine(text);
        }

        public void RawFieldName(string name)
        {
            _builder.Append(' ', _indent).Append(Key(name)).Append(" = ");
        }

        public void Field(string name, string value)
        {
            _builder.Append(' ', _indent).Append(Key(name)).Append(" = ").Append(Quote(value)).AppendLine(",");
        }

        public void TField(string name, string value)
        {
            _builder.Append(' ', _indent).Append(Key(name)).Append(" = T(").Append(Quote(value)).AppendLine("),");
        }

        public override string ToString() => _builder.ToString();

        private static string Key(string name)
        {
            if (!ReservedWords.Contains(name) && name.Length > 0 &&
                (char.IsLetter(name[0]) || name[0] == '_') &&
                name.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_'))
                return name;
            return "[" + Quote(name) + "]";
        }

        private static string Quote(string value)
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

    private static IEnumerable<XElement> ElementsByLocalName(this XElement parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
}
