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

        public void Parse(Movie movie)
        {
            if (string.IsNullOrWhiteSpace(movie.FileName))
                return;

            var name = Path.GetFileNameWithoutExtension(movie.FileName);

            // Start with the raw file name. We will progressively remove
            // IMDb tags, years, version markers, and technical noise from this value.
            string cleaned = name;

            movie.VersionTag = null;
            movie.IsAlternateVersion = false;
            movie.CandidateYears.Clear();

            // =========================================================
            // 🔑 Detect IMDb ID first
            //
            // Existing Plex-style names such as:
            // "Annie (1982) {imdb-tt0083564}.m4v"
            //
            // already contain the strongest possible lookup key. Preserve it
            // and remove the IMDb tag from the title text before title parsing.
            // =========================================================
            var imdbSearchText = $"{name} {movie.DirectoryPath}";
            var imdbMatch = ImdbIdRegex.Match(imdbSearchText);

            if (imdbMatch.Success)
            {
                movie.ImdbId = imdbMatch.Value.ToLowerInvariant();
            }

            cleaned = ImdbTagRegex.Replace(cleaned, " ");
            cleaned = ImdbIdRegex.Replace(cleaned, " ");

            // =========================================================
            // 🎬 Detect version / edition markers
            //
            // These should NOT be sent to OMDb as part of the title.
            // However, they are meaningful to the user and to Plex, so
            // we preserve them as VersionTag.
            //
            // Example:
            // "Cocktail 1988 - Edited.mp4"
            //
            // Becomes:
            // Title: Cocktail
            // Year: 1988
            // VersionTag: Edited
            // =========================================================
            var versionPatterns = new Dictionary<string, string>
            {
                // Longer / more specific phrases first.
                // These patterns allow spaces, dots, underscores, or hyphens between words.
                { @"\bFamily[\s._-]*Edit\b", "Family Edit" },
                { @"\bClean[\s._-]*Version\b", "Clean Version" },
                { @"\bTV[\s._-]*Edit\b", "TV Edit" },
                { @"\bExtended[\s._-]*Edition\b", "Extended Edition" },
                { @"\bDirector'?s[\s._-]*Cut\b", "Director's Cut" },
                { @"\bTheatrical[\s._-]*Cut\b", "Theatrical Cut" },

                // Shorter / single-token markers after.
                { @"\bEdited\b", "Edited" },
                { @"\bClean\b", "Clean" },
                { @"\bExtended\b", "Extended Edition" },
                { @"\bUnrated\b", "Unrated" },
                { @"\bRemastered\b", "Remastered" },
                { @"\bIMAX\b", "IMAX" }
            };

            // Detect version tag from the original filename text.
            // Use "name" here because it is the untouched filename without the extension.
            foreach (var pattern in versionPatterns)
            {
                if (Regex.IsMatch(name, pattern.Key, RegexOptions.IgnoreCase))
                {
                    movie.VersionTag = pattern.Value;
                    movie.IsAlternateVersion = true;

                    // Remove the version marker from the cleaned title
                    // so OMDb receives the real movie title only.
                    cleaned = Regex.Replace(
                        cleaned,
                        pattern.Key,
                        " ",
                        RegexOptions.IgnoreCase);

                    break;
                }
            }

            // =========================================================
            // 📅 Detect years
            //
            // We collect all possible years, then use the LAST one as
            // the primary guess. This helps with file names that contain
            // multiple year-like values.
            // =========================================================
            var yearMatches = Regex.Matches(cleaned, @"\b(19|20)\d{2}\b");

            foreach (Match match in yearMatches)
            {
                var year = int.Parse(match.Value);
                movie.CandidateYears.Add(year);
            }

            if (movie.CandidateYears.Count > 0)
            {
                movie.Year = movie.CandidateYears.Last();
            }

            // Remove all detected years from the cleaned title.
            foreach (Match match in yearMatches)
            {
                cleaned = Regex.Replace(
                    cleaned,
                    $@"(?<=^|[._\-\s]){match.Value}(?=$|[._\-\s])",
                    " ");
            }

            // Remove leftover "(YEAR)" patterns.
            cleaned = Regex.Replace(cleaned, @"\(\s*(19|20)\d{2}\s*\)", "");

            // =========================================================
            // 🧹 Remove common technical noise
            //
            // These are not part of the movie title and should not be
            // sent to OMDb.
            // =========================================================
            cleaned = Regex.Replace(
                cleaned,
                @"\b(1080p|720p|480p|2160p|4k|BluRay|WEBRip|WEB-DL|HDRip|DVDRip|x264|x265|AAC|YTS|RARBG|UHD|HD)\b",
                "",
                RegexOptions.IgnoreCase);

            // Replace common separators with spaces.
            cleaned = cleaned
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ');

            // Clean up brackets or parentheses that may now be empty.
            cleaned = Regex.Replace(cleaned, @"\(\s*\)", " ");
            cleaned = Regex.Replace(cleaned, @"\[\s*\]", " ");
            cleaned = Regex.Replace(cleaned, @"\{\s*\}", " ");

            // Clean extra spaces.
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            movie.Title = cleaned;
        }
    }
}
