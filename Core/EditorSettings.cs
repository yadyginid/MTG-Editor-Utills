using System.Text.Json;

namespace UrukCardEditor.Core;

public static class EditorSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MTG-Editor-Utills");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static string? LoadGameDirectory()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return null;
            var settings = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(SettingsPath));
            return string.IsNullOrWhiteSpace(settings?.GameDirectory) ? null : settings.GameDirectory;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveGameDirectory(string gameDirectory)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var settings = new SettingsModel { GameDirectory = Path.GetFullPath(gameDirectory) };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class SettingsModel
    {
        public string? GameDirectory { get; set; }
    }
}
