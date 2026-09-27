namespace PIM.Web.Services;

/// <summary>
/// Folder holding the user's mutable settings files (library-settings.json and
/// cleanup-settings.json). Defaults to %LOCALAPPDATA%\Plex Integrity Manager.
/// PIM:UserSettingsDirectory (for example the PIM__UserSettingsDirectory
/// environment variable) moves it, so a development instance can run against
/// an isolated folder without reading or changing the owner's real settings.
/// </summary>
public static class UserSettingsLocation
{
    public const string ConfigurationKey = "PIM:UserSettingsDirectory";

    public static string GetDirectory(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey]?.Trim();

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Plex Integrity Manager")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }
}
