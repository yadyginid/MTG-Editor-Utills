using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UrukCardEditor.Core;

public sealed record ZedEntry(string Name, long Offset, int CompressedSize, int UncompressedSize, ushort Method, uint Crc32);

public static class ZedArchive
{
    private static readonly BigInteger Modulus = BigInteger.Parse(
        "095BC0FDBB0EEF370ACE10BB8241FB6102BB9B67EA8FE65237785C9FCA453D2ACEBFF8929C2F5F5834BB2141EDFCA370403831324872F89D08A9B893E2CD5EA2B6F862BE6375BD1955F764E077C1E1913DF226E5BA7FA7DEBFD703E4560085D4D4612A7D6EC27FB6F4A4CE4BBF079DA7E87FDD2FD6E80AFEB2F1977D3A3C83C8D9E12EBC63446C12E37BA47ECD9591145417B3754C056EB7C64DAFEA0A4222F243A556B99432FF267B020FAC8FA7768B9FA721BCF14DC422F4263F9DD42498C35E1D11E8B3A15249DEB95371AC1E5026E7648284C36F2BB706388DBAD92E83BED21581D0DB553563BC05CE212A1340DA789C25170971190789D349330F11E8E75",
        System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Exponent = new(0x11);

    public static List<ZedEntry> ReadEntries(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 264)
            throw new InvalidDataException("ZED file is too small.");

        stream.Seek(-8, SeekOrigin.End);
        var tail = ReadExactly(stream, 8);
        for (var i = 7; i > 0; i--)
            tail[i] ^= tail[i - 1];
        tail[0] ^= tail[7];

        var centralOffset = (long)tail[1] | ((long)tail[3] << 8) | ((long)tail[5] << 16) | ((long)tail[7] << 24);
        var encryptedCentralSize = tail[0] | (tail[2] << 8) | (tail[4] << 16) | (tail[6] << 24);
        if (centralOffset < 0 || centralOffset + encryptedCentralSize > stream.Length)
            throw new InvalidDataException("Invalid ZED central directory bounds.");

        stream.Position = centralOffset;
        var decodedHead = DecodeFirst256(ReadExactly(stream, 256));
        var centralSize = encryptedCentralSize - (256 - decodedHead.Length);
        if (centralSize < decodedHead.Length)
            throw new InvalidDataException("Invalid decoded central directory size.");

        var central = new byte[centralSize];
        Buffer.BlockCopy(decodedHead, 0, central, 0, decodedHead.Length);
        ReadExactly(stream, central, decodedHead.Length, central.Length - decodedHead.Length);
        for (var i = central.Length - 1; i > 0; i--)
            central[i] ^= central[i - 1];
        central[0] ^= central[^1];

        var entries = new List<ZedEntry>();
        var position = 0;
        while (position + 82 <= central.Length - 1)
        {
            var nameLength = ReadUInt16(central, position + 28);
            var recordLength = 82 + nameLength;
            if (position + recordLength > central.Length)
                break;
            var name = Encoding.UTF8.GetString(central, position + 46, nameLength);
            entries.Add(new ZedEntry(
                name,
                ReadUInt32(central, position + 42),
                checked((int)ReadUInt32(central, position + 20)),
                checked((int)ReadUInt32(central, position + 24)),
                ReadUInt16(central, position + 10),
                ReadUInt32(central, position + 16)));
            position += recordLength;
        }
        return entries;
    }

    public static byte[] Extract(string path, ZedEntry entry)
    {
        if (entry.CompressedSize == 0)
            return [];
        using var stream = File.OpenRead(path);
        stream.Position = entry.Offset;
        if (entry.Method == 0)
            return ReadExactly(stream, entry.CompressedSize);
        if (entry.Method != 8)
            throw new NotSupportedException($"Unsupported ZED compression method {entry.Method}.");

        var decodedHead = DecodeFirst256(ReadExactly(stream, 256));
        var adjustedSize = entry.CompressedSize - (256 - decodedHead.Length);
        if (adjustedSize < decodedHead.Length)
            throw new InvalidDataException("Invalid compressed ZED entry size.");
        var compressed = new byte[adjustedSize];
        Buffer.BlockCopy(decodedHead, 0, compressed, 0, decodedHead.Length);
        ReadExactly(stream, compressed, decodedHead.Length, compressed.Length - decodedHead.Length);
        for (var i = compressed.Length - 1; i > 0; i--)
            compressed[i] ^= compressed[i - 1];
        compressed[0] ^= 0x53;

        using var input = new MemoryStream(compressed, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = entry.UncompressedSize > 0 ? new MemoryStream(entry.UncompressedSize) : new MemoryStream();
        inflater.CopyTo(output);
        return output.ToArray();
    }

    public static void ExtractAll(string archivePath, string outputRoot)
    {
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        foreach (var entry in ReadEntries(archivePath).Where(e => e.CompressedSize > 0))
        {
            var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe ZED entry path: {entry.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, Extract(archivePath, entry));
        }
    }

    private static byte[] DecodeFirst256(byte[] encrypted)
    {
        var value = new BigInteger(encrypted, isUnsigned: true, isBigEndian: true);
        var decodedValue = BigInteger.ModPow(value, Exponent, Modulus);
        var compact = decodedValue.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (compact.Length > 256)
            compact = compact[^256..];
        var block = new byte[256];
        Buffer.BlockCopy(compact, 0, block, 256 - compact.Length, compact.Length);

        var seed = new byte[24];
        Buffer.BlockCopy(block, 235, seed, 0, 20);
        var mask = new byte[240];
        for (var i = 0; i < 12; i++)
        {
            seed[23] = (byte)i;
            var digest = SHA1.HashData(seed);
            Buffer.BlockCopy(digest, 0, mask, i * 20, 20);
        }
        for (var i = 0; i < 235; i++)
            block[i] ^= mask[i];

        var cursor = 0;
        if (block[cursor] == 0x80)
            cursor++;
        while (cursor < block.Length && block[cursor] == 0)
            cursor++;
        var length = 214 - cursor;
        if (cursor + 1 > block.Length || length < 0 || cursor + 1 + length > block.Length)
            throw new InvalidDataException("Unable to decode encrypted ZED block.");
        var result = new byte[length];
        Buffer.BlockCopy(block, cursor + 1, result, 0, length);
        return result;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));
    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var data = new byte[count];
        ReadExactly(stream, data, 0, count);
        return data;
    }

    private static void ReadExactly(Stream stream, byte[] data, int offset, int count)
    {
        while (count > 0)
        {
            var read = stream.Read(data, offset, count);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }
}
