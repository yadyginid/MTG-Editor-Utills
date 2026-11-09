using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UrukCardEditor.Core;

namespace Adding2013VersionCards;

public sealed record ImportResult(
    int RegisteredCards,
    int FirstId,
    int LastId,
    int LastUnlockableId,
    string InstalledArchive,
    string? ArchiveBackup,
    string SessionDirectory,
    ProfilePatchResult? ProfileResult);

public sealed record SourceValidationResult(
    int TotalCards,
    int CollectibleCards,
    int Tokens,
    int FirstId,
    int LastId);

public static class Magic2013Importer
{
    private const string DefinitionBundleName = "MO_SHOEBOX_CARD_POOL.lol";
    private const string RegistrationName = "M13_IMPORTED_CARD_POOL.lol";

    public static SourceValidationResult Validate(ImportOptions options, Action<string> log)
    {
        _ = ValidateGameDirectory(options.GameDirectory);
        var (cardsDirectory, _) = FindSourceLayout(options.SourceDirectory);
        var cards = Directory.EnumerateFiles(cardsDirectory, "*.xml")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Magic2013CardConverter.Read)
            .ToList();
        var collectible = cards.Count(card => !card.IsToken);
        if (collectible == 0)
            throw new InvalidDataException("The source contains no collectible cards.");
        var lastId = checked(options.StartId + collectible - 1);
        var source = Magic2013CardConverter.BuildRegistration(cards, options.StartId);
        LuaDecompiler.Validate(source, RegistrationName);

