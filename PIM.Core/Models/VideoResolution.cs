using System.Text.RegularExpressions;

namespace PIM.Core.Models;

/// <summary>
/// One display vocabulary for video resolution, whether it comes from Plex
/// ("4k", "1080", "sd") or from a file name tag ("2160p", "1080i", "UHD").
/// </summary>
public static class VideoResolution
{
    private static readonly Regex FileNameTagRegex = new(
        @"(?<![A-Za-z0-9])(?<tag>2160p|4k|uhd|1080p|1080i|720p|576p|480p)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Normalizes a Plex videoResolution value.</summary>
    public static string? FromPlex(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "4k" or "2160" => "4K",
            "1080" => "1080p",
            "720" => "720p",
            "576" => "576p",
            "480" => "480p",
            "sd" => "SD",
            var other => other.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Reads a resolution tag from a file name, or null when there is none.
    /// This is a hint only: PIM does not inspect the video stream.
    /// </summary>
    public static string? FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var match = FileNameTagRegex.Match(fileName);

        if (!match.Success)
            return null;

        return match.Groups["tag"].Value.ToLowerInvariant() switch
        {
            "2160p" or "4k" or "uhd" => "4K",
            "1080p" or "1080i" => "1080p",
            var tag => tag
        };
    }

    /// <summary>Formats a byte count for display, e.g. "8.2 GB".</summary>
    public static string FormatSize(long? bytes)
    {
        if (bytes is not > 0)
            return "size unknown";

        const double Gb = 1024d * 1024 * 1024;
        const double Mb = 1024d * 1024;

        return bytes.Value >= Gb
            ? $"{bytes.Value / Gb:0.0} GB"
            : $"{bytes.Value / Mb:0} MB";
    }
}
