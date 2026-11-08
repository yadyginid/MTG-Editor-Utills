using System.IO;
using System.Windows;
using UrukCardEditor.Core;

namespace UrukCardEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 3 && e.Args[0].Equals("--compile-lua", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var source = File.ReadAllText(e.Args[1], System.Text.Encoding.UTF8);
                LuaCompiler.CompileToFile(source, e.Args[2], Path.GetFileName(e.Args[2]));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "compile-lua.log"), ex.ToString());
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--recompile-lua", StringComparer.OrdinalIgnoreCase))
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "recompile-lua.log");
            try
            {
                var repository = new GameRepository();
                var log = new List<string>();
                _ = repository.BuildCatalog(message => log.Add(message));
                var count = repository.RecompilePlainLuaFiles(message => log.Add(message));
                log.Add($"Скомпилировано Lua-файлов: {count}");
                if (count == 0)
                    throw new InvalidOperationException("В DATA_008 нет текстовых Lua-файлов для перекомпиляции.");
                var installed = repository.InstallWorkspace(message => log.Add(message));
                log.Add("Установлено: " + installed.InstalledPath);
                log.Add("Резервная копия: " + installed.BackupPath);
                log.Add("SHA-256: " + installed.Sha256);
                log.Add("Файлов в архиве: " + installed.FileCount);
                File.WriteAllLines(logPath, log);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, ex.ToString());
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        var repairIndex = Array.FindIndex(e.Args, arg =>
            arg.Equals("--repair-card", StringComparison.OrdinalIgnoreCase));
        if (repairIndex >= 0)
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "repair-card.log");
            try
            {
                if (repairIndex + 1 >= e.Args.Length || string.IsNullOrWhiteSpace(e.Args[repairIndex + 1]))
                    throw new ArgumentException("После --repair-card требуется внутренний ID карты.");

                var cardKey = e.Args[repairIndex + 1];
                var repository = new GameRepository();
                var log = new List<string>();
                var catalog = repository.BuildCatalog(message => log.Add(message));
                var item = catalog.FirstOrDefault(candidate =>
                    candidate.Key.Equals(cardKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Карта {cardKey} не найдена.");
                var card = repository.LoadCard(item);
                log.Add($"Загружена карта: {card.Document.Title} ({card.Index.Key})");
                card.Document.AbilitiesText = AbilityTemplates.RemoveDuplicateKnownTriggers(
                    card.Document.Format, card.Document.AbilitiesText, out var removedDuplicates);
                log.Add($"Удалено повторяющихся триггеров: {removedDuplicates}");
                var saved = repository.SaveToWorkspace(card, message => log.Add(message));
                log.Add("Файл карты: " + saved.CardPath);
                log.Add("Общий пул: " + (saved.BundlePath ?? "не найден"));
                if (saved.BundlePath is null)
                    throw new InvalidOperationException("Запись карты не найдена ни в одном общем пуле.");
                var installed = repository.InstallWorkspace(message => log.Add(message));
                log.Add("Установлено: " + installed.InstalledPath);
                log.Add("Резервная копия: " + installed.BackupPath);
                log.Add("SHA-256: " + installed.Sha256);
                log.Add("Файлов в архиве: " + installed.FileCount);
                File.WriteAllLines(logPath, log);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, ex.ToString());
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        var restoreIndex = Array.FindIndex(e.Args, arg =>
            arg.Equals("--restore-official-card", StringComparison.OrdinalIgnoreCase));
        if (restoreIndex >= 0)
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "restore-official-card.log");
            try
            {
                if (restoreIndex + 1 >= e.Args.Length || string.IsNullOrWhiteSpace(e.Args[restoreIndex + 1]))
                    throw new ArgumentException("После --restore-official-card требуется внутренний ID карты.");

                var cardKey = e.Args[restoreIndex + 1];
                var repository = new GameRepository();
                var log = new List<string>();
                _ = repository.BuildCatalog(message => log.Add(message));
                var saved = repository.RestoreOfficialCardToWorkspace(cardKey, message => log.Add(message));
                log.Add("Файл карты: " + saved.CardPath);
                log.Add("Общий пул: " + (saved.BundlePath ?? "не найден"));
                var installed = repository.InstallWorkspace(message => log.Add(message));
                log.Add("Установлено: " + installed.InstalledPath);
                log.Add("Резервная копия: " + installed.BackupPath);
                log.Add("SHA-256: " + installed.Sha256);
                log.Add("Файлов в архиве: " + installed.FileCount);
                File.WriteAllLines(logPath, log);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, ex.ToString());
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        var propagateIndex = Array.FindIndex(e.Args, arg =>
            arg.Equals("--propagate-standalone-card", StringComparison.OrdinalIgnoreCase));
        if (propagateIndex >= 0)
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "propagate-standalone-card.log");
            try
            {
                if (propagateIndex + 1 >= e.Args.Length || string.IsNullOrWhiteSpace(e.Args[propagateIndex + 1]))
                    throw new ArgumentException("После --propagate-standalone-card требуется внутренний ID карты.");

                var cardKey = e.Args[propagateIndex + 1];
                var repository = new GameRepository();
                var log = new List<string>();
                var catalog = repository.BuildCatalog(message => log.Add(message));
                var item = catalog.FirstOrDefault(candidate =>
                    candidate.Key.Equals(cardKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Карта {cardKey} не найдена.");
                var card = repository.LoadStandaloneCard(item);
                log.Add($"Автономная карта: {card.Document.Title}; стоимость {card.Document.ManaCost}; сила/выносливость {card.Document.Power}/{card.Document.Toughness}.");
                var saved = repository.SaveToWorkspace(card, message => log.Add(message));
                log.Add("Файл карты: " + saved.CardPath);
                log.Add("Общий пул: " + (saved.BundlePath ?? "не найден"));
                if (saved.BundlePath is null)
                    throw new InvalidOperationException("Не найден общий пул, содержащий эту карту.");
                var installed = repository.InstallWorkspace(message => log.Add(message));
                log.Add("Установлено: " + installed.InstalledPath);
                log.Add("Резервная копия: " + installed.BackupPath);
                log.Add("SHA-256: " + installed.Sha256);
                log.Add("Файлов в архиве: " + installed.FileCount);
                File.WriteAllLines(logPath, log);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, ex.ToString());
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "self-test.log");
            try
            {
                EditorSelfTest.Run(logPath);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, ex.ToString());
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        base.OnStartup(e);
    }
}
