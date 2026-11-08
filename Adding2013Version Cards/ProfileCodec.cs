using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Win32;

namespace Adding2013VersionCards;

public sealed record ProfilePatchResult(
    string ProfilePath,
    string ProfileBackup,
    string HashBackup,
    int ChangedCards,
    int FirstId,
    int LastId,
    byte Quantity);

public static class ProfilePatcher
{
    public static string? LocateProfile(string? requestedPath, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var explicitPath = Path.GetFullPath(requestedPath);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException("The requested Steam profile was not found.", explicitPath);
            return explicitPath;
        }

        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
                steamRoots.Add(Path.GetFullPath(steamPath));
        }
        var defaultSteam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultSteam))
            steamRoots.Add(defaultSteam);

        var profiles = steamRoots
            .SelectMany(root => Directory.Exists(Path.Combine(root, "userdata"))
                ? Directory.EnumerateFiles(Path.Combine(root, "userdata"), "*.profile", SearchOption.AllDirectories)
                : [])
            .Where(path => path.Replace('/', '\\').Contains(@"\316010\remote\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (profiles.Count == 0)
        {
            log("No Magic Duels Steam profile was auto-detected.");
            return null;
        }
        if (profiles.Count > 1)
            log($"Multiple profiles were found; using the most recently modified one: {profiles[0]}");
        return profiles[0];
    }

    public static ProfilePatchResult Patch(
        string profilePath,
        int firstId,
        int lastId,
        byte quantity,
        string backupDirectory)
    {
        if (Process.GetProcessesByName("MagicDuels").Length != 0 ||
            Process.GetProcessesByName("MTG-Editor-Utills").Length != 0)
            throw new InvalidOperationException("Close Magic Duels and MTG-Editor-Utills before changing the profile.");
        if (firstId < 0 || lastId < firstId || lastId >= ProfileCodec.CardCapacity)
            throw new ArgumentOutOfRangeException(nameof(firstId));
        if (quantity > 7)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        profilePath = Path.GetFullPath(profilePath);
        var hashPath = Path.Combine(Path.GetDirectoryName(profilePath)!, "hash.file");
        if (!File.Exists(hashPath))
            throw new FileNotFoundException("hash.file was not found next to the profile.", hashPath);

        var originalProfile = File.ReadAllBytes(profilePath);
        var originalHash = File.ReadAllBytes(hashPath);
        if (originalHash.Length < 4)
            throw new InvalidDataException("hash.file is too small.");
        var storedHash = BinaryPrimitives.ReadUInt32LittleEndian(originalHash);
        var currentHash = ProfileCodec.ComputeHash(originalProfile);
        if (storedHash != currentHash)
            throw new InvalidDataException($"Profile hash mismatch: stored 0x{storedHash:X8}, computed 0x{currentHash:X8}.");

        var content = ProfileCodec.Decode(originalProfile);
        if (!ProfileCodec.Encode(content).AsSpan().SequenceEqual(originalProfile))
            throw new InvalidDataException("Profile decode/encode round-trip is not byte-identical; refusing to write.");

        var cards = ProfileCodec.ReadCards(content);
        var changed = 0;
        for (var id = firstId; id <= lastId; id++)
        {
            if (cards[id] == quantity)
                continue;
            cards[id] = quantity;
            changed++;
        }
        ProfileCodec.WriteCards(content, cards);

        var updatedProfile = ProfileCodec.Encode(content);
        var updatedHashValue = ProfileCodec.ComputeHash(updatedProfile);
        var updatedHash = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(updatedHash, updatedHashValue);
        var verificationCards = ProfileCodec.ReadCards(ProfileCodec.Decode(updatedProfile));
        if (Enumerable.Range(firstId, lastId - firstId + 1).Any(id => verificationCards[id] != quantity))
            throw new InvalidDataException("The generated profile failed verification.");

        Directory.CreateDirectory(backupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var profileBackup = Path.Combine(backupDirectory, $"{Path.GetFileName(profilePath)}.{stamp}.bak");
        var hashBackup = Path.Combine(backupDirectory, $"{Path.GetFileName(hashPath)}.{stamp}.bak");
        File.Copy(profilePath, profileBackup, overwrite: false);
        File.Copy(hashPath, hashBackup, overwrite: false);

        var tempProfile = profilePath + ".m13-import-new";
        var tempHash = hashPath + ".m13-import-new";
        File.WriteAllBytes(tempProfile, updatedProfile);
        File.WriteAllBytes(tempHash, updatedHash);
        try
        {
            File.Move(tempProfile, profilePath, overwrite: true);
            File.Move(tempHash, hashPath, overwrite: true);
        }
        catch
        {
            File.Copy(profileBackup, profilePath, overwrite: true);
            File.Copy(hashBackup, hashPath, overwrite: true);
            throw;
        }
        finally
        {
            if (File.Exists(tempProfile)) File.Delete(tempProfile);
            if (File.Exists(tempHash)) File.Delete(tempHash);
        }

        return new ProfilePatchResult(profilePath, profileBackup, hashBackup, changed, firstId, lastId, quantity);
    }
}

public static class ProfileCodec
{
    public const int CardCapacity = 2048;

    public static byte[] Decode(byte[] encoded)
    {
        if (encoded.Length <= 0x85E + 28)
            throw new InvalidDataException("Profile is too small.");
        var fileContent = (byte[])encoded.Clone();
        for (var index = fileContent.Length - 1; index >= 1; index--)
            fileContent[index] ^= fileContent[index - 1];
        fileContent[0] ^= fileContent[^1];

        var content = new byte[fileContent.Length - 28];
        Array.Copy(fileContent, 0, content, 0, 0x842);
        Array.Copy(fileContent, 0x85E, content, 0x842, fileContent.Length - 0x85E);
        var fieldLength = content[0x45A] + ((content[0x45B] ^ content[0x45A]) << 8);
        var fieldEnd = 0x459 + fieldLength;
        if (fieldEnd >= content.Length)
            throw new InvalidDataException("Nested profile field is outside the file.");
        for (var index = fieldEnd; index >= 0x45B; index--)
            content[index] ^= content[index - 1];
        content[0x45A] ^= content[fieldEnd];
        return content;
    }

    public static byte[] Encode(byte[] decoded)
    {
        var content = (byte[])decoded.Clone();
        var fileContent = new byte[content.Length + 28];
        var fieldLength = content[0x45A] + (content[0x45B] << 8);
        var fieldEnd = 0x459 + fieldLength;
        if (fieldEnd >= content.Length)
            throw new InvalidDataException("Nested profile field is outside the file.");
        content[0x45A] ^= content[fieldEnd];
        for (var index = 0x45B; index <= fieldEnd; index++)
            content[index] ^= content[index - 1];
        Array.Copy(content, 0, fileContent, 0, 0x842);
        Array.Copy(content, 0x842, fileContent, 0x85E, fileContent.Length - 0x85E);
        fileContent[0x85A] = 0xE8;
        fileContent[0x85B] = 0x03;
        fileContent[0] ^= fileContent[^1];
        for (var index = 1; index < fileContent.Length; index++)
            fileContent[index] ^= fileContent[index - 1];
        return fileContent;
    }

    public static byte[] ReadCards(byte[] content)
    {
        var offset = content.Length - 1026;
        if (offset < 0)
            throw new InvalidDataException("Profile has no 2048-card unlock table.");
        var cards = new byte[CardCapacity];
        for (var index = 0; index < 1024; index++)
        {
            cards[2 * index] = (byte)(content[offset + index] & 0x0F);
            cards[2 * index + 1] = (byte)((content[offset + index] >> 4) & 0x0F);
        }
        return cards;
    }

    public static void WriteCards(byte[] content, byte[] cards)
    {
        if (cards.Length != CardCapacity)
            throw new ArgumentException($"Expected {CardCapacity} card quantities.", nameof(cards));
        var offset = content.Length - 1026;
        for (var index = 0; index < 1024; index++)
            content[offset + index] = (byte)((cards[2 * index] & 0x0F) | ((cards[2 * index + 1] << 4) & 0xF0));
    }

    public static uint ComputeHash(ReadOnlySpan<byte> data)
    {
        var hash = 0x811C9DC5u;
        foreach (var value in data)
        {
            hash = unchecked(hash * 0x01000193u);
            hash ^= value;
        }
        return hash;
    }
}
