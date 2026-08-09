using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<PlexLibraryConflictService> _logger;
        private readonly object _loadLock = new();

        private bool _loadAttempted;
        private bool _disabledLogged;
        private IReadOnlyList<PlexLibraryEntry> _libraryEntries = Array.Empty<PlexLibraryEntry>();
        private string? _loadError;

        public PlexLibraryConflictService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<PlexLibraryConflictService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public PlexLibraryConflictResult Check(Movie movie)
        {
            if (!IsEnabled())
            {
                if (!_disabledLogged)
                {
                    _logger.LogWarning(
                        "Plex library conflict detection is disabled. Resolved Plex:Enabled value: {PlexEnabledValue}",
                        _configuration["Plex:Enabled"] ?? "<null>");
                    _disabledLogged = true;
                }

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
                // Enabled Plex validation must never silently degrade into an empty index.
                return PlexLibraryConflictResult.Conflict(
                    PlexLibraryConflictType.LibraryMismatch,
                    "Plex library validation loaded zero movie entries. PIM cannot safely determine whether this movie already exists in Plex.");
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
            // Destination conflict detection cannot see that, so any Plex item already tied
            // to the proposed target path must block the move. An identity mismatch is
            // reported separately because it indicates a more serious library inconsistency.
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                var targetPathEntry = _libraryEntries.FirstOrDefault(entry =>
                    entry.Paths.Contains(targetPath, StringComparer.OrdinalIgnoreCase));

                if (targetPathEntry != null)
                {
                    if (!IdentityMatches(movieImdbId, movieTitle, movie.Year, targetPathEntry))
                    {
                        return PlexLibraryConflictResult.Conflict(
                            PlexLibraryConflictType.LibraryMismatch,
                            $"Plex already associates the proposed target path with a different library item: {FormatIdentity(targetPathEntry)}.",
                            GetDisplayPath(targetPathEntry));
                    }

                    return PlexLibraryConflictResult.Conflict(
                        PlexLibraryConflictType.AlreadyExistsAtTargetPath,
                        $"Plex already contains this movie at the proposed target path: {FormatIdentity(targetPathEntry)}.",
                        GetDisplayPath(targetPathEntry));
                }
            }

            var imdbMatches = string.IsNullOrWhiteSpace(movieImdbId)
                ? new List<PlexLibraryEntry>()
                : _libraryEntries
                    .Where(entry => string.Equals(
                        entry.ImdbId,
                        movieImdbId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var titleYearMatches = string.IsNullOrWhiteSpace(movieTitle) || movie.Year == null
                ? new List<PlexLibraryEntry>()
                : _libraryEntries
                    .Where(entry =>
                        entry.Year == movie.Year &&
                        string.Equals(
                            entry.NormalizedTitle,
                            movieTitle,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (imdbMatches.Count > 0 || titleYearMatches.Count > 0)
            {
                _logger.LogInformation(
                    "Plex candidates found for {Title} ({Year}), IMDb {ImdbId}: {ImdbMatchCount} IMDb match(es), {TitleYearMatchCount} title/year match(es).",
                    movie.Title,
                    movie.Year,
                    movieImdbId ?? "<none>",
                    imdbMatches.Count,
                    titleYearMatches.Count);
            }

            if (imdbMatches.Count > 0)
            {
                var imdbMatchesElsewhere = imdbMatches
                    .Where(entry => !EntryContainsPath(entry, targetPath))
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

            var titleYearMatch = titleYearMatches
                .FirstOrDefault(entry => !EntryContainsPath(entry, targetPath));

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
                    _logger.LogError(ex, "Plex library validation failed while loading the movie index.");
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

            var baseUrl = GetBaseUrl();
            _logger.LogInformation(
                "Loading Plex movie libraries from {PlexBaseUrl}.",
                baseUrl);

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

            _logger.LogInformation(
                "Plex returned {MovieSectionCount} movie library section(s).",
                movieSections.Count);

            var entries = new List<PlexLibraryEntry>();

            foreach (var section in movieSections)
            {
                var start = 0;
                var sectionStartCount = entries.Count;
                int? expectedTotal = null;

                while (true)
                {
                    var sectionKey = Uri.EscapeDataString(section.Key!);

                    // Do not request the legacy includeGuids=1 option here.
                    // On current Plex servers that option can change the response shape
                    // for large libraries and prevent normal pagination/Metadata loading.
                    var envelope = await SendJsonAsync<PlexMediaEnvelope>(
                        $"/library/sections/{sectionKey}/all?type=1",
                        token,
                        start,
                        PageSize);

                    var container = envelope.MediaContainer
                        ?? throw new InvalidOperationException(
                            $"Plex returned no MediaContainer for movie library '{section.Title ?? section.Key}'.");

                    expectedTotal ??= container.TotalSize;

                    var items = container.Metadata ?? new List<PlexMovieDto>();

                    foreach (var item in items)
                    {
                        entries.Add(CreateLibraryEntry(item, section.Title));
                    }

                    if (items.Count == 0)
                    {
                        break;
                    }

                    start += items.Count;

                    if (container.TotalSize is int totalSize)
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

                var sectionLoadedCount = entries.Count - sectionStartCount;

                if (expectedTotal is int expected && sectionLoadedCount != expected)
                {
                    throw new InvalidOperationException(
                        $"Plex library '{section.Title ?? section.Key}' reported {expected} movies, but PIM loaded {sectionLoadedCount}. Validation would be incomplete.");
                }

                _logger.LogInformation(
                    "Loaded {LoadedMovieCount} movie(s) from Plex library {LibraryTitle} (section {SectionKey}).",
                    sectionLoadedCount,
                    section.Title ?? "<untitled>",
                    section.Key);
            }

            if (entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "Plex returned movie library sections, but PIM loaded zero movie entries.");
            }

            var imdbEntryCount = entries.Count(entry => !string.IsNullOrWhiteSpace(entry.ImdbId));

            _logger.LogInformation(
                "Plex movie index loaded successfully: {MovieCount} movie entries across {LibraryCount} libraries; {ImdbCount} entries include an IMDb ID.",
                entries.Count,
                movieSections.Count,
                imdbEntryCount);

            return entries;
        }

        private async Task<T> SendJsonAsync<T>(
            string path,
            string token,
            int? containerStart = null,
            int? containerSize = null)
        {
            var baseUri = new Uri(GetBaseUrl().TrimEnd('/') + "/", UriKind.Absolute);
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

        private string GetBaseUrl()
        {
            var baseUrl = _configuration["Plex:BaseUrl"]?.Trim();
            return string.IsNullOrWhiteSpace(baseUrl)
                ? "http://localhost:32400"
                : baseUrl;
        }

        private static PlexLibraryEntry CreateLibraryEntry(
            PlexMovieDto item,
            string? libraryTitle)
        {
            var paths = item.Media?
                .SelectMany(media => media.Part ?? new List<PlexPartDto>())
                .Select(part => NormalizePath(part.File))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            // Prefer explicit external GUID data when Plex provides it.
            var imdbId = item.Guids?
                .Select(guid => NormalizeImdbId(guid.Id))
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

            imdbId ??= NormalizeImdbId(item.PrimaryGuid);

            // Normal Plex library listings do not consistently expose external
            // IMDb GUIDs. Plex-friendly media paths often do contain the canonical
            // {imdb-tt1234567} tag, so use the path as a strong identity fallback.
            imdbId ??= paths
                .Select(NormalizeImdbId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

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
                " ",
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
