using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    /// <summary>
    /// Cross-checks proposed movie moves against the movies currently known by
    /// Plex Media Server. The Plex library is loaded once per service instance,
    /// then reused for every movie checked during the current conflict-detection pass.
    /// </summary>
    public class PlexLibraryConflictService : IPlexLibraryConflictService
    {
        private const int PageSize = 500;
        private const string StandardEditionKey = "<standard>";

        private static readonly Regex ImdbIdRegex = new(
            @"(?<id>tt\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EditionTagRegex = new(
            @"\{edition-(?<edition>[^}]+)\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly object _loadLock = new();

        private bool _loadAttempted;
        private IReadOnlyList<PlexLibraryEntry> _libraryEntries = Array.Empty<PlexLibraryEntry>();
        private string? _loadError;

        public PlexLibraryConflictService(
            HttpClient httpClient,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        public PlexLibraryConflictResult Check(Movie movie)
        {
            if (!IsEnabled())
            {
                return PlexLibraryConflictResult.NoConflict();
            }

            EnsureLibraryLoaded();

            if (!string.IsNullOrWhiteSpace(_loadError))
            {
                return PlexLibraryConflictResult.Conflict(
                    PlexLibraryConflictType.LibraryMismatch,
                    $"Plex library validation could not be completed: {_loadError}");
            }

            if (_libraryEntries.Count == 0)
            {
                return PlexLibraryConflictResult.NoConflict();
            }

            return FindConflict(movie);
        }

        private PlexLibraryConflictResult FindConflict(Movie movie)
        {
            var targetPath = NormalizePath(movie.TargetPath);
            var movieImdbId = NormalizeImdbId(movie.ImdbId);
            var movieTitle = NormalizeTitle(movie.Title);
            var movieEdition = NormalizeEdition(movie.VersionTag);

            // A stale Plex database can still point at a path that no longer exists on disk.
            // Destination conflict detection cannot see that, so flag any identity mismatch
            // where Plex already associates the proposed target path with another movie.
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                var targetPathMismatch = _libraryEntries.FirstOrDefault(entry =>
                    entry.Paths.Contains(targetPath, StringComparer.OrdinalIgnoreCase) &&
                    !IdentityMatches(movieImdbId, movieTitle, movie.Year, entry));

                if (targetPathMismatch != null)
                {
                    return PlexLibraryConflictResult.Conflict(
                        PlexLibraryConflictType.LibraryMismatch,
                        $"Plex already associates the proposed target path with a different library item: {FormatIdentity(targetPathMismatch)}.",
                        GetDisplayPath(targetPathMismatch));
                }
            }

            if (!string.IsNullOrWhiteSpace(movieImdbId))
            {
                var imdbMatchesElsewhere = _libraryEntries
                    .Where(entry =>
                        string.Equals(
                            entry.ImdbId,
                            movieImdbId,
                            StringComparison.OrdinalIgnoreCase) &&
                        !EntryContainsPath(entry, targetPath))
                    .ToList();

                if (imdbMatchesElsewhere.Count > 0)
                {
                    var sameEdition = imdbMatchesElsewhere.FirstOrDefault(entry =>
                        string.Equals(
                            entry.EditionKey,
                            movieEdition,
                            StringComparison.OrdinalIgnoreCase));

                    if (sameEdition != null)
                    {
                        return PlexLibraryConflictResult.Conflict(
                            PlexLibraryConflictType.SameImdbIdDifferentPath,
                            $"Plex already contains IMDb {movieImdbId} with the same edition at a different path.",
                            GetDisplayPath(sameEdition));
                    }

                    var alternateEdition = imdbMatchesElsewhere[0];

                    return PlexLibraryConflictResult.Conflict(
                        PlexLibraryConflictType.ExistingAlternateVersion,
                        $"Plex already contains IMDb {movieImdbId} as another edition ({FormatEdition(alternateEdition.EditionKey)}).",
                        GetDisplayPath(alternateEdition));
                }
            }

            if (!string.IsNullOrWhiteSpace(movieTitle) && movie.Year != null)
            {
                var titleYearMatch = _libraryEntries.FirstOrDefault(entry =>
                    entry.Year == movie.Year &&
                    string.Equals(
                        entry.NormalizedTitle,
                        movieTitle,
                        StringComparison.OrdinalIgnoreCase) &&
                    !EntryContainsPath(entry, targetPath));

                if (titleYearMatch != null)
                {
                    if (!string.IsNullOrWhiteSpace(movieImdbId) &&
                        !string.IsNullOrWhiteSpace(titleYearMatch.ImdbId) &&
                        !string.Equals(
                            movieImdbId,
                            titleYearMatch.ImdbId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return PlexLibraryConflictResult.Conflict(
                            PlexLibraryConflictType.LibraryMismatch,
                            $"Plex contains the same title and year but with a different IMDb ID ({titleYearMatch.ImdbId}).",
                            GetDisplayPath(titleYearMatch));
                    }

                    return PlexLibraryConflictResult.Conflict(
                        PlexLibraryConflictType.SameTitleYearDifferentPath,
                        "Plex already contains the same title and year at a different path.",
                        GetDisplayPath(titleYearMatch));
                }
            }

            return PlexLibraryConflictResult.NoConflict();
        }

        private void EnsureLibraryLoaded()
        {
            if (_loadAttempted)
            {
                return;
            }

            lock (_loadLock)
            {
                if (_loadAttempted)
                {
                    return;
                }

                try
                {
                    _libraryEntries = LoadLibraryAsync()
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    _loadError = ex.Message;
                    _libraryEntries = Array.Empty<PlexLibraryEntry>();
                }
                finally
                {
                    _loadAttempted = true;
                }
            }
        }

        private async Task<IReadOnlyList<PlexLibraryEntry>> LoadLibraryAsync()
        {
            var token = _configuration["Plex:Token"];

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Plex:Token is not configured. Store the token in .NET user secrets, not appsettings.json.");
            }

            var sectionsEnvelope = await SendJsonAsync<PlexSectionsEnvelope>(
                "/library/sections",
                token);

            var movieSections = sectionsEnvelope.MediaContainer?.Directory?
                .Where(section =>
                    string.Equals(
                        section.Type,
                        "movie",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(section.Key))
                .ToList() ?? new List<PlexSectionDto>();

            if (movieSections.Count == 0)
            {
                throw new InvalidOperationException(
                    "Plex returned no movie library sections.");
            }

            var entries = new List<PlexLibraryEntry>();

            foreach (var section in movieSections)
            {
                var start = 0;

                while (true)
                {
                    var sectionKey = Uri.EscapeDataString(section.Key!);
                    var envelope = await SendJsonAsync<PlexMediaEnvelope>(
                        $"/library/sections/{sectionKey}/all?type=1&includeGuids=1",
                        token,
                        start,
                        PageSize);

                    var container = envelope.MediaContainer;
                    var items = container?.Metadata ?? new List<PlexMovieDto>();

                    foreach (var item in items)
                    {
                        entries.Add(CreateLibraryEntry(item, section.Title));
                    }

                    if (items.Count == 0)
                    {
                        break;
                    }

                    start += items.Count;

                    if (container?.TotalSize is int totalSize)
                    {
                        if (start >= totalSize)
                        {
                            break;
                        }
                    }
                    else if (items.Count < PageSize)
                    {
                        break;
                    }
                }
            }

            return entries;
        }

        private async Task<T> SendJsonAsync<T>(
            string path,
            string token,
            int? containerStart = null,
            int? containerSize = null)
        {
            var baseUrl = _configuration["Plex:BaseUrl"]?.Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://localhost:32400";
            }

            var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            var requestUri = new Uri(baseUri, path.TrimStart('/'));

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            request.Headers.TryAddWithoutValidation(
                "X-Plex-Client-Identifier",
                "plex-integrity-manager");
            request.Headers.TryAddWithoutValidation(
                "X-Plex-Product",
                "Plex Integrity Manager");
            request.Headers.TryAddWithoutValidation("X-Plex-Version", "0.1");

            if (containerStart != null)
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Plex-Container-Start",
                    containerStart.Value.ToString());
            }

            if (containerSize != null)
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Plex-Container-Size",
                    containerSize.Value.ToString());
            }

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);

            if (payload == null)
            {
                throw new InvalidOperationException(
                    $"Plex returned an empty or unreadable response for {path}.");
            }

            return payload;
        }

        private static PlexLibraryEntry CreateLibraryEntry(
            PlexMovieDto item,
            string? libraryTitle)
        {
            var paths = item.Media?
                .SelectMany(media => media.Part ?? new List<PlexPartDto>())
                .Select(part => NormalizePath(part.File))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            var imdbId = item.Guids?
                .Select(guid => NormalizeImdbId(guid.Id))
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

            imdbId ??= NormalizeImdbId(item.PrimaryGuid);

            var edition = item.EditionTitle;

            if (string.IsNullOrWhiteSpace(edition))
            {
                edition = paths
                    .Select(ExtractEditionFromPath)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            }

            return new PlexLibraryEntry(
                item.Title,
                NormalizeTitle(item.Title),
                item.Year,
                imdbId,
                NormalizeEdition(edition),
                paths,
                libraryTitle);
        }

        private static bool IdentityMatches(
            string? movieImdbId,
            string? movieTitle,
            int? movieYear,
            PlexLibraryEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(movieImdbId) &&
                !string.IsNullOrWhiteSpace(entry.ImdbId))
            {
                return string.Equals(
                    movieImdbId,
                    entry.ImdbId,
                    StringComparison.OrdinalIgnoreCase);
            }

            return movieYear != null &&
                   entry.Year == movieYear &&
                   !string.IsNullOrWhiteSpace(movieTitle) &&
                   string.Equals(
                       movieTitle,
                       entry.NormalizedTitle,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool EntryContainsPath(
            PlexLibraryEntry entry,
            string? normalizedPath)
        {
            return !string.IsNullOrWhiteSpace(normalizedPath) &&
                   entry.Paths.Contains(
                       normalizedPath,
                       StringComparer.OrdinalIgnoreCase);
        }

        private static string? NormalizeImdbId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = ImdbIdRegex.Match(value);
            return match.Success
                ? match.Groups["id"].Value.ToLowerInvariant()
                : null;
        }

        private static string? NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return string.Join(
                ' ',
                title.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Trim();
        }

        private static string NormalizeEdition(string? edition)
        {
            if (string.IsNullOrWhiteSpace(edition) ||
                edition.Trim().Equals(
                    "Alternate Version",
                    StringComparison.OrdinalIgnoreCase))
            {
                return StandardEditionKey;
            }

            return edition.Trim().ToUpperInvariant();
        }

        private static string? ExtractEditionFromPath(string path)
        {
            var match = EditionTagRegex.Match(path);
            return match.Success
                ? match.Groups["edition"].Value.Trim()
                : null;
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return path
                .Trim()
                .Replace('\\', '/')
                .TrimEnd('/');
        }

        private static string GetDisplayPath(PlexLibraryEntry entry)
        {
            return entry.Paths.FirstOrDefault()
                   ?? $"Plex library item: {FormatIdentity(entry)}";
        }

        private static string FormatIdentity(PlexLibraryEntry entry)
        {
            var title = string.IsNullOrWhiteSpace(entry.Title)
                ? "Unknown title"
                : entry.Title;

            return entry.Year == null
                ? title
                : $"{title} ({entry.Year})";
        }

        private static string FormatEdition(string editionKey)
        {
            return editionKey == StandardEditionKey
                ? "standard edition"
                : editionKey;
        }

        private bool IsEnabled()
        {
            return bool.TryParse(
                       _configuration["Plex:Enabled"],
                       out var enabled) &&
                   enabled;
        }

        private sealed record PlexLibraryEntry(
            string? Title,
            string? NormalizedTitle,
            int? Year,
            string? ImdbId,
            string EditionKey,
            IReadOnlyList<string> Paths,
            string? LibraryTitle);

        private sealed class PlexSectionsEnvelope
        {
            [JsonPropertyName("MediaContainer")]
            public PlexSectionsContainer? MediaContainer { get; set; }
        }

        private sealed class PlexSectionsContainer
        {
            [JsonPropertyName("Directory")]
            public List<PlexSectionDto>? Directory { get; set; }
        }

        private sealed class PlexSectionDto
        {
            [JsonPropertyName("key")]
            public string? Key { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }

        private sealed class PlexMediaEnvelope
        {
            [JsonPropertyName("MediaContainer")]
            public PlexMediaContainer? MediaContainer { get; set; }
        }

        private sealed class PlexMediaContainer
        {
            [JsonPropertyName("totalSize")]
            public int? TotalSize { get; set; }

            [JsonPropertyName("Metadata")]
            public List<PlexMovieDto>? Metadata { get; set; }
        }

        private sealed class PlexMovieDto
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("year")]
            public int? Year { get; set; }

            [JsonPropertyName("guid")]
            public string? PrimaryGuid { get; set; }

            [JsonPropertyName("editionTitle")]
            public string? EditionTitle { get; set; }

            [JsonPropertyName("Guid")]
            public List<PlexGuidDto>? Guids { get; set; }

            [JsonPropertyName("Media")]
            public List<PlexMediaDto>? Media { get; set; }
        }

        private sealed class PlexGuidDto
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }
        }

        private sealed class PlexMediaDto
        {
            [JsonPropertyName("Part")]
            public List<PlexPartDto>? Part { get; set; }
        }

        private sealed class PlexPartDto
        {
            [JsonPropertyName("file")]
            public string? File { get; set; }
        }
    }
}
