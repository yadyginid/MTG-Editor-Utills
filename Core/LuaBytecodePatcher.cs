using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace UrukCardEditor.Core;

/// <summary>
/// Applies scalar card edits to an official Lua 5.1 chunk without recompiling
/// it. This preserves the exact prototype/debug layout expected by Magic Duels.
/// </summary>
public static class LuaBytecodePatcher
{
    public static bool TryPatchCard(
        byte[] originalBytes,
        string originalSource,
        CardDocument edited,
        out byte[] patchedBytes,
        out string reason)
    {
        patchedBytes = [];
        reason = "";
        try
        {
            var original = CardDocument.Parse(edited.CardKey, CardSourceFormat.Lua, originalSource);
            if (!EquivalentLua(original.AbilitiesText, edited.AbilitiesText))
            {
                reason = "abilities differ from the official standalone file";
                return false;
            }

            var parser = new ChunkParser(originalBytes);
            var root = parser.Parse();
            var replacements = new Dictionary<StringRef, byte[]>();

            PatchScalar(root, "casting_cost", edited.ManaCost, replacements);
            PatchScalar(root, "power", edited.Power, replacements);
            PatchScalar(root, "toughness", edited.Toughness, replacements);
            PatchScalar(root, "artist", edited.Artist, replacements);

            var titleValues = ExtractPrintedTitleValues(originalSource);
            if (titleValues.Count == 0)
                throw new InvalidDataException("No localized titles were found in the official card.");
            var replacementTitle = Encoding.UTF8.GetBytes(edited.Title);
            var replacedTitles = 0;
            foreach (var value in titleValues)
            {
                foreach (var reference in root.AllStrings())
                {
                    if (!reference.Value.AsSpan().SequenceEqual(value))
                        continue;
                    replacements[reference] = replacementTitle;
                    replacedTitles++;
                }
            }
            if (replacedTitles == 0)
                throw new InvalidDataException("Localized titles were not found in the bytecode.");

            patchedBytes = ApplyReplacements(originalBytes, replacements);
            reason = $"string constants changed: {replacements.Count}";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool EquivalentLua(string left, string right) =>
        Regex.Replace(left, @"\s+", "").Equals(Regex.Replace(right, @"\s+", ""), StringComparison.Ordinal);

    private static void PatchScalar(
        FunctionRef root,
        string field,
        string value,
        Dictionary<StringRef, byte[]> replacements)
    {
        var replacement = Encoding.UTF8.GetBytes(value);
        var matches = new List<(FunctionRef Function, int ConstantIndex, StringRef Reference)>();
        foreach (var function in root.AllFunctions())
        {
            var keyIndexes = function.Constants
                .Select((constant, index) => (constant, index))
                .Where(item => item.constant is not null && Encoding.UTF8.GetString(item.constant.Value).Equals(field, StringComparison.Ordinal))
                .Select(item => item.index)
                .ToHashSet();
            if (keyIndexes.Count == 0)
                continue;

            foreach (var instruction in function.Instructions)
            {
                if ((instruction & 0x3F) != 9) // OP_SETTABLE
                    continue;
                var keyOperand = (int)((instruction >> 23) & 0x1FF);
                var valueOperand = (int)((instruction >> 14) & 0x1FF);
                if (keyOperand < 256 || valueOperand < 256 || !keyIndexes.Contains(keyOperand - 256))
                    continue;
                var valueIndex = valueOperand - 256;
                if (valueIndex >= function.Constants.Count || function.Constants[valueIndex] is not { } reference)
                    throw new InvalidDataException($"Field {field} does not reference a string constant.");
                matches.Add((function, valueIndex, reference));
            }
        }

        if (matches.Count != 1)
            throw new InvalidDataException($"Expected one assignment to field {field}; found {matches.Count}.");
        var match = matches[0];
        if (CountInstructionUses(match.Function, match.ConstantIndex) != 1)
            throw new InvalidDataException($"The constant for field {field} is used by multiple instructions.");
        replacements[match.Reference] = replacement;
    }

    private static int CountInstructionUses(FunctionRef function, int constantIndex)
    {
        var count = 0;
        foreach (var instruction in function.Instructions)
        {
            var op = (int)(instruction & 0x3F);
            var bx = (int)(instruction >> 14);
            if ((op is 1 or 5 or 7) && bx == constantIndex)
                count++;
            var b = (int)((instruction >> 23) & 0x1FF);
            var c = (int)((instruction >> 14) & 0x1FF);
            if (b == constantIndex + 256)
                count++;
            if (c == constantIndex + 256)
                count++;
        }
        return count;
    }

    private static HashSet<byte[]> ExtractPrintedTitleValues(string source)
    {
        var title = LuaTableEditor.GetField(source, "title");
        var values = new HashSet<byte[]>(ByteArrayComparer.Instance);
        foreach (Match match in Regex.Matches(title, "data\\s*=\\s*T\\(\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"\\)"))
            values.Add(DecodePrintedLiteral(match.Groups["v"].Value));
        return values;
    }

    private static byte[] DecodePrintedLiteral(string value)
    {
        using var output = new MemoryStream(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\\' && index + 1 < value.Length)
            {
                var next = value[index + 1];
                if (char.IsDigit(next))
                {
                    var end = index + 1;
                    while (end < value.Length && end < index + 4 && char.IsDigit(value[end]))
                        end++;
                    var number = int.Parse(value[(index + 1)..end]);
                    if (number == 0 && end < value.Length && value[end] is >= '\u0080' and <= '\u00FF')
                    {
                        index = end - 1;
                        continue;
                    }
                    output.WriteByte((byte)number);
                    index = end - 1;
                    continue;
                }
                index++;
                output.WriteByte(next switch
                {
                    'n' => (byte)'\n',
                    'r' => (byte)'\r',
                    't' => (byte)'\t',
                    _ => (byte)next
                });
                continue;
            }

            if (current <= byte.MaxValue)
                output.WriteByte((byte)current);
            else
                output.Write(Encoding.UTF8.GetBytes(current.ToString()));
        }
        return output.ToArray();
    }

    private static byte[] ApplyReplacements(byte[] original, Dictionary<StringRef, byte[]> replacements)
    {
        var ordered = replacements.OrderBy(item => item.Key.LengthOffset).ToList();
        using var output = new MemoryStream(original.Length + ordered.Sum(item => item.Value.Length - item.Key.Value.Length));
        var cursor = 0;
        foreach (var (reference, value) in ordered)
        {
            output.Write(original, cursor, reference.LengthOffset - cursor);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length + 1);
            output.Write(length);
            output.Write(value);
            output.WriteByte(0);
            cursor = reference.DataOffset + reference.StoredLength;
        }
        output.Write(original, cursor, original.Length - cursor);
        return output.ToArray();
    }

    private sealed class ChunkParser(byte[] bytes)
    {
        private int _offset;

        public FunctionRef Parse()
        {
            if (bytes.Length < 12 || bytes[0] != 0x1B || bytes[1] != (byte)'L' || bytes[2] != (byte)'u' ||
                bytes[3] != (byte)'a' || bytes[4] != 0x51 || bytes[6] != 1 || bytes[7] != 4 || bytes[8] != 4 ||
                bytes[9] != 4 || bytes[10] != 8)
                throw new InvalidDataException("Unsupported Lua bytecode format.");
            _offset = 12;
            return ParseFunction();
        }

        private FunctionRef ParseFunction()
        {
            _ = ReadString();
            Skip(8);
            Skip(4);
            var codeCount = ReadInt32();
            var instructions = new List<uint>(codeCount);
            for (var index = 0; index < codeCount; index++)
                instructions.Add(ReadUInt32());

            var constantCount = ReadInt32();
            var constants = new List<StringRef?>(constantCount);
            for (var index = 0; index < constantCount; index++)
            {
                var type = ReadByte();
                constants.Add(type switch
                {
                    0 => null,
                    1 => SkipAndNull(1),
                    3 => SkipAndNull(8),
                    4 => ReadString(),
                    _ => throw new InvalidDataException($"Unknown Lua constant type: {type}.")
                });
            }

            var childCount = ReadInt32();
            var children = new List<FunctionRef>(childCount);
            for (var index = 0; index < childCount; index++)
                children.Add(ParseFunction());

            Skip(ReadInt32() * 4);
            var localCount = ReadInt32();
            for (var index = 0; index < localCount; index++)
            {
                _ = ReadString();
                Skip(8);
            }
            var upvalueCount = ReadInt32();
            for (var index = 0; index < upvalueCount; index++)
                _ = ReadString();
            return new FunctionRef(instructions, constants, children);
        }

        private StringRef? SkipAndNull(int count) { Skip(count); return null; }

        private StringRef? ReadString()
        {
            var lengthOffset = _offset;
            var length = ReadInt32();
            if (length == 0)
                return null;
            if (length < 1 || _offset + length > bytes.Length)
                throw new InvalidDataException("Corrupted Lua string.");
            var dataOffset = _offset;
            var value = bytes.AsSpan(_offset, length - 1).ToArray();
            Skip(length);
            return new StringRef(lengthOffset, dataOffset, length, value);
        }

        private byte ReadByte()
        {
            if (_offset >= bytes.Length) throw new EndOfStreamException();
            return bytes[_offset++];
        }

        private int ReadInt32()
        {
            if (_offset + 4 > bytes.Length) throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        private uint ReadUInt32()
        {
            if (_offset + 4 > bytes.Length) throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        private void Skip(int count)
        {
            if (count < 0 || _offset + count > bytes.Length) throw new EndOfStreamException();
            _offset += count;
        }
    }

    private sealed record StringRef(int LengthOffset, int DataOffset, int StoredLength, byte[] Value);

    private sealed record FunctionRef(List<uint> Instructions, List<StringRef?> Constants, List<FunctionRef> Children)
    {
        public IEnumerable<FunctionRef> AllFunctions()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var nested in child.AllFunctions())
                    yield return nested;
        }

        public IEnumerable<StringRef> AllStrings() =>
            AllFunctions().SelectMany(function => function.Constants).OfType<StringRef>();
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            foreach (var value in obj) hash.Add(value);
            return hash.ToHashCode();
        }
    }
}
