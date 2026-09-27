using System.Text.RegularExpressions;

namespace PIM.Core.Models;

/// <summary>
/// Normalizes an IMDb ID typed or pasted by the user. Accepts a bare ID
/// ("tt0088794") or an IMDb page URL containing exactly one ID.
/// </summary>
public static class ImdbIdInput
{
    private static readonly Regex ImdbIdRegex = new(
        @"(?<![A-Za-z0-9])tt\d{7,9}(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryNormalize(string? input, out string imdbId)
    {
        imdbId = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || input.Length > 500)
            return false;

        var ids = ImdbIdRegex.Matches(input)
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count != 1)
            return false;

        imdbId = ids[0];
        return true;
    }
}
