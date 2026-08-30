using System.Text.RegularExpressions;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Parsing
{
    public class FileNameParser : IFileNameParser
    {
        private static readonly Regex ImdbIdRegex = new(
            @"\btt\d{7,9}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ImdbTagRegex = new(
            @"\{?\s*imdb[\s._-]*tt\d{7,9}\s*\}?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex YearRegex = new(
            @"\b(19|20)\d{2}\b",
            RegexOptions.Compiled);

        public void Parse(Movie movie)
        {
            if (string.IsNullOrWhiteSpace(movie.FileName))
                return;

            var name = Path.GetFileNameWithoutExtension(movie.FileName);
            var parentFolderName = GetImmediateParentFolderName(movie.DirectoryPath);

            // Start with the raw file name. We will progressively remove
            // IMDb tags, years, version markers, and technical noise from this value.
            var cleaned = name;

            movie.VersionTag = null;
            movie.IsAlternateVersion = false;
            movie.CandidateYears.Clear();

            // =========================================================
            // Detect IMDb ID first
            //
            // Existing Plex-style names may store the IMDb ID in either the
            // movie file or its immediate/ancestor folder. The ID is the strongest
            // possible lookup key, so preserve it before title parsing.
            // =========================================================
            var imdbSearchText = $"{name} {movie.DirectoryPath}";
            var imdbMatch = ImdbIdRegex.Match(imdbSearchText);

            if (imdbMatch.Success)
                movie.ImdbId = imdbMatch.Value.ToLowerInvariant();

            cleaned = ImdbTagRegex.Replace(cleaned, " ");
            cleaned = ImdbIdRegex.Replace(cleaned, " ");

            // =========================================================
            // Detect version / edition markers
            // =========================================================
            var versionPatterns = new Dictionary<string, string>
            {
                { @"\bFamily[\s._-]*Edit\b", "Family Edit" },
                { @"\bClean[\s._-]*Version\b", "Clean Version" },
                { @"\bTV[\s._-]*Edit\b", "TV Edit" },
                { @"\bExtended[\s._-]*Edition\b", "Extended Edition" },
                { @"\bDirector'?s[\s._-]*Cut\b", "Director's Cut" },
                { @"\bTheatrical[\s._-]*Cut\b", "Theatrical Cut" },
                { @"\bEdited\b", "Edited" },
                { @"\bClean\b", "Clean" },
                { @"\bExtended\b", "Extended Edition" },
                { @"\bUnrated\b", "Unrated" },
                { @"\bRemastered\b", "Remastered" },
                { @"\bIMAX\b", "IMAX" }
            };

            var detectedVersions = versionPatterns
                .SelectMany(pattern => Regex.Matches(
                        name,
                        pattern.Key,
                        RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(match => new
                    {
                        match.Index,
                        match.Length,
                        Tag = pattern.Value
                    }))
                .OrderBy(version => version.Index)
                .ThenByDescending(version => version.Length)
                .ToList();

            var nonOverlappingVersions = detectedVersions
                .Where((version, index) => !detectedVersions
                    .Take(index)
                    .Any(previous =>
                        version.Index < previous.Index + previous.Length &&
                        previous.Index < version.Index + version.Length))
                .ToList();

            foreach (var pattern in versionPatterns)
            {
                cleaned = Regex.Replace(
                    cleaned,
                    pattern.Key,
                    " ",
                    RegexOptions.IgnoreCase);
            }

            var versionTags = nonOverlappingVersions
                .Select(version => version.Tag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (versionTags.Count > 0)
            {
                movie.VersionTag = string.Join(" + ", versionTags);
                movie.IsAlternateVersion = true;
            }

            // =========================================================
            // Detect release year
            //
            // Prefer the filename. When it does not include a year, use the
            // immediate movie folder. This supports layouts such as:
            //   A Quiet Place (2018) {imdb-tt6644200}\A Quiet Place 4K UHD.mp4
            // without accidentally taking a year from a distant ancestor folder.
            // =========================================================
            var fileYearMatches = YearRegex.Matches(cleaned);

            foreach (Match match in fileYearMatches)
                movie.CandidateYears.Add(int.Parse(match.Value));

            if (movie.CandidateYears.Count == 0 &&
                !string.IsNullOrWhiteSpace(parentFolderName))
            {
                foreach (Match match in YearRegex.Matches(parentFolderName))
                    movie.CandidateYears.Add(int.Parse(match.Value));
            }

            if (movie.CandidateYears.Count > 0)
                movie.Year = movie.CandidateYears.Last();

            // Remove only years actually present in the filename title text.
            foreach (Match match in fileYearMatches)
            {
                cleaned = Regex.Replace(
                    cleaned,
                    $@"(?<=^|[._\-\s]){match.Value}(?=$|[._\-\s])",
                    " ");
            }

            cleaned = Regex.Replace(cleaned, @"\(\s*(19|20)\d{2}\s*\)", "");

            // =========================================================
            // Remove common technical noise
            // =========================================================
            cleaned = Regex.Replace(
                cleaned,
                @"\b(1080p|720p|480p|2160p|4k|BluRay|WEBRip|WEB-DL|HDRip|DVDRip|x264|x265|AAC|YTS|RARBG|UHD|HD)\b",
                "",
                RegexOptions.IgnoreCase);

            cleaned = cleaned
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ');

            cleaned = Regex.Replace(cleaned, @"\(\s*\)", " ");
            cleaned = Regex.Replace(cleaned, @"\[\s*\]", " ");
            cleaned = Regex.Replace(cleaned, @"\{\s*\}", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            // Normalize library-style trailing articles into the title form
            // expected by OMDb: "Matrix Resurrections, The" becomes
            // "The Matrix Resurrections".
            var trailingArticle = Regex.Match(
                cleaned,
                @"^(.+),\s*(The|A|An)$",
                RegexOptions.IgnoreCase);

            if (trailingArticle.Success)
            {
                cleaned =
                    $"{trailingArticle.Groups[2].Value} {trailingArticle.Groups[1].Value}";
            }

            movie.Title = cleaned;
        }

        private static string? GetImmediateParentFolderName(string? directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                return null;

            try
            {
                return new DirectoryInfo(directoryPath).Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
