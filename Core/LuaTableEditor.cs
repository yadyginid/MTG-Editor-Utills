using System.Text.RegularExpressions;

namespace UrukCardEditor.Core;

public readonly record struct LuaFieldSpan(int Start, int EndExclusive, int BraceStart, int BraceEnd, string Indent);

public static class LuaTableEditor
{
    public static LuaFieldSpan FindField(string source, string fieldName, int startIndex = 0)
    {
        var pattern = $@"(?m)^(?<indent>[ \t]*){Regex.Escape(fieldName)}\s*=\s*\{{";
        var match = Regex.Match(source, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        while (match.Success && match.Index < startIndex)
            match = match.NextMatch();
        if (!match.Success)
            throw new InvalidDataException($"Lua field '{fieldName}' was not found.");
        var braceStart = source.IndexOf('{', match.Index, match.Length);
        var braceEnd = FindMatchingBrace(source, braceStart);
        return new LuaFieldSpan(match.Index, braceEnd + 1, braceStart, braceEnd, match.Groups["indent"].Value);
    }

    public static string GetField(string source, string fieldName)
    {
        var span = FindField(source, fieldName);
        return source[span.Start..span.EndExclusive];
    }

    public static string ReplaceField(string source, string fieldName, string replacement)
    {
        var span = FindField(source, fieldName);
        var normalized = Reindent(replacement, span.Indent);
        return source[..span.Start] + normalized + source[span.EndExclusive..];
    }

    public static string AppendTableItem(string fieldText, string item)
    {
        var opening = fieldText.IndexOf('{');
        if (opening < 0)
            throw new InvalidDataException("The Lua field is not a table.");
        var closing = FindMatchingBrace(fieldText, opening);
        var body = fieldText[(opening + 1)..closing].Trim();
        var fieldIndent = Regex.Match(fieldText, @"^[ \t]*").Value;
        var itemIndent = fieldIndent + "  ";
        var normalizedItem = Reindent(item, itemIndent).TrimEnd();
        var separator = body.Length == 0 ? "\n" : ",\n";
        var prefix = fieldText[..closing].TrimEnd();
        return prefix + separator + normalizedItem + "\n" + fieldIndent + fieldText[closing..];
    }

    public static string RemoveDuplicateTableItems(string fieldText, string marker, out int removedCount)
    {
        removedCount = 0;
        var opening = fieldText.IndexOf('{');
        if (opening < 0)
            throw new InvalidDataException("The Lua field is not a table.");
        var closing = FindMatchingBrace(fieldText, opening);
        var items = ReadTopLevelTableItems(fieldText, opening, closing);
        var retained = new List<string>(items.Count);
        var found = false;
        foreach (var item in items)
        {
            if (!item.Contains(marker, StringComparison.Ordinal))
            {
                retained.Add(item);
                continue;
            }

            if (!found)
            {
                found = true;
                retained.Add(item);
            }
            else
            {
                removedCount++;
            }
        }

        if (removedCount == 0)
            return fieldText;

        var fieldIndent = Regex.Match(fieldText, @"^[ \t]*").Value;
        var itemIndent = fieldIndent + "  ";
        var normalizedItems = retained
            .Select(item => Reindent(item, itemIndent).TrimEnd())
            .ToList();
        var body = normalizedItems.Count == 0
            ? "\n"
            : "\n" + string.Join(",\n", normalizedItems) + "\n";
        return fieldText[..(opening + 1)] + body + fieldIndent + fieldText[closing..];
    }

    private static List<string> ReadTopLevelTableItems(string source, int opening, int closing)
    {
        var items = new List<string>();
        var index = opening + 1;
        while (index < closing)
        {
            while (index < closing && (char.IsWhiteSpace(source[index]) || source[index] == ','))
                index++;
            if (index >= closing)
                break;
            if (source[index] != '{')
                throw new InvalidDataException("The Lua table contains an unsupported top-level value.");
            var itemEnd = FindMatchingBrace(source, index);
            if (itemEnd > closing)
                throw new InvalidDataException("A Lua table item extends beyond its containing field.");
            items.Add(source[index..(itemEnd + 1)]);
            index = itemEnd + 1;
        }
        return items;
    }

    public static string ReplaceCardObjectInBundle(string bundleSource, string cardFileName, string standaloneCardSource)
    {
        var (objectStart, objectEnd, indent) = FindCardObject(bundleSource, cardFileName);

        var cardAssignment = Regex.Match(standaloneCardSource, @"(?m)^card\s*=\s*\{");
        if (!cardAssignment.Success)
            throw new InvalidDataException("The standalone Lua source does not contain 'card = {'.");
        var cardBrace = standaloneCardSource.IndexOf('{', cardAssignment.Index, cardAssignment.Length);
        var cardEnd = FindMatchingBrace(standaloneCardSource, cardBrace);
        var objectText = standaloneCardSource[cardBrace..(cardEnd + 1)];
        var normalized = Reindent(objectText, indent);
        return bundleSource[..objectStart] + normalized + bundleSource[(objectEnd + 1)..];
    }

    public static string ExtractCardAsStandalone(string bundleSource, string cardFileName)
    {
        var (objectStart, objectEnd, _) = FindCardObject(bundleSource, cardFileName);
        return "card = " + bundleSource[objectStart..(objectEnd + 1)] + Environment.NewLine;
    }

    private static (int Start, int End, string Indent) FindCardObject(string bundleSource, string cardFileName)
    {
        var needle = $"file_name = \"{cardFileName}\"";
        var idIndex = bundleSource.IndexOf(needle, StringComparison.Ordinal);
        if (idIndex < 0)
            throw new InvalidDataException($"Card '{cardFileName}' was not found in the booster set.");

        var openingMatches = Regex.Matches(bundleSource[..idIndex], @"(?m)^(?<indent>[ \t]*)\{\s*$");
        if (openingMatches.Count == 0)
            throw new InvalidDataException("The containing card table was not found in the booster set.");
        var objectMatch = openingMatches[^1];
        var objectStart = objectMatch.Index + objectMatch.Value.IndexOf('{');
        var objectEnd = FindMatchingBrace(bundleSource, objectStart);
        if (objectEnd < idIndex)
            throw new InvalidDataException("The detected booster table does not contain the selected card.");
        var indent = objectMatch.Groups["indent"].Value;
        return (objectStart, objectEnd, indent);
    }

    public static int FindMatchingBrace(string source, int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= source.Length || source[openingBrace] != '{')
            throw new ArgumentOutOfRangeException(nameof(openingBrace));
        var depth = 0;
        for (var i = openingBrace; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch is '"' or '\'')
            {
                i = SkipQuotedString(source, i, ch);
                continue;
            }
            if (ch == '[' && i + 1 < source.Length && source[i + 1] == '[')
            {
                var end = source.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new InvalidDataException("Unterminated Lua long string.");
                i = end + 1;
                continue;
            }
            if (ch == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                var lineEnd = source.IndexOf('\n', i + 2);
                if (lineEnd < 0)
                    return source.Length - 1;
                i = lineEnd;
                continue;
            }
            if (ch == '{') depth++;
            if (ch == '}' && --depth == 0) return i;
        }
        throw new InvalidDataException("Unbalanced Lua table braces.");
    }

    private static int SkipQuotedString(string source, int start, char quote)
    {
        for (var i = start + 1; i < source.Length; i++)
        {
            if (source[i] == '\\')
            {
                i++;
                continue;
            }
            if (source[i] == quote)
                return i;
        }
        throw new InvalidDataException("Unterminated Lua string.");
    }

    private static string Reindent(string text, string targetIndent)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim().Split('\n');
        var common = lines.Where(l => l.Trim().Length > 0)
            .Select(l => l.TakeWhile(char.IsWhiteSpace).Count())
            .DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => targetIndent + (l.Length >= common ? l[common..] : l)));
    }
}
