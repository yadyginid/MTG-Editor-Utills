using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace UrukCardEditor.Core;

public sealed class GameRepository
{
    private readonly Dictionary<string, string> _bundleSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BundleLocation> _artEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _thumbnailGate = new(3, 3);
    private readonly List<BundleLocation> _aggregateCardFiles = [];
    private readonly object _sync = new();

    public GameRepository(string? gameDirectory = null)
    {
        GameDirectory = Path.GetFullPath(gameDirectory ?? EditorSettings.LoadGameDirectory() ?? LocateGameDirectory());
        Data008Path = Path.Combine(GameDirectory, "DATA_008.ZED");
        if (!File.Exists(Data008Path))
            throw new FileNotFoundException("DATA_008.ZED was not found.", Data008Path);
        var sessionName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        SessionDirectory = Path.Combine(GameDirectory, "UrukCardEditorData", "Sessions", sessionName);
        WorkingRoot = Path.Combine(SessionDirectory, "DATA_008");
        BackupDirectory = Path.Combine(GameDirectory, "UrukCardEditorData", "Backups");
    }

    public string GameDirectory { get; }
    public string Data008Path { get; }
    public string SessionDirectory { get; }
    public string WorkingRoot { get; }
    public string BackupDirectory { get; }
    public int PendingChanges { get; private set; }

    public IReadOnlyList<CardIndexItem> BuildCatalog(Action<string>? progress = null)
    {
        var archives = Directory.EnumerateFiles(GameDirectory, "DATA_*.ZED", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Number = ParseArchiveNumber(path) })
            .Where(x => x.Number >= 0)
            .OrderBy(x => x.Number)
            .ToList();
        var cards = new Dictionary<string, CardIndexItem>(StringComparer.OrdinalIgnoreCase);
        _aggregateCardFiles.Clear();
        _artEntries.Clear();

