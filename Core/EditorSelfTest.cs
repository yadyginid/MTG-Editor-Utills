using System.Text;
using System.Text.RegularExpressions;

namespace UrukCardEditor.Core;

public static class EditorSelfTest
{
    public static void Run(string logPath)
    {
        var log = new StringBuilder();
        var repository = new GameRepository();
        log.AppendLine("Game: " + repository.GameDirectory);
        var catalog = repository.BuildCatalog(message => log.AppendLine(message));
        if (catalog.Count == 0)
            throw new InvalidOperationException("The card catalog is empty.");
        log.AppendLine("Cards: " + catalog.Count);

        var sample = catalog.FirstOrDefault(c => c.Key.Equals("FESTERING_MUMMY_395329", StringComparison.OrdinalIgnoreCase))
            ?? catalog.First();
        var loaded = repository.LoadCard(sample);
        log.AppendLine($"Loaded: {loaded.Document.Title} ({loaded.Index.Key})");
        var thumbnail = repository.LoadThumbnail(sample)
            ?? throw new InvalidOperationException("The sample card thumbnail could not be decoded.");
        log.AppendLine($"Thumbnail: OK ({thumbnail.PixelWidth}x{thumbnail.PixelHeight})");
        if (loaded.Document.Format == CardSourceFormat.Lua)
        {
            loaded.Document.AbilitiesText = AbilityTemplates.AddDeathDamage(CardSourceFormat.Lua, loaded.Document.AbilitiesText);
            var once = loaded.Document.AbilitiesText;
            loaded.Document.AbilitiesText = AbilityTemplates.AddDeathDamage(CardSourceFormat.Lua, loaded.Document.AbilitiesText);
            if (!string.Equals(once, loaded.Document.AbilitiesText, StringComparison.Ordinal))
                throw new InvalidOperationException("The same Lua trigger was appended twice.");
            loaded.Document.ValidateAbilities();
            log.AppendLine("Lua ability template and duplicate guard: OK");
        }
        else
        {
            loaded.Document.AbilitiesText = AbilityTemplates.AddDeathDamage(CardSourceFormat.Xml, loaded.Document.AbilitiesText);
            loaded.Document.ValidateAbilities();
            log.AppendLine("XML ability template: OK");
        }
        var testArt = Path.Combine(repository.GameDirectory, "UrukSoldierMod", "assets", "uruk-soldier.png");
        if (File.Exists(testArt))
            loaded.NewArtPath = testArt;
        var saved = repository.SaveToWorkspace(loaded, message => log.AppendLine(message));
        if (!File.Exists(saved.CardPath))
            throw new InvalidOperationException("The staged card file was not created.");
        if (loaded.Document.Format == CardSourceFormat.Lua)
        {
            var standaloneBytes = File.ReadAllBytes(saved.CardPath);
            if (!LuaDecompiler.IsCompiled(standaloneBytes))
                throw new InvalidOperationException("The staged standalone card was not compiled.");
            LuaDecompiler.Validate(LuaDecompiler.Decode(standaloneBytes), Path.GetFileName(saved.CardPath));
            log.AppendLine("Standalone match card: compiled");
        }
        log.AppendLine("Workspace save: OK");
        log.AppendLine("Bundle: " + (saved.BundlePath ?? "not found"));
        log.AppendLine("Art: " + (saved.ArtPath ?? "not requested"));

        var xmlItem = catalog.FirstOrDefault(c => c.Format == CardSourceFormat.Xml);
        if (xmlItem is not null)
        {
            var xmlCard = repository.LoadCard(xmlItem);
            xmlCard.Document.AbilitiesText = AbilityTemplates.AddDeathMinusCounter(CardSourceFormat.Xml, xmlCard.Document.AbilitiesText);
            xmlCard.Document.ValidateAbilities();
            var xmlSaved = repository.SaveToWorkspace(xmlCard, message => log.AppendLine(message));
            if (!File.Exists(xmlSaved.CardPath))
                throw new InvalidOperationException("The staged XML card file was not created.");
            log.AppendLine("XML workspace save: OK (" + xmlItem.Key + ")");
        }

        var bloodItem = catalog.FirstOrDefault(c => c.Key.Equals("BLOODCURSED_KNIGHT_386055", StringComparison.OrdinalIgnoreCase));
        if (bloodItem is not null)
        {
            var blood = repository.LoadCard(bloodItem);
            var damageTriggerCount = Regex.Matches(
                blood.Document.AbilitiesText, "CARD_QUERY_CHOOSE_DEAL_1_DAMAGE", RegexOptions.CultureInvariant).Count;
            var counterTriggerCount = Regex.Matches(
                blood.Document.AbilitiesText, "CARD_QUERY_CHOOSE_CREATURE_TO_GET_MINUS1_MINUS1_COUNTER", RegexOptions.CultureInvariant).Count;
            log.AppendLine($"Blood-Cursed Knight: title={blood.Document.Title}; cost={blood.Document.ManaCost}; power={blood.Document.Power}; toughness={blood.Document.Toughness}; damage triggers={damageTriggerCount}; counter triggers={counterTriggerCount}");
            var bloodSaved = repository.SaveToWorkspace(blood, message => log.AppendLine(message));
            if (!LuaDecompiler.IsCompiled(File.ReadAllBytes(bloodSaved.CardPath)))
                throw new InvalidOperationException("Blood-Cursed Knight standalone match card was not compiled.");
            if (bloodSaved.BundlePath is null || !Path.GetFileName(bloodSaved.BundlePath).Equals("MO_PLAYER_POOL.lol", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Blood-Cursed Knight was not propagated to MO_PLAYER_POOL.lol.");
            var poolBytes = File.ReadAllBytes(bloodSaved.BundlePath);
            if (!LuaDecompiler.IsCompiled(poolBytes))
                throw new InvalidOperationException("The updated MO_PLAYER_POOL.lol was not compiled.");
            var poolSource = LuaDecompiler.Decode(poolBytes);
            if (!poolSource.Contains(blood.Document.Title, StringComparison.Ordinal))
                throw new InvalidOperationException("The updated Blood-Cursed Knight title is missing from MO_PLAYER_POOL.lol.");
            LuaDecompiler.Validate(poolSource, "MO_PLAYER_POOL.lol");
            log.AppendLine("MO_PLAYER_POOL compiled propagation: OK");

            var restoredBlood = repository.RestoreOfficialCardToWorkspace(blood.Index.Key, message => log.AppendLine(message));
            var restoredSource = LuaDecompiler.Decode(File.ReadAllBytes(restoredBlood.CardPath));
            var restoredDocument = CardDocument.Parse(blood.Index.Key, CardSourceFormat.Lua, restoredSource);
            if (restoredDocument.AbilitiesText.Contains("CARD_QUERY_CHOOSE_DEAL_1_DAMAGE", StringComparison.Ordinal) ||
                restoredDocument.AbilitiesText.Contains("CARD_QUERY_CHOOSE_CREATURE_TO_GET_MINUS1_MINUS1_COUNTER", StringComparison.Ordinal))
                throw new InvalidOperationException("The official Blood-Cursed Knight restore retained custom death triggers.");
            log.AppendLine($"Official Blood-Cursed Knight restore: OK ({restoredDocument.ManaCost}, {restoredDocument.Power}/{restoredDocument.Toughness})");
        }

        var corpseItem = catalog.FirstOrDefault(c => c.Key.Equals("CORPSE_HAULER_393498", StringComparison.OrdinalIgnoreCase));
        if (corpseItem is not null)
        {
            var corpseStandalone = repository.LoadStandaloneCard(corpseItem);
            var corpseSaved = repository.SaveToWorkspace(corpseStandalone, message => log.AppendLine(message));
            if (corpseSaved.BundlePath is null)
                throw new InvalidOperationException("Corpse Hauler was not propagated to an aggregate card pool.");
            log.AppendLine($"Corpse Hauler aggregate propagation: OK ({corpseStandalone.Document.Power}/{corpseStandalone.Document.Toughness}; {Path.GetFileName(corpseSaved.BundlePath)})");
        }

        var dataBin = Path.Combine(repository.SessionDirectory, "self-test-Data.bin");
        var archive = Path.Combine(repository.SessionDirectory, "self-test-DATA_008.ZED");
        global::ZedBuilder.CreateDataBin(repository.Data008Path, dataBin);
        global::ZedBuilder.CreateZed(dataBin, repository.WorkingRoot, archive);
        var sourceCount = Directory.EnumerateFiles(repository.WorkingRoot, "*", SearchOption.AllDirectories).Count();
        var packedCount = ZedArchive.ReadEntries(archive).Count(e =>
            e.CompressedSize > 0 && e.Name.StartsWith("DATA_008/", StringComparison.OrdinalIgnoreCase));
        if (sourceCount != packedCount)
            throw new InvalidOperationException($"Archive file count mismatch: {sourceCount} != {packedCount}.");
        log.AppendLine($"Archive build: OK ({packedCount} files)");
        File.WriteAllText(logPath, log.ToString());
    }
}
