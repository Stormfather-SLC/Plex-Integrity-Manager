using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Metadata
{
    public class OmdbMetadataService : IMetadataService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public OmdbMetadataService(
            HttpClient httpClient,
            IConfiguration configuration)
        {
            _httpClient = httpClient;

            _apiKey = configuration["Omdb:ApiKey"]?.Trim()
                ?? throw new InvalidOperationException(
                    "The OMDb API key is not configured. " +
                    "Set the 'Omdb:ApiKey' user secret.");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "The configured OMDb API key is empty.");
            }
        }

        public async Task EnrichAsync(Movie movie)
        {
            var parsedTitle = movie.Title;
            var parsedYear = movie.Year;
            var originalImdbId = movie.ImdbId;
            var hasImdbId = !string.IsNullOrWhiteSpace(originalImdbId);

            if (!hasImdbId && string.IsNullOrWhiteSpace(parsedTitle))
                return;

            var url = hasImdbId
                ? $"https://www.omdbapi.com/?i={Uri.EscapeDataString(originalImdbId!)}"
                : $"https://www.omdbapi.com/?t={Uri.EscapeDataString(parsedTitle!)}";

            if (!hasImdbId && parsedYear.HasValue)
                url += $"&y={parsedYear}";

            url += $"&type=movie&apikey={_apiKey}";

            Console.WriteLine(hasImdbId
                ? $"OMDb Lookup by IMDb ID: IMDb='{originalImdbId}', ParsedTitle='{parsedTitle}', ParsedYear='{parsedYear}'"
                : $"OMDb Lookup by Title: Title='{parsedTitle}', Year='{parsedYear}'");

            HttpResponseMessage response;

            try
            {
                response = await _httpClient.GetAsync(url);
            }
            catch (Exception ex) when (
                ex is HttpRequestException ||
                ex is TaskCanceledException)
            {
                if (hasImdbId)
                    MarkImdbLookupFailure(movie);

                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                if (hasImdbId)
                    MarkImdbLookupFailure(movie);

                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<OmdbResponse>(json);

            if (data == null || data.Response == "False")
            {
                if (hasImdbId)
                    MarkImdbLookupFailure(movie);

                return;
            }

            var metadataYear = ParseYear(data.Year);
            var metadataTitle = NormalizeMetadataValue(data.Title);
            var returnedImdbId = NormalizeMetadataValue(data.ImdbID);

            movie.SuggestedTitle = metadataTitle;
            movie.MatchConfidence = CalculateConfidence(
                parsedTitle,
                parsedYear,
                metadataTitle ?? string.Empty,
                metadataYear,
                hasImdbId);
            movie.MetadataMatchedByImdbId = hasImdbId;
            movie.Title = metadataTitle ?? movie.Title;
            movie.Year = metadataYear ?? movie.Year;
            movie.ImdbId = returnedImdbId ?? originalImdbId;
            movie.MpaRating = NormalizeMetadataValue(data.Rated);
            movie.Genres = ParseGenres(data.Genre);
            movie.PrimaryGenre = movie.Genres.FirstOrDefault();
            movie.MetadataFetched = true;

            // An IMDb ID embedded in the source path is the authoritative movie
            // identity. Once OMDb successfully resolves that ID, parsed title/year
            // differences are diagnostic only and must not force manual review.
            if (hasImdbId)
            {
                if (string.IsNullOrWhiteSpace(movie.Title) || !movie.Year.HasValue)
                {
                    movie.RequireReview(
                        "IMDb ID matched, but OMDb did not return the title and release year required for naming");
                    movie.Status = "Needs Review";
                    return;
                }

                movie.Status = movie.NeedsReview
                    ? "Needs Review"
                    : "IMDb ID Match";
                return;
            }

            if (movie.MatchConfidence < 85)
            {
                movie.RequireReview(
                    $"Low confidence metadata match ({movie.MatchConfidence:0}% confidence)");
                movie.Status = "Needs Review";
            }
            else
            {
                movie.Status = movie.NeedsReview
                    ? "Needs Review"
                    : "Metadata Enriched";
            }
        }

        private static void MarkImdbLookupFailure(Movie movie)
        {
            movie.MetadataFetched = false;
            movie.MetadataMatchedByImdbId = false;
            movie.RequireReview("IMDb ID found, but OMDb lookup failed");
            movie.MatchConfidence = 0;
            movie.Status = "IMDb ID Lookup Failed";
        }

        private static string? NormalizeMetadataValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
                ? null
                : value.Trim();
        }

        private static List<string> ParseGenres(string? rawGenres)
        {
            if (string.IsNullOrWhiteSpace(rawGenres) ||
                rawGenres.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            return rawGenres
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(genre => genre.Trim())
                .Where(genre => !string.IsNullOrWhiteSpace(genre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int? ParseYear(string? rawYear)
        {
            if (string.IsNullOrWhiteSpace(rawYear))
                return null;

            var match = Regex.Match(rawYear, @"\b(19|20)\d{2}\b");

            return match.Success && int.TryParse(match.Value, out var year)
                ? year
                : null;
        }

        private static double CalculateConfidence(
            string? parsedTitle,
            int? parsedYear,
            string metadataTitle,
            int? metadataYear,
            bool matchedByImdbId)
        {
            var titleSimilarity = CalculateTitleSimilarity(parsedTitle, metadataTitle);
            var yearMatches = parsedYear.HasValue && metadataYear.HasValue && parsedYear.Value == metadataYear.Value;
            var yearConflicts = parsedYear.HasValue && metadataYear.HasValue && parsedYear.Value != metadataYear.Value;

            if (matchedByImdbId)
            {
                if (titleSimilarity >= 0.95 && yearMatches)
                    return 100;

                if (titleSimilarity >= 0.85 && yearMatches)
                    return 98;

                if (titleSimilarity >= 0.65 && yearMatches)
                    return 94;

                if (titleSimilarity >= 0.85 && !yearConflicts)
                    return 95;

                if (yearMatches)
                    return 90;

                if (yearConflicts)
                    return titleSimilarity >= 0.85 ? 82 : 75;

                return 88;
            }

            if (titleSimilarity >= 0.95 && yearMatches)
                return 95;

            if (titleSimilarity >= 0.85 && yearMatches)
                return 90;

            if (titleSimilarity >= 0.85 && !yearConflicts)
                return 86;

            if (yearConflicts)
                return titleSimilarity >= 0.90 ? 78 : 65;

            return Math.Round(titleSimilarity * 80, 0);
        }

        private static double CalculateTitleSimilarity(string? left, string? right)
        {
            var normalizedLeft = NormalizeTitle(left);
            var normalizedRight = NormalizeTitle(right);

            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
                return 0;

            if (normalizedLeft == normalizedRight)
                return 1;

            var distance = LevenshteinDistance(normalizedLeft, normalizedRight);
            var maxLength = Math.Max(normalizedLeft.Length, normalizedRight.Length);

            return maxLength == 0
                ? 0
                : 1.0 - ((double)distance / maxLength);
        }

        private static string NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var normalized = title.ToLowerInvariant();

            normalized = Regex.Replace(normalized, @"\b(the|a|an)\b", " ");
            normalized = Regex.Replace(normalized, @"[^a-z0-9]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

            return normalized;
        }

        private static int LevenshteinDistance(string left, string right)
        {
            var matrix = new int[left.Length + 1, right.Length + 1];

            for (var i = 0; i <= left.Length; i++)
                matrix[i, 0] = i;

            for (var j = 0; j <= right.Length; j++)
                matrix[0, j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;

                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[left.Length, right.Length];
        }

        private class OmdbResponse
        {
            public string Title { get; set; } = string.Empty;

            public string Year { get; set; } = string.Empty;

            public string Rated { get; set; } = string.Empty;

            public string Genre { get; set; } = string.Empty;

            [JsonPropertyName("imdbID")]
            public string ImdbID { get; set; } = string.Empty;

            public string Response { get; set; } = string.Empty;
        }
    }
}