        foreach (var archive in archives)
        {
            progress?.Invoke($"Indexing DATA_{archive.Number:000}…");
            foreach (var entry in ZedArchive.ReadEntries(archive.Path).Where(e => e.CompressedSize > 0))
            {
                var normalized = entry.Name.Replace('\\', '/');
                if (normalized.Contains("/Data_All_Platforms/Art_Assets/Illustrations/", StringComparison.OrdinalIgnoreCase) &&
                    normalized.EndsWith(".tdx", StringComparison.OrdinalIgnoreCase))
                {
                    var artId = Path.GetFileNameWithoutExtension(normalized);
                    _artEntries[artId] = new BundleLocation(archive.Path, entry, archive.Number);
                    continue;
                }
                if (!normalized.Contains("/Data_All_Platforms/Cards/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var fileName = Path.GetFileName(normalized);
                if (fileName.EndsWith(".lol", StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(Path.GetFileNameWithoutExtension(fileName), @"_\d{5,}$"))
                {
                    _aggregateCardFiles.RemoveAll(existing =>
                        Path.GetFileName(existing.Entry.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                        existing.ArchiveNumber <= archive.Number);
                    if (!_aggregateCardFiles.Any(existing =>
                            Path.GetFileName(existing.Entry.Name).Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                            existing.ArchiveNumber > archive.Number))
                        _aggregateCardFiles.Add(new BundleLocation(archive.Path, entry, archive.Number));
                    continue;
                }
                var extension = Path.GetExtension(fileName);
                if (extension is not (".lol" or ".xml") && !extension.Equals(".LOL", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".XML", StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = Path.GetFileNameWithoutExtension(fileName);
                if (!Regex.IsMatch(key, @"_\d{5,}$"))
                    continue;
                var format = extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ? CardSourceFormat.Xml : CardSourceFormat.Lua;
                var item = new CardIndexItem
                {
                    Key = key,
                    DisplayName = FriendlyName(key),
                    ArchivePath = archive.Path,
                    Entry = entry,
                    ArchiveNumber = archive.Number,
                    Format = format
                };
                if (!cards.TryGetValue(key, out var existing) || IsPreferred(item, existing))
                    cards[key] = item;
            }
        }

        EnsureWorkspace(progress);
        return cards.Values.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public LoadedCard LoadCard(CardIndexItem item)
    {
        var document = LoadDocument(item);
        if (item.Format == CardSourceFormat.Lua)
        {
            var aggregate = FindContainingAggregates(item.Key).FirstOrDefault();
            if (aggregate is not null)
            {
                var aggregateSource = GetBundleSource(aggregate);
                var cardSource = LuaTableEditor.ExtractCardAsStandalone(aggregateSource, item.Key);
                document = CardDocument.Parse(item.Key, CardSourceFormat.Lua, cardSource);
            }
        }
        if (!string.IsNullOrWhiteSpace(document.Title))
            item.DisplayName = document.Title;
        return new LoadedCard { Index = item, Document = document };
    }

    public LoadedCard LoadStandaloneCard(CardIndexItem item)
    {
        var document = LoadDocument(item);
        if (!string.IsNullOrWhiteSpace(document.Title))
            item.DisplayName = document.Title;
        return new LoadedCard { Index = item, Document = document };
    }

    public BitmapSource? LoadThumbnail(CardIndexItem item)
    {
        _thumbnailGate.Wait();
        try
        {
            var document = LoadDocument(item);
            if (string.IsNullOrWhiteSpace(document.ArtId))
                return null;
            lock (_sync)
                if (_thumbnailCache.TryGetValue(document.ArtId, out var cached))
                    return cached;
            if (!_artEntries.TryGetValue(document.ArtId, out var location))
                return null;
            var bitmap = TdxCodec.DecodeThumbnail(ZedArchive.Extract(location.ArchivePath, location.Entry));
            lock (_sync)
                _thumbnailCache[document.ArtId] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private static CardDocument LoadDocument(CardIndexItem item)
    {
        var bytes = ZedArchive.Extract(item.ArchivePath, item.Entry);
        var format = item.Format;
        string source;
        if (LuaDecompiler.IsCompiled(bytes))
        {
            format = CardSourceFormat.Lua;
            source = LuaDecompiler.Decode(bytes);
        }
        else
        {
            source = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', '\0', ' ', '\r', '\n', '\t');
            format = source.StartsWith('<') ? CardSourceFormat.Xml : CardSourceFormat.Lua;
        }
        return CardDocument.Parse(item.Key, format, source);
    }

    public SaveResult SaveToWorkspace(LoadedCard card, Action<string>? progress = null)
    {
        EnsureWorkspace(progress);
        progress?.Invoke("Validating and saving the card file…");
        var updatedSource = card.Document.ApplyChanges();
        var extension = card.Document.Format == CardSourceFormat.Xml ? ".xml" : ".lol";
        var cardsFolder = Path.Combine(WorkingRoot, "Data_All_Platforms", "Cards");
        Directory.CreateDirectory(cardsFolder);
        string? bundlePath = null;
        var aggregates = card.Document.Format == CardSourceFormat.Lua
            ? FindContainingAggregates(card.Index.Key)
            : [];

        // Deck building reads aggregate tables such as MO_PLAYER_POOL, while an
        // active match loads the standalone card chunk. Both representations must
        // be updated. The byte-preserving compiler keeps the localized T(...)
        // payloads valid, so a compiled DATA_008 override is safe here.
        var cardPath = Path.Combine(cardsFolder, card.Index.Key + extension);
        if (card.Document.Format == CardSourceFormat.Lua)
        {
            var wroteOfficialPatch = false;
            if (aggregates.Count > 0 && FindOfficialStandalone(card.Index.Key) is { } officialBytes)
            {
                var officialSource = LuaDecompiler.Decode(officialBytes);
                if (LuaBytecodePatcher.TryPatchCard(
                        officialBytes, officialSource, card.Document,
                        out var patchedBytes, out var patchMessage))
                {
                    progress?.Invoke($"Patching official bytecode {Path.GetFileName(cardPath)} for matches ({patchMessage})…");
                    File.WriteAllBytes(cardPath, patchedBytes);
                    wroteOfficialPatch = true;
                }
                else
                {
                    progress?.Invoke($"Official bytecode cannot be patched: {patchMessage}");
                }
            }
            if (!wroteOfficialPatch)
            {
                progress?.Invoke($"Compiling standalone match file {Path.GetFileName(cardPath)}…");
                LuaCompiler.CompileToFile(updatedSource, cardPath, Path.GetFileName(cardPath));
            }
        }
        else
        {
            File.WriteAllText(cardPath, updatedSource, new UTF8Encoding(false));
        }

        if (aggregates.Count > 0)
        {
            progress?.Invoke("Finding aggregate set and card-pool records…");
            foreach (var bundle in aggregates)
            {
                progress?.Invoke($"Updating {Path.GetFileName(bundle.Entry.Name)}…");
                var bundleSource = GetBundleSource(bundle);
                var updatedBundle = LuaTableEditor.ReplaceCardObjectInBundle(bundleSource, card.Index.Key, updatedSource);
                LuaDecompiler.Validate(updatedBundle, Path.GetFileName(bundle.Entry.Name));
                var updatedPath = Path.Combine(cardsFolder, Path.GetFileName(bundle.Entry.Name));
                progress?.Invoke($"Compiling {Path.GetFileName(bundle.Entry.Name)} to Lua 5.1…");
                LuaCompiler.CompileToFile(updatedBundle, updatedPath, Path.GetFileName(bundle.Entry.Name));
                bundlePath ??= updatedPath;
                lock (_sync)
                    _bundleSourceCache[BundleCacheKey(bundle)] = updatedBundle;
            }
        }

        string? artPath = null;
        if (!string.IsNullOrWhiteSpace(card.NewArtPath))
        {
            if (string.IsNullOrWhiteSpace(card.Document.ArtId))
                throw new InvalidDataException("The card has no ARTID/art_id, so the texture name cannot be determined.");
            progress?.Invoke("Converting artwork to TDX/DXT1…");
            var artFolder = Path.Combine(WorkingRoot, "Data_All_Platforms", "Art_Assets", "Illustrations");
            artPath = Path.Combine(artFolder, card.Document.ArtId + ".tdx");
            TdxCodec.WriteCardArt(card.NewArtPath, artPath);
        }

        PendingChanges++;
        return new SaveResult(cardPath, bundlePath, artPath);
    }

    public SaveResult RestoreOfficialCardToWorkspace(string cardKey, Action<string>? progress = null)
    {
        EnsureWorkspace(progress);
        var officialBytes = FindOfficialStandalone(cardKey)
            ?? throw new InvalidOperationException($"The official standalone file for {cardKey} was not found in DATA_000 through DATA_007.");
        var officialSource = LuaDecompiler.Decode(officialBytes);
        var officialDocument = CardDocument.Parse(cardKey, CardSourceFormat.Lua, officialSource);
        officialDocument.ValidateAbilities();

        var cardsFolder = Path.Combine(WorkingRoot, "Data_All_Platforms", "Cards");
        Directory.CreateDirectory(cardsFolder);
        var cardPath = Path.Combine(cardsFolder, cardKey + ".lol");
        progress?.Invoke($"Restoring official bytecode {Path.GetFileName(cardPath)}…");
        File.WriteAllBytes(cardPath, officialBytes);

        string? bundlePath = null;
        var aggregates = FindContainingAggregates(cardKey);
        if (aggregates.Count == 0)
            throw new InvalidOperationException($"Card {cardKey} was not found in an aggregate pool.");

        foreach (var bundle in aggregates)
        {
            progress?.Invoke($"Restoring the official record in {Path.GetFileName(bundle.Entry.Name)}…");
            var bundleSource = GetBundleSource(bundle);
            var updatedBundle = LuaTableEditor.ReplaceCardObjectInBundle(bundleSource, cardKey, officialSource);
            LuaDecompiler.Validate(updatedBundle, Path.GetFileName(bundle.Entry.Name));
            var updatedPath = Path.Combine(cardsFolder, Path.GetFileName(bundle.Entry.Name));
            LuaCompiler.CompileToFile(updatedBundle, updatedPath, Path.GetFileName(bundle.Entry.Name));
            bundlePath ??= updatedPath;
            lock (_sync)
                _bundleSourceCache[BundleCacheKey(bundle)] = updatedBundle;
        }

        PendingChanges++;
        progress?.Invoke($"Restored card: {officialDocument.Title}, {officialDocument.ManaCost}, {officialDocument.Power}/{officialDocument.Toughness}.");
        return new SaveResult(cardPath, bundlePath, null);
    }

    private byte[]? FindOfficialStandalone(string cardKey)
    {
        var expectedName = cardKey + ".lol";
        var archives = Directory.EnumerateFiles(GameDirectory, "DATA_*.ZED", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(Data008Path), StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Number = ParseArchiveNumber(path) })
            .Where(item => item.Number >= 0)
            .OrderByDescending(item => item.Number);

        foreach (var archive in archives)
        {
            var entry = ZedArchive.ReadEntries(archive.Path).FirstOrDefault(candidate =>
            {
                var normalized = candidate.Name.Replace('\\', '/');
                return candidate.CompressedSize > 0 &&
                       normalized.Contains("/Data_All_Platforms/Cards/", StringComparison.OrdinalIgnoreCase) &&
                       Path.GetFileName(normalized).Equals(expectedName, StringComparison.OrdinalIgnoreCase);
            });
            if (entry is null)
                continue;
            var bytes = ZedArchive.Extract(archive.Path, entry);
            if (LuaDecompiler.IsCompiled(bytes))
                return bytes;
        }
        return null;
    }

    public int RecompilePlainLuaFiles(Action<string>? progress = null)
    {
        EnsureWorkspace(progress);
        var cardsFolder = Path.Combine(WorkingRoot, "Data_All_Platforms", "Cards");
        if (!Directory.Exists(cardsFolder))
            return 0;

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(cardsFolder, "*.lol", SearchOption.TopDirectoryOnly))
        {
            var bytes = File.ReadAllBytes(path);
            if (LuaDecompiler.IsCompiled(bytes))
                continue;
            var source = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            LuaDecompiler.Validate(source, Path.GetFileName(path));
            progress?.Invoke($"Compiling {Path.GetFileName(path)} to Lua 5.1…");
            LuaCompiler.CompileToFile(source, path, Path.GetFileName(path));
            count++;
        }

        if (count > 0)
            PendingChanges += count;
        return count;
    }

    public InstallResult InstallWorkspace(Action<string>? progress = null)
    {
        if (PendingChanges == 0)
            throw new InvalidOperationException("The working copy has no newly saved changes.");
        if (Process.GetProcessesByName("MagicDuels").Length > 0)
            throw new InvalidOperationException("Close Magic Duels completely first.");
        var currentProcessId = Environment.ProcessId;
        if (Process.GetProcessesByName("MTG-Editor-Utills").Any(process => process.Id != currentProcessId) ||
            Process.GetProcessesByName("UrukCardEditor").Any(process => process.Id != currentProcessId))
            throw new InvalidOperationException("Close all other running instances of MTG-Editor-Utills first.");

        Directory.CreateDirectory(BackupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(BackupDirectory, $"DATA_008.before-{stamp}.ZED");
        var dataBinPath = Path.Combine(SessionDirectory, "Data.bin");
        var outputPath = Path.Combine(SessionDirectory, "DATA_008.new.ZED");

        progress?.Invoke("Creating a backup of DATA_008.ZED…");
        File.Copy(Data008Path, backupPath, overwrite: false);
        global::ZedBuilder.CreateDataBin(Data008Path, dataBinPath);
        progress?.Invoke("Building a new DATA_008.ZED…");
        global::ZedBuilder.CreateZed(dataBinPath, WorkingRoot, outputPath);

        progress?.Invoke("Verifying the new archive contents…");
        var sourceFiles = Directory.EnumerateFiles(WorkingRoot, "*", SearchOption.AllDirectories).Count();
        var packedFiles = ZedArchive.ReadEntries(outputPath).Count(e =>
            e.CompressedSize > 0 && e.Name.StartsWith("DATA_008/", StringComparison.OrdinalIgnoreCase));
        if (sourceFiles != packedFiles)
            throw new InvalidDataException($"Archive validation failed: {sourceFiles} source files, {packedFiles} packed files.");

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath)));
        progress?.Invoke("Installing the validated archive…");
        File.Move(outputPath, Data008Path, overwrite: true);
        PendingChanges = 0;
        return new InstallResult(Data008Path, backupPath, hash, packedFiles);
    }

    private IReadOnlyList<BundleLocation> FindContainingAggregates(string cardKey)
    {
        var needle = Encoding.UTF8.GetBytes(cardKey);
        var matches = new List<BundleLocation>();
        foreach (var bundle in _aggregateCardFiles.OrderByDescending(b => b.ArchiveNumber))
        {
            var bytes = ZedArchive.Extract(bundle.ArchivePath, bundle.Entry);
            if (bytes.AsSpan().IndexOf(needle) >= 0)
                matches.Add(bundle);
        }
        return matches;
    }

    private string GetBundleSource(BundleLocation bundle)
    {
        var key = BundleCacheKey(bundle);
        lock (_sync)
            if (_bundleSourceCache.TryGetValue(key, out var cached))
                return cached;
        var source = LuaDecompiler.Decode(ZedArchive.Extract(bundle.ArchivePath, bundle.Entry));
        lock (_sync)
            _bundleSourceCache[key] = source;
        return source;
    }

    private void EnsureWorkspace(Action<string>? progress)
    {
        if (Directory.Exists(WorkingRoot))
            return;
        progress?.Invoke("Creating a working copy of DATA_008…");
        Directory.CreateDirectory(SessionDirectory);
        ZedArchive.ExtractAll(Data008Path, SessionDirectory);
        if (!Directory.Exists(WorkingRoot))
            throw new InvalidDataException("The DATA_008 archive did not create the expected working directory.");
    }

    private static bool IsPreferred(CardIndexItem candidate, CardIndexItem existing) =>
        candidate.ArchiveNumber > existing.ArchiveNumber ||
        candidate.ArchiveNumber == existing.ArchiveNumber && candidate.Format == CardSourceFormat.Lua && existing.Format == CardSourceFormat.Xml;

    private static int ParseArchiveNumber(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"DATA_(\d{3})\.ZED$", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static string FriendlyName(string key)
    {
        var withoutId = Regex.Replace(key, @"_\d+$", "");
        return string.Join(' ', withoutId.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }

    private static string BundleCacheKey(BundleLocation bundle) => bundle.ArchivePath + "|" + bundle.Entry.Name;

    public static string LocateGameDirectory()
    {
        var candidates = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DATA_000.ZED")) &&
                    File.Exists(Path.Combine(directory.FullName, "DATA_008.ZED")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not find a Magic Duels directory containing DATA_000.ZED and DATA_008.ZED.");
    }

    public static bool IsGameDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "DATA_000.ZED")) &&
        File.Exists(Path.Combine(path, "DATA_008.ZED"));
}

public sealed record SaveResult(string CardPath, string? BundlePath, string? ArtPath);
public sealed record InstallResult(string InstalledPath, string BackupPath, string Sha256, int FileCount);
