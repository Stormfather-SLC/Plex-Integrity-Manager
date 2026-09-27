namespace PIM.Core.Models;

/// <summary>
/// Shared interpretation of Plex configuration, so the conflict check and the
/// setup warnings on the page always agree.
/// </summary>
public static class PlexSettings
{
    public const string EnabledKey = "Plex:Enabled";

    /// <summary>
    /// The Plex library check is a safety boundary, so it is on unless
    /// Plex:Enabled is explicitly "false". A missing or unreadable value keeps
    /// it on; if Plex is then unreachable or the token is missing, every item
    /// fails closed instead of silently skipping the check.
    /// </summary>
    public static bool IsLibraryCheckEnabled(string? enabledValue)
    {
        return !(bool.TryParse(enabledValue?.Trim(), out var enabled) && !enabled);
    }
}
