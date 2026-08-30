using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Metadata
{
    public class OmdbMetadataService : IMetadataService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly ILogger<OmdbMetadataService> _logger;

        public OmdbMetadataService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<OmdbMetadataService>? logger = null)
        {
            _httpClient = httpClient;
            _logger = logger ?? NullLogger<OmdbMetadataService>.Instance;

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
            ArgumentNullException.ThrowIfNull(movie);

            movie.ClearMetadataReviewReasons();

            var parsedTitle = movie.Title;
            var parsedYear = movie.Year;
            var originalImdbId = movie.ImdbId;
            var hasImdbId = !string.IsNullOrWhiteSpace(originalImdbId);

            if (!hasImdbId && string.IsNullOrWhiteSpace(parsedTitle))
            {
                MarkLookupFailure(
                    movie,
                    MetadataLookupFailureType.MissingLookupInput,
                    "A title or IMDb ID is required to query OMDb.",
                    "OMDb lookup not attempted: title and IMDb ID are missing");
                return;
            }

            var url = hasImdbId
                ? $"https://www.omdbapi.com/?i={Uri.EscapeDataString(originalImdbId!)}"
                : $"https://www.omdbapi.com/?t={Uri.EscapeDataString(parsedTitle!)}";

            if (!hasImdbId && parsedYear.HasValue)
                url += $"&y={parsedYear}";

            url += $"&type=movie&apikey={_apiKey}";

            _logger.LogInformation(
                "Starting OMDb {LookupType} lookup for title '{Title}' and year '{Year}'.",
                hasImdbId ? "IMDb ID" : "title/year",
                parsedTitle,
                parsedYear);

            try
            {
                using var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var statusDetail = $"HTTP {(int)response.StatusCode}";
                    var reasonPhrase = SanitizeDiagnostic(response.ReasonPhrase);

                    if (!string.IsNullOrWhiteSpace(reasonPhrase))
                        statusDetail += $" ({reasonPhrase})";

                    MarkLookupFailure(
                        movie,
                        MetadataLookupFailureType.HttpFailure,
                        statusDetail,
                        $"OMDb lookup failed: {statusDetail}");
                    return;
                }

                string json;

                try
                {
                    json = await response.Content.ReadAsStringAsync();
                }
                catch (TaskCanceledException)
                {
                    MarkTimeoutFailure(movie);
                    return;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException ||
                    ex is IOException)
                {
                    MarkNetworkFailure(movie);
                    return;
                }

                OmdbResponse? data;

                try
                {
                    data = JsonSerializer.Deserialize<OmdbResponse>(json);
                }
                catch (Exception ex) when (
                    ex is JsonException ||
                    ex is NotSupportedException)
                {
                    MarkMalformedResponse(movie);
                    return;
                }

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.Response))
                {
                    MarkMalformedResponse(movie);
                    return;
                }

                if (data.Response.Equals(
                        "False",
                        StringComparison.OrdinalIgnoreCase))
                {
                    MarkOmdbFailure(movie, data.Error);
                    return;
                }

                if (!data.Response.Equals(
                        "True",
                        StringComparison.OrdinalIgnoreCase))
                {
                    MarkMalformedResponse(movie);
                    return;
                }

                ApplySuccessfulResponse(
                    movie,
                    data,
                    parsedTitle,
                    parsedYear,
                    originalImdbId,
                    hasImdbId);
            }
            catch (TaskCanceledException)
            {
                MarkTimeoutFailure(movie);
            }
            catch (HttpRequestException)
            {
                MarkNetworkFailure(movie);
            }
        }

        private void ApplySuccessfulResponse(
            Movie movie,
            OmdbResponse data,
            string? parsedTitle,
            int? parsedYear,
            string? originalImdbId,
            bool hasImdbId)
        {
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

            var missingFields = new List<string>();

            if (string.IsNullOrWhiteSpace(movie.Title))
                missingFields.Add("title");

            if (!movie.Year.HasValue)
                missingFields.Add("release year");

            if (string.IsNullOrWhiteSpace(movie.ImdbId))
                missingFields.Add("IMDb ID");

            if (missingFields.Count > 0)
            {
                var detail = string.Join(", ", missingFields);
                MarkLookupFailure(
                    movie,
                    MetadataLookupFailureType.MissingRequiredFields,
                    detail,
                    $"OMDb lookup succeeded but did not return required fields: {detail}");
                return;
            }

            movie.MetadataFetched = true;

            // An IMDb ID embedded in the source path is the authoritative movie
            // identity. Once OMDb successfully resolves that ID, parsed title/year
            // differences are diagnostic only and must not force manual review.
            if (hasImdbId)
            {
                movie.Status = movie.NeedsReview
                    ? "Needs Review"
                    : "IMDb ID Match";
                return;
            }

            if (movie.MatchConfidence < 85)
            {
                var reviewReason =
                    $"Low confidence metadata match ({movie.MatchConfidence:0}% confidence)";
                movie.SetMetadataReview(
                    reviewReason,
                    MetadataLookupFailureType.LowConfidence,
                    $"{movie.MatchConfidence:0}% confidence");
                movie.Status = "Needs Review";
            }
            else
            {
                movie.Status = movie.NeedsReview
                    ? "Needs Review"
                    : "Metadata Enriched";
            }
        }

        private void MarkOmdbFailure(Movie movie, string? rawError)
        {
            var error = SanitizeDiagnostic(rawError);

            if (string.IsNullOrWhiteSpace(error))
                error = "OMDb returned an unspecified error";

            var failureType = error.Contains(
                    "not found",
                    StringComparison.OrdinalIgnoreCase)
                ? MetadataLookupFailureType.MovieNotFound
                : error.Contains(
                    "request limit",
                    StringComparison.OrdinalIgnoreCase) ||
                  error.Contains(
                    "quota",
                    StringComparison.OrdinalIgnoreCase)
                    ? MetadataLookupFailureType.RequestLimitReached
                    : error.Contains(
                        "api key",
                        StringComparison.OrdinalIgnoreCase)
                        ? MetadataLookupFailureType.InvalidApiKey
                        : MetadataLookupFailureType.OmdbError;

            MarkLookupFailure(
                movie,
                failureType,
                error,
                $"OMDb lookup failed: {error}");
        }

        private void MarkTimeoutFailure(Movie movie)
        {
            MarkLookupFailure(
                movie,
                MetadataLookupFailureType.Timeout,
                "The OMDb request timed out.",
                "OMDb lookup failed: request timed out");
        }

        private void MarkNetworkFailure(Movie movie)
        {
            MarkLookupFailure(
                movie,
                MetadataLookupFailureType.NetworkFailure,
                "The OMDb request could not reach the service.",
                "OMDb lookup failed: network error");
        }

        private void MarkMalformedResponse(Movie movie)
        {
            MarkLookupFailure(
                movie,
                MetadataLookupFailureType.MalformedResponse,
                "OMDb returned malformed or unusable data.",
                "OMDb lookup failed: malformed or unusable response");
        }

        private void MarkLookupFailure(
            Movie movie,
            MetadataLookupFailureType failureType,
            string failureDetail,
            string reviewReason)
        {
            movie.MetadataFetched = false;
            movie.MetadataMatchedByImdbId = false;
            movie.SetMetadataReview(
                reviewReason,
                failureType,
                failureDetail);
            movie.MatchConfidence = 0;
            movie.Status = "Metadata Lookup Failed";

            _logger.LogWarning(
                "OMDb lookup failed with {FailureType}: {FailureDetail}",
                failureType,
                failureDetail);
        }

        private string? SanitizeDiagnostic(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var sanitized = value
                .Replace(_apiKey, "[redacted]", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            return sanitized.Length <= 240
                ? sanitized
                : sanitized[..240];
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

            public string Error { get; set; } = string.Empty;
        }
    }
}