        var validationRoot = Path.Combine(Path.GetTempPath(), "Adding2013VersionCards", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationRoot);
        try
        {
            var compiledPath = Path.Combine(validationRoot, RegistrationName);
            LuaCompiler.CompileToFile(source, compiledPath, RegistrationName);
            var roundTrip = LuaDecompiler.Decode(File.ReadAllBytes(compiledPath));
            var roundTripCount = roundTrip.Split('\n').Count(line => line.TrimStart().StartsWith("name = ", StringComparison.Ordinal));
            if (roundTripCount != collectible || !roundTrip.Contains($"id = \"{lastId}\"", StringComparison.Ordinal))
                throw new InvalidDataException("Compiled registration failed round-trip validation.");

            var modulesDirectory = Path.Combine(validationRoot, "Cards");
            Directory.CreateDirectory(modulesDirectory);
            foreach (var card in cards)
            {
                var module = Magic2013CardConverter.BuildStandaloneModule(card.Xml);
                LuaDecompiler.Validate(module, card.FileName + ".lol");
                LuaCompiler.CompileToFile(
                    module,
                    Path.Combine(modulesDirectory, card.FileName + ".lol"),
                    card.FileName + ".lol");
            }
        }
        finally
        {
            if (Directory.Exists(validationRoot))
                Directory.Delete(validationRoot, recursive: true);
        }
        log($"Validated and compiled {cards.Count} standalone card modules and {collectible} collectible registrations.");
        return new SourceValidationResult(cards.Count, collectible, cards.Count - collectible, options.StartId, lastId);
    }

    public static ImportResult Run(ImportOptions options, Action<string> log)
    {
        EnsureApplicationsAreClosed();
        var gameDirectory = ValidateGameDirectory(options.GameDirectory);
        var (sourceCardsDirectory, sourceArtDirectory) = FindSourceLayout(options.SourceDirectory);
        var sourceCards = Directory.EnumerateFiles(sourceCardsDirectory, "*.xml")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Magic2013CardConverter.Read)
            .ToList();
        if (sourceCards.Count == 0)
            throw new InvalidDataException("The source contains no CARD_V2 XML files.");
        var collectibleCards = sourceCards.Where(card => !card.IsToken).ToList();
        if (collectibleCards.Count == 0)
            throw new InvalidDataException("The source contains no collectible cards.");

        var lastId = checked(options.StartId + collectibleCards.Count - 1);
        var lastUnlockableId = Math.Min(ProfileCodec.CardCapacity - 1, lastId);
        log($"Source cards: {sourceCards.Count} ({collectibleCards.Count} collectible, {sourceCards.Count - collectibleCards.Count} tokens)");
        log($"Registration IDs: {options.StartId}..{lastId}");
        log($"Profile-visible IDs: {options.StartId}..{lastUnlockableId}");

        var archives = Directory.EnumerateFiles(gameDirectory, "DATA_*.ZED")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (archives.Count == 0)
            throw new FileNotFoundException("No DATA_*.ZED archives were found in the selected game directory.");
        var targetArchive = Path.Combine(gameDirectory, "DATA_008.ZED");
        var seedArchive = File.Exists(targetArchive) ? targetArchive : archives[^1];

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dataRoot = Path.Combine(gameDirectory, "UrukCardEditorData");
        var sessionDirectory = Path.Combine(dataRoot, "Sessions", $"Adding2013VersionCards-{stamp}");
        var workingRoot = Path.Combine(sessionDirectory, "DATA_008");
        Directory.CreateDirectory(sessionDirectory);
        if (File.Exists(targetArchive))
        {
            log("Extracting the current DATA_008.ZED working copy...");
            ZedArchive.ExtractAll(targetArchive, sessionDirectory);
        }
        Directory.CreateDirectory(workingRoot);

        var targetCardsDirectory = Path.Combine(workingRoot, "Data_All_Platforms", "Cards");
        var targetArtDirectory = Path.Combine(workingRoot, "Data_All_Platforms", "Art_Assets", "Illustrations");
        var targetPoolsDirectory = Path.Combine(workingRoot, "Data_All_Platforms", "CardPools");
        Directory.CreateDirectory(targetCardsDirectory);
        Directory.CreateDirectory(targetArtDirectory);
        Directory.CreateDirectory(targetPoolsDirectory);

        log("Copying CARD_V2 XML files...");
        var xmlSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            NewLineChars = "\r\n",
            OmitXmlDeclaration = false
        };
        foreach (var sourceCard in sourceCards)
        {
            var card = new XElement(sourceCard.Xml);
            card.SetAttributeValue("ExportVersion", "1");
            SetValueElement(card, "SETID", "value", "DPM_SBX");
            SetValueElement(card, "EXPANSION", "value", "DPM_SBX");
            var destination = Path.Combine(targetCardsDirectory, sourceCard.FileName + ".xml");
            using var writer = XmlWriter.Create(destination, xmlSettings);
            new XDocument(new XDeclaration("1.0", "utf-8", null), card).Save(writer);
        }

        log("Compiling standalone LOL modules used by the duel engine...");
        foreach (var sourceCard in sourceCards)
        {
            var module = Magic2013CardConverter.BuildStandaloneModule(sourceCard.Xml);
            LuaDecompiler.Validate(module, sourceCard.FileName + ".lol");
            LuaCompiler.CompileToFile(
                module,
                Path.Combine(targetCardsDirectory, sourceCard.FileName + ".lol"),
                sourceCard.FileName + ".lol");
        }
        log($"Standalone duel modules compiled: {sourceCards.Count}");

        log("Copying TDX illustrations...");
        var artCount = 0;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceArtDirectory, "*.tdx")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, Path.Combine(targetArtDirectory, Path.GetFileName(sourcePath)), overwrite: true);
            artCount++;
        }

        var definitionBundlePath = Path.Combine(targetCardsDirectory, DefinitionBundleName);
        if (!File.Exists(definitionBundlePath))
        {
            log("Extracting the active MO card-definition bundle...");
            ExtractLatestEntry(archives, $"/Data_All_Platforms/Cards/{DefinitionBundleName}", definitionBundlePath);
        }
        log("Updating the compiled card-definition bundle...");
        var bundleSource = LuaDecompiler.Decode(File.ReadAllBytes(definitionBundlePath));
        var missingObjects = sourceCards
            .Where(card => !bundleSource.Contains($"file_name = \"{card.FileName}\"", StringComparison.Ordinal))
            .Select(card => Magic2013CardConverter.BuildCardObject(card.Xml))
            .ToList();
        if (missingObjects.Count > 0)
        {
            var cardsField = LuaTableEditor.FindField(bundleSource, "cards");
            var prefix = bundleSource[..cardsField.BraceEnd].TrimEnd();
            var separator = prefix.EndsWith('{') ? "\n" : ",\n";
            var updatedBundle = prefix + separator + string.Join(",\n", missingObjects) + "\n" + bundleSource[cardsField.BraceEnd..];
            LuaDecompiler.Validate(updatedBundle, DefinitionBundleName);
            LuaCompiler.CompileToFile(updatedBundle, definitionBundlePath, DefinitionBundleName);
        }
        log($"Card definitions added to bundle: {missingObjects.Count}");

        log("Creating explicit CardPools registration...");
        var registrationSource = Magic2013CardConverter.BuildRegistration(sourceCards, options.StartId);
        LuaDecompiler.Validate(registrationSource, RegistrationName);
        var registrationSourcePath = Path.Combine(sessionDirectory, "M13_IMPORTED_CARD_POOL.lua");
        File.WriteAllText(registrationSourcePath, registrationSource, new UTF8Encoding(false));
        var registrationPath = Path.Combine(targetPoolsDirectory, RegistrationName);
        LuaCompiler.CompileToFile(registrationSource, registrationPath, RegistrationName);

        var dataBinPath = Path.Combine(sessionDirectory, "DATA_008.import.bin");
        var builtArchivePath = Path.Combine(sessionDirectory, "DATA_008.imported.ZED");
        log("Building DATA_008.ZED...");
        global::ZedBuilder.CreateDataBin(seedArchive, dataBinPath);
        global::ZedBuilder.CreateZed(dataBinPath, workingRoot, builtArchivePath);
        VerifyArchive(builtArchivePath, sourceCards, collectibleCards.Count, lastId);

        var backupDirectory = Path.Combine(dataRoot, "Backups");
        Directory.CreateDirectory(backupDirectory);
        string? archiveBackup = null;
        if (File.Exists(targetArchive))
        {
            archiveBackup = Path.Combine(backupDirectory, $"DATA_008.before-M13-import-{stamp}.ZED");
            File.Copy(targetArchive, archiveBackup, overwrite: false);
        }
        InstallArchive(builtArchivePath, targetArchive);
        log($"Installed archive SHA-256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targetArchive)))}");

        ProfilePatchResult? profileResult = null;
        if (!options.SkipProfile)
        {
            var profilePath = ProfilePatcher.LocateProfile(options.ProfilePath, log);
            if (profilePath is not null)
            {
                log("Patching the 2048-slot profile unlock table...");
                profileResult = ProfilePatcher.Patch(
                    profilePath,
                    options.StartId,
                    lastUnlockableId,
                    options.Quantity,
                    Path.Combine(backupDirectory, "Profiles"));
                log($"Profile cards changed: {profileResult.ChangedCards}");
            }
        }

        var manifestPath = Path.Combine(sessionDirectory, "import-manifest.txt");
        File.WriteAllLines(manifestPath,
        [
            $"Source: {options.SourceDirectory}",
            $"Game: {gameDirectory}",
            $"CARD_V2 XML files: {sourceCards.Count}",
            $"Standalone LOL duel modules: {sourceCards.Count}",
            $"Collectible registrations: {collectibleCards.Count}",
            $"Token definitions (not registered): {sourceCards.Count - collectibleCards.Count}",
            $"TDX illustrations: {artCount}",
            $"Registered IDs: {options.StartId}..{lastId}",
            $"Profile-visible IDs: {options.StartId}..{lastUnlockableId}",
            $"Installed archive: {targetArchive}",
            $"Archive backup: {archiveBackup ?? "none"}",
            $"Profile: {profileResult?.ProfilePath ?? "not patched"}",
            $"Profile backup: {profileResult?.ProfileBackup ?? "none"}"
        ], new UTF8Encoding(false));

        return new ImportResult(
            collectibleCards.Count,
            options.StartId,
            lastId,
            lastUnlockableId,
            targetArchive,
            archiveBackup,
            sessionDirectory,
            profileResult);
    }

    private static void EnsureApplicationsAreClosed()
    {
        var running = Process.GetProcessesByName("MagicDuels")
            .Concat(Process.GetProcessesByName("MTG-Editor-Utills"))
            .ToList();
        if (running.Count != 0)
            throw new InvalidOperationException("Close Magic Duels and MTG-Editor-Utills before importing cards.");
    }

    private static string ValidateGameDirectory(string path)
    {
        var gameDirectory = Path.GetFullPath(path);
        if (!Directory.Exists(gameDirectory))
            throw new DirectoryNotFoundException(gameDirectory);
        if (!File.Exists(Path.Combine(gameDirectory, "MagicDuels.exe")))
            throw new InvalidDataException("The selected folder does not contain MagicDuels.exe.");
        return gameDirectory;
    }

    private static (string Cards, string Art) FindSourceLayout(string sourceRoot)
    {
        var candidates = new[]
        {
            (Path.Combine(sourceRoot, "Cards"), Path.Combine(sourceRoot, "Illustrations")),
            (Path.Combine(sourceRoot, "Data_All_Platforms", "Cards"),
                Path.Combine(sourceRoot, "Data_All_Platforms", "Art_Assets", "Illustrations"))
        };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate.Item1) && Directory.Exists(candidate.Item2))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            "The source must contain Cards + Illustrations, or Data_All_Platforms/Cards + Data_All_Platforms/Art_Assets/Illustrations.");
    }

    private static void SetValueElement(XElement card, string elementName, string attributeName, string value)
    {
        var element = card.Element(elementName);
        if (element is null)
        {
            element = new XElement(elementName);
            card.Add(element);
        }
        element.SetAttributeValue(attributeName, value);
    }

    private static void ExtractLatestEntry(
        IReadOnlyList<string> archives,
        string entrySuffix,
        string destination)
    {
        foreach (var archivePath in archives.Reverse())
        {
            var entry = ZedArchive.ReadEntries(archivePath).FirstOrDefault(candidate =>
                candidate.Name.Replace('\\', '/').EndsWith(entrySuffix, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, ZedArchive.Extract(archivePath, entry));
            return;
        }
        throw new FileNotFoundException($"Active archive entry was not found: {entrySuffix}");
    }

    private static void VerifyArchive(
        string archivePath,
        IReadOnlyList<SourceCard> sourceCards,
        int expectedCards,
        int expectedLastId)
    {
        var entries = ZedArchive.ReadEntries(archivePath);
        var entry = entries.FirstOrDefault(candidate =>
            candidate.Name.Replace('\\', '/').EndsWith($"/CardPools/{RegistrationName}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The built archive has no Magic 2013 registration pool.");
        var source = LuaDecompiler.Decode(ZedArchive.Extract(archivePath, entry));
        var registeredCards = source.Split('\n').Count(line => line.TrimStart().StartsWith("name = ", StringComparison.Ordinal));
        if (registeredCards != expectedCards || !source.Contains($"id = \"{expectedLastId}\"", StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Registration verification failed: expected {expectedCards} cards ending at ID {expectedLastId}.");

        var moduleNames = new HashSet<string>(
            entries.Where(candidate => candidate.Name.Replace('\\', '/').Contains("/Data_All_Platforms/Cards/", StringComparison.OrdinalIgnoreCase))
                .Where(candidate => candidate.Name.EndsWith(".lol", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => Path.GetFileNameWithoutExtension(candidate.Name)),
            StringComparer.OrdinalIgnoreCase);
        var missingModules = sourceCards.Where(card => !moduleNames.Contains(card.FileName)).Select(card => card.FileName).Take(10).ToList();
        if (missingModules.Count > 0)
            throw new InvalidDataException(
                "The built archive is missing standalone duel modules: " + string.Join(", ", missingModules));
    }

    private static void InstallArchive(string sourcePath, string targetPath)
    {
        var temporaryPath = targetPath + ".m13-import-new";
        File.Copy(sourcePath, temporaryPath, overwrite: true);
        var expectedHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
        var temporaryHash = SHA256.HashData(File.ReadAllBytes(temporaryPath));
        if (!expectedHash.AsSpan().SequenceEqual(temporaryHash))
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException("Temporary DATA_008.ZED hash mismatch.");
        }
        File.Move(temporaryPath, targetPath, overwrite: true);
    }
}
