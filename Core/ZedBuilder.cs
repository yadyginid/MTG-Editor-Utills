using System.Text;

static class ZedBuilder
{
    private const int SeedCentralHeaderSize = 340;
    private const int EntryPrefixSize = 46;
    private const int EntrySuffixSize = 36;

    public static void CreateDataBin(string genuineZedPath, string outputPath)
    {
        using var input = File.OpenRead(genuineZedPath);
        if (input.Length < 1024)
            throw new InvalidDataException("The genuine ZED is too small to provide a header.");

        input.Position = input.Length - 8;
        var tail = ReadExactly(input, 8);
        for (var i = 7; i > 0; i--)
            tail[i] ^= tail[i - 1];
        tail[0] ^= tail[7];
        var centralOffset = (long)tail[1] | ((long)tail[3] << 8) | ((long)tail[5] << 16) | ((long)tail[7] << 24);

        input.Position = centralOffset;
        var seedCentralHeader = ReadExactly(input, SeedCentralHeaderSize);

        input.Position = centralOffset + 541;
        var entryPrefix = ReadExactly(input, EntryPrefixSize);
        for (var i = entryPrefix.Length - 1; i > 0; i--)
            entryPrefix[i] ^= entryPrefix[i - 1];
        entryPrefix[0] = 0x50;

        var prefixPayloadLength = ReadUInt32(entryPrefix, 42);
        if (prefixPayloadLength > input.Length)
            throw new InvalidDataException("Invalid metadata payload length in the genuine ZED.");
        input.Position = 0;
        var metadataPayload = ReadExactly(input, checked((int)prefixPayloadLength));

        var nameLength = ReadUInt16(entryPrefix, 28);
        input.Position = centralOffset + 541 + EntryPrefixSize + nameLength;
        var entrySuffix = ReadExactly(input, EntrySuffixSize);
        for (var i = entrySuffix.Length - 1; i > 0; i--)
            entrySuffix[i] ^= entrySuffix[i - 1];
        entrySuffix[0] = 0x0A;

        entryPrefix[38] = 0x30;
        entryPrefix[10] = 0;
        Array.Clear(entryPrefix, 16, 4);
        Array.Clear(entryPrefix, 20, 4);
        Array.Clear(entryPrefix, 24, 4);
        Array.Clear(entryPrefix, 28, 2);
        Array.Clear(entryPrefix, 42, 4);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        output.Write(metadataPayload);
        output.Write(seedCentralHeader);
        output.Write(entryPrefix);
        output.Write(entrySuffix);
    }

    public static void CreateZed(string dataBinPath, string sourceFolderPath, string outputPath)
    {
        var sourceFolder = new DirectoryInfo(sourceFolderPath);
        if (!sourceFolder.Exists)
            throw new DirectoryNotFoundException(sourceFolder.FullName);

        var data = File.ReadAllBytes(dataBinPath);
        if (data.Length <= SeedCentralHeaderSize + EntryPrefixSize + EntrySuffixSize)
            throw new InvalidDataException("Data.bin is incomplete.");
        var metadataLength = data.Length - (SeedCentralHeaderSize + EntryPrefixSize + EntrySuffixSize);
        var metadata = data.AsSpan(0, metadataLength).ToArray();
        var seedCentralHeader = data.AsSpan(metadataLength, SeedCentralHeaderSize).ToArray();
        var entryPrefix = data.AsSpan(metadataLength + SeedCentralHeaderSize, EntryPrefixSize).ToArray();
        var entrySuffix = data.AsSpan(metadataLength + SeedCentralHeaderSize + EntryPrefixSize, EntrySuffixSize).ToArray();

        var outputFullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        using var output = File.Create(outputFullPath);
        output.Write(metadata);

        using var central = new MemoryStream();
        central.Write(seedCentralHeader);
        AddEntry(sourceFolder, sourceFolder.Name, output, central, entryPrefix, entrySuffix);

        var centralBytes = central.ToArray();
        for (var i = SeedCentralHeaderSize; i < centralBytes.Length; i++)
            centralBytes[i] ^= centralBytes[i - 1];
        var centralOffset = output.Position;
        output.Write(centralBytes);

        var tail = new byte[8];
        WriteUInt32Interleaved(tail, 0, checked((uint)centralBytes.Length));
        WriteUInt32Interleaved(tail, 1, checked((uint)centralOffset));
        tail[0] ^= tail[7];
        for (var i = 1; i < tail.Length; i++)
            tail[i] ^= tail[i - 1];
        output.Write(tail);
    }

    private static void AddEntry(DirectoryInfo directory, string archivePath, Stream output, Stream central, byte[] entryPrefix, byte[] entrySuffix)
    {
        WriteEntryHeader(archivePath.Replace('\\', '/') + "/", true, [], output.Position, central, entryPrefix, entrySuffix);
        foreach (var childDirectory in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            AddEntry(childDirectory, archivePath + "/" + childDirectory.Name, output, central, entryPrefix, entrySuffix);
        foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(file.FullName);
            var offset = output.Position;
            output.Write(bytes);
            WriteEntryHeader(archivePath + "/" + file.Name, false, bytes, offset, central, entryPrefix, entrySuffix);
        }
    }

    private static void WriteEntryHeader(string archivePath, bool isDirectory, byte[] content, long offset, Stream central, byte[] entryPrefix, byte[] entrySuffix)
    {
        var name = Encoding.UTF8.GetBytes(archivePath.Replace('\\', '/'));
        if (name.Length > ushort.MaxValue)
            throw new InvalidDataException("Archive path is too long.");
        var header = new byte[EntryPrefixSize + name.Length + EntrySuffixSize];
        Buffer.BlockCopy(entryPrefix, 0, header, 0, EntryPrefixSize);
        Buffer.BlockCopy(name, 0, header, EntryPrefixSize, name.Length);
        Buffer.BlockCopy(entrySuffix, 0, header, EntryPrefixSize + name.Length, EntrySuffixSize);
        WriteUInt16(header, 28, checked((ushort)name.Length));
        WriteUInt32(header, 42, checked((uint)offset));
        if (isDirectory)
        {
            header[38] = 0x30;
        }
        else
        {
            WriteUInt32(header, 16, ComputeCrc32(content));
            WriteUInt32(header, 20, checked((uint)content.Length));
            WriteUInt32(header, 24, checked((uint)content.Length));
            header[38] = 0x20;
        }
        central.Write(header);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt32Interleaved(byte[] data, int start, uint value)
    {
        data[start] = (byte)value;
        data[start + 2] = (byte)(value >> 8);
        data[start + 4] = (byte)(value >> 16);
        data[start + 6] = (byte)(value >> 24);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var data = new byte[count];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = stream.Read(data, offset, data.Length - offset);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
        return data;
    }
}
