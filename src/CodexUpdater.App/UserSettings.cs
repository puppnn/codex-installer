using System.IO;
using System.Text.Json;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal sealed record UserSettings(string DownloadDirectory, DownloadRoute DownloadRoute = DownloadRoute.Auto)
{
    private static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUpdater");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return Default();
            }

            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
            if (settings is null || string.IsNullOrWhiteSpace(settings.DownloadDirectory))
            {
                return Default();
            }

            return Enum.IsDefined(settings.DownloadRoute) ? settings : settings with { DownloadRoute = DownloadRoute.Auto };
        }
        catch
        {
            return Default();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(
            SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static UserSettings Default()
    {
        return new UserSettings(DownloadPaths.DefaultDownloadsDirectory);
    }
}
