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
        private const double MinimumFuzzyTitleSimilarity = 0.78;
        private const double MinimumSuggestedTitleSimilarity = 0.65;
        private const double MinimumAutomaticFuzzyConfidence = 85;
        private const double MinimumWinnerMargin = 8;
        private const int MaximumSearchPages = 2;

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
            movie.SuggestedTitle = null;
            movie.SuggestedYear = null;
            movie.SuggestedImdbId = null;
            movie.IsFuzzyMatch = false;
            movie.MetadataMatchOrigin = MetadataMatchOrigin.None;
            movie.MetadataDiscoveryReason = null;

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
                    if (ClassifyOmdbFailure(data.Error) ==
                        MetadataLookupFailureType.MovieNotFound)
                    {
                        if (hasImdbId)
                        {
                            await TryInvalidImdbRecoveryAsync(
                                movie,
                                parsedTitle,
                                parsedYear,
                                originalImdbId!,
                                data.Error);
                        }
                        else
                        {
                            await TryRecoveryFallbackAsync(
                                movie,
                                parsedTitle!,
                                parsedYear,
                                data.Error);
                        }

                        return;
                    }

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

                if (hasImdbId &&
                    MarkImdbIdentityConflictIfNeeded(
                        movie,
                        data,
                        parsedTitle,
                        parsedYear,
                        originalImdbId!))
                {
                    return;
                }

                ApplySuccessfulResponse(
                    movie,
                    data,
                    parsedTitle,
                    parsedYear,
                    originalImdbId,
                    hasImdbId,
                    null,
                    hasImdbId
                        ? MetadataMatchOrigin.ImdbId
                        : MetadataMatchOrigin.ExactTitleYear,
                    hasImdbId
                        ? "Matched by the IMDb ID supplied in the source identity."
                        : "Matched by exact OMDb title and year lookup.");
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

        private async Task TryInvalidImdbRecoveryAsync(
            Movie movie,
            string? parsedTitle,
            int? parsedYear,
            string invalidImdbId,
            string? imdbNotFoundError)
        {
            _logger.LogInformation(
                "Supplied IMDb ID {ImdbId} did not resolve; attempting exact title/year recovery.",
                invalidImdbId);

            if (string.IsNullOrWhiteSpace(parsedTitle))
            {
                MarkLookupFailure(
                    movie,
                    MetadataLookupFailureType.MovieNotFound,
                    "The supplied IMDb ID did not resolve and no parsed title was available for recovery.",
                    "The provided IMDb ID could not be found, and no title was available for recovery.");
                return;
            }

            var url =
                $"https://www.omdbapi.com/?t={Uri.EscapeDataString(parsedTitle)}";

            if (parsedYear.HasValue)
                url += $"&y={parsedYear.Value}";

            url += $"&type=movie&apikey={_apiKey}";
            var data = await FetchJsonAsync<OmdbResponse>(url, movie);

            if (data == null)
                return;

            if (string.IsNullOrWhiteSpace(data.Response))
            {
                MarkMalformedResponse(movie);
                return;
            }

            if (data.Response.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                if (ClassifyOmdbFailure(data.Error) ==
                    MetadataLookupFailureType.MovieNotFound)
                {
                    _logger.LogInformation(
                        "Exact title/year recovery after unresolved IMDb ID returned movie-not-found; continuing to title-only and bounded typo recovery.");
                    await TryRecoveryFallbackAsync(
                        movie,
                        parsedTitle,
                        parsedYear,
                        imdbNotFoundError);
                    return;
                }

                MarkOmdbFailure(movie, data.Error);
                return;
            }

            if (!data.Response.Equals("True", StringComparison.OrdinalIgnoreCase))
            {
                MarkMalformedResponse(movie);
                return;
            }

            ApplySuccessfulResponse(
                movie,
                data,
                parsedTitle,
                parsedYear,
                null,
                false,
                null,
                MetadataMatchOrigin.ExactTitleYear,
                $"Recovered by exact title/year after supplied IMDb ID {invalidImdbId} did not resolve.");

            _logger.LogInformation(
                "Recovered metadata identity {RecoveredImdbId} by exact title/year after supplied IMDb ID {InvalidImdbId} did not resolve.",
                movie.ImdbId,
                invalidImdbId);
        }

        private bool MarkImdbIdentityConflictIfNeeded(
            Movie movie,
            OmdbResponse data,
            string? parsedTitle,
            int? parsedYear,
            string originalImdbId)
        {
            var metadataTitle = NormalizeMetadataValue(data.Title);
            var metadataYear = ParseYear(data.Year);
            var returnedImdbId = NormalizeMetadataValue(data.ImdbID) ?? originalImdbId;
            var titleConflicts = !string.IsNullOrWhiteSpace(parsedTitle) &&
                                 !string.IsNullOrWhiteSpace(metadataTitle) &&
                                 !NormalizeTitle(parsedTitle).Equals(
                                     NormalizeTitle(metadataTitle),
                                     StringComparison.Ordinal);
            var yearConflicts = parsedYear.HasValue &&
                                metadataYear.HasValue &&
                                parsedYear.Value != metadataYear.Value;

            if (!titleConflicts && !yearConflicts)
            {
                _logger.LogInformation(
                    "Supplied IMDb ID {ImdbId} resolved and validated against the parsed title/year.",
                    originalImdbId);
                return false;
            }

            var reviewReason = titleConflicts && yearConflicts
                ? "The provided IMDb ID does not match the movie title or year provided."
                : titleConflicts
                    ? "The provided IMDb ID does not match the movie title provided."
                    : "The provided IMDb ID does not match the movie year provided.";
            var providedIdentity =
                $"{parsedTitle ?? "title unavailable"} " +
                $"({parsedYear?.ToString() ?? "year unknown"}) — {originalImdbId}";
            var resolvedIdentity =
                $"{metadataTitle ?? "title unavailable"} " +
                $"({metadataYear?.ToString() ?? "year unknown"}) — {returnedImdbId}";
            var diagnostic =
                $"Provided: {providedIdentity}. IMDb ID identifies: {resolvedIdentity}.";

            movie.MetadataFetched = false;
            movie.MetadataMatchedByImdbId = true;
            movie.SuggestedTitle = metadataTitle;
            movie.SuggestedYear = metadataYear;
            movie.SuggestedImdbId = returnedImdbId;
            movie.MatchConfidence = CalculateConfidence(
                parsedTitle,
                parsedYear,
                metadataTitle ?? string.Empty,
                metadataYear,
                true);
            movie.IsFuzzyMatch = false;
            movie.MetadataMatchOrigin = MetadataMatchOrigin.ImdbId;
            movie.MetadataDiscoveryReason =
                "The supplied IMDb ID resolved, but its canonical identity conflicts with the parsed filename identity.";
            movie.SetMetadataReview(
                reviewReason,
                MetadataLookupFailureType.ImdbIdentityConflict,
                diagnostic);
            movie.Status = "Needs Review";

            _logger.LogWarning(
                "IMDb identity conflict for supplied ID {ImdbId}. Parsed title/year: '{ParsedTitle}' ({ParsedYear}); OMDb title/year: '{MetadataTitle}' ({MetadataYear}).",
                originalImdbId,
                parsedTitle,
                parsedYear,
                metadataTitle,
                metadataYear);
            return true;
        }

        private async Task TryRecoveryFallbackAsync(
            Movie movie,
            string parsedTitle,
            int? parsedYear,
            string? originalNotFoundError)
        {
            _logger.LogInformation(
                "Exact OMDb title/year lookup returned movie-not-found; starting bounded recovery discovery.");

            if (parsedYear.HasValue &&
                await TryTitleOnlyLookupAsync(movie, parsedTitle, parsedYear) ==
                FallbackOutcome.Completed)
            {
                return;
            }

            IReadOnlyList<TitleSpellingCandidateGenerator.SpellingCandidate>
                spellingCandidates;

            try
            {
                spellingCandidates =
                    TitleSpellingCandidateGenerator.Generate(parsedTitle);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException ||
                ex is IOException)
            {
                MarkLookupFailure(
                    movie,
                    MetadataLookupFailureType.OmdbError,
                    "Local spelling candidate generation was unavailable.",
                    "OMDb recovery stopped because local spelling candidate generation was unavailable");
                return;
            }

            if (await TrySpellingCorrectedLookupsAsync(
                    movie,
                    parsedTitle,
                    parsedYear,
                    spellingCandidates) == FallbackOutcome.Completed)
            {
                return;
            }

            if (await TryFuzzyCandidateSearchAsync(
                    movie,
                    parsedTitle,
                    parsedYear,
                    yearRelaxed: false) == FallbackOutcome.Completed)
            {
                return;
            }

            if (await TryFuzzyCandidateSearchAsync(
                    movie,
                    parsedTitle,
                    parsedYear,
                    yearRelaxed: true) == FallbackOutcome.Completed)
            {
                return;
            }

            MarkOmdbFailure(movie, originalNotFoundError);
        }

        private async Task<FallbackOutcome> TryTitleOnlyLookupAsync(
            Movie movie,
            string parsedTitle,
            int? parsedYear)
        {
            var url =
                $"https://www.omdbapi.com/?t={Uri.EscapeDataString(parsedTitle)}" +
                $"&type=movie&apikey={_apiKey}";
            var data = await FetchJsonAsync<OmdbResponse>(url, movie);

            if (data == null)
                return FallbackOutcome.Completed;

            if (string.IsNullOrWhiteSpace(data.Response))
            {
                MarkMalformedResponse(movie);
                return FallbackOutcome.Completed;
            }

            if (data.Response.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                if (ClassifyOmdbFailure(data.Error) ==
                    MetadataLookupFailureType.MovieNotFound)
                {
                    return FallbackOutcome.NoCandidate;
                }

                MarkOmdbFailure(movie, data.Error);
                return FallbackOutcome.Completed;
            }

            if (!data.Response.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                !TryScoreResponse(parsedTitle, parsedYear, data, null, out var candidate))
            {
                MarkMalformedResponse(movie);
                return FallbackOutcome.Completed;
            }

            var explanation = candidate.YearConflicts
                ? $"the filename year is {parsedYear} but OMDb reports " +
                  $"{candidate.Year?.ToString() ?? "an unknown year"}; the candidate was found after retrying without the year"
                : candidate.YearMatches
                    ? "the candidate was found only after retrying without the year"
                    : "the candidate year is unavailable and the match was found after retrying without the year";
            var failureType = candidate.YearConflicts
                ? MetadataLookupFailureType.FuzzyCandidateYearConflict
                : MetadataLookupFailureType.FuzzyCandidateNeedsReview;

            MarkCandidateForReview(
                movie,
                candidate,
                failureType,
                explanation,
                MetadataMatchOrigin.YearRelaxedTitle,
                "Candidate found by retrying the same parsed title without the filename year; manual confirmation is required.");
            return FallbackOutcome.Completed;
        }

        private async Task<FallbackOutcome> TrySpellingCorrectedLookupsAsync(
            Movie movie,
            string parsedTitle,
            int? parsedYear,
            IReadOnlyList<TitleSpellingCandidateGenerator.SpellingCandidate> spellingCandidates)
        {
            if (spellingCandidates.Count == 0)
                return FallbackOutcome.NoCandidate;

            var matches = new List<SpellingMatch>();

            foreach (var spellingCandidate in spellingCandidates)
            {
                var url =
                    $"https://www.omdbapi.com/?t={Uri.EscapeDataString(spellingCandidate.Title)}";

                if (parsedYear.HasValue)
                    url += $"&y={parsedYear.Value}";

                url += $"&type=movie&apikey={_apiKey}";
                var data = await FetchJsonAsync<OmdbResponse>(url, movie);

                if (data == null)
                    return FallbackOutcome.Completed;

                if (string.IsNullOrWhiteSpace(data.Response))
                {
                    MarkMalformedResponse(movie);
                    return FallbackOutcome.Completed;
                }

                if (data.Response.Equals("False", StringComparison.OrdinalIgnoreCase))
                {
                    if (ClassifyOmdbFailure(data.Error) ==
                        MetadataLookupFailureType.MovieNotFound)
                    {
                        continue;
                    }

                    MarkOmdbFailure(movie, data.Error);
                    return FallbackOutcome.Completed;
                }

                if (!data.Response.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                    !TryScoreResponse(
                        parsedTitle,
                        parsedYear,
                        data,
                        spellingCandidate.EditDistance,
                        out var scoredCandidate))
                {
                    MarkMalformedResponse(movie);
                    return FallbackOutcome.Completed;
                }

                matches.Add(new SpellingMatch(
                    spellingCandidate,
                    scoredCandidate,
                    data));
            }

            if (matches.Count == 0)
                return FallbackOutcome.NoCandidate;

            var rankedMatches = matches
                .OrderByDescending(match => match.Candidate.Confidence)
                .ThenByDescending(match => match.Candidate.TitleSimilarity)
                .ThenBy(match => match.Spelling.EditDistance)
                .ToList();
            var winner = rankedMatches[0];
            var runnerUp = rankedMatches.Skip(1).FirstOrDefault();
            var winnerMargin = runnerUp == null
                ? 100
                : winner.Candidate.Confidence - runnerUp.Candidate.Confidence;
            var hasClearWinner = winnerMargin >= MinimumWinnerMargin;
            var canAutomaticallyAccept = winner.Candidate.YearMatches &&
                                         winner.Candidate.TitleSimilarity >=
                                         MinimumFuzzyTitleSimilarity &&
                                         winner.Candidate.Confidence >=
                                         MinimumAutomaticFuzzyConfidence &&
                                         hasClearWinner;

            if (!canAutomaticallyAccept)
            {
                var failureType = winner.Candidate.YearConflicts
                    ? MetadataLookupFailureType.FuzzyCandidateYearConflict
                    : !hasClearWinner
                        ? MetadataLookupFailureType.AmbiguousFuzzyCandidates
                        : MetadataLookupFailureType.FuzzyCandidateNeedsReview;
                var explanation = winner.Candidate.YearConflicts
                    ? "the candidate release year conflicts with the parsed year"
                    : !hasClearWinner
                        ? "another spelling correction produced a candidate that scored too closely"
                        : "the spelling-corrected candidate did not meet the automatic-match threshold";

                MarkCandidateForReview(
                    movie,
                    winner.Candidate,
                    failureType,
                    explanation,
                    MetadataMatchOrigin.SpellCorrectedTitleYear,
                    $"Candidate found after a local one-edit spelling correction from '{parsedTitle}' to '{winner.Spelling.Title}'.");
                return FallbackOutcome.Completed;
            }

            ApplySuccessfulResponse(
                movie,
                winner.Data,
                parsedTitle,
                parsedYear,
                null,
                false,
                winner.Candidate.Confidence,
                MetadataMatchOrigin.SpellCorrectedTitleYear,
                $"Matched after a local one-edit spelling correction from '{parsedTitle}' to '{winner.Spelling.Title}'.");

            _logger.LogInformation(
                "Matched parsed title '{ParsedTitle}' to '{MatchedTitle}' ({Year}) after spelling correction at {Confidence}% confidence.",
                parsedTitle,
                winner.Candidate.Title,
                winner.Candidate.Year,
                winner.Candidate.Confidence);
            return FallbackOutcome.Completed;
        }

        private async Task<FallbackOutcome> TryFuzzyCandidateSearchAsync(
            Movie movie,
            string parsedTitle,
            int? parsedYear,
            bool yearRelaxed)
        {
            _logger.LogInformation(
                "Starting bounded OMDb fuzzy candidate search ({YearPolicy}).",
                yearRelaxed ? "year relaxed" : "year constrained");

            var candidates = new Dictionary<string, OmdbSearchItem>(
                StringComparer.OrdinalIgnoreCase);
            var allCandidateResultsEvaluated = true;

            var candidateQueries = BuildCandidateQueries(parsedTitle).ToList();

            for (var queryIndex = 0; queryIndex < candidateQueries.Count; queryIndex++)
            {
                var query = candidateQueries[queryIndex];
                var queryProducedPlausibleCandidate = false;

                for (var page = 1; page <= MaximumSearchPages; page++)
                {
                    var url =
                        $"https://www.omdbapi.com/?s={Uri.EscapeDataString(query)}" +
                        $"&type=movie&page={page}";

                    if (!yearRelaxed && parsedYear.HasValue)
                        url += $"&y={parsedYear.Value}";

                    url += $"&apikey={_apiKey}";
                    var search = await FetchJsonAsync<OmdbSearchResponse>(url, movie);

                    if (search == null)
                        return FallbackOutcome.Completed;

                    if (string.IsNullOrWhiteSpace(search.Response))
                    {
                        MarkMalformedResponse(movie);
                        return FallbackOutcome.Completed;
                    }

                    if (search.Response.Equals(
                            "False",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (ClassifyOmdbFailure(search.Error) ==
                            MetadataLookupFailureType.MovieNotFound)
                        {
                            break;
                        }

                        MarkOmdbFailure(movie, search.Error);
                        return FallbackOutcome.Completed;
                    }

                    if (!search.Response.Equals(
                            "True",
                            StringComparison.OrdinalIgnoreCase) ||
                        search.Search == null)
                    {
                        MarkMalformedResponse(movie);
                        return FallbackOutcome.Completed;
                    }

                    foreach (var candidate in search.Search.Where(IsUsableMovieCandidate))
                    {
                        candidates.TryAdd(candidate.ImdbID, candidate);

                        if (CalculateTitleSimilarity(parsedTitle, candidate.Title) >=
                            MinimumSuggestedTitleSimilarity)
                        {
                            queryProducedPlausibleCandidate = true;
                        }
                    }

                    if (!int.TryParse(search.TotalResults, out var totalResults) ||
                        totalResults < 0)
                    {
                        MarkMalformedResponse(movie);
                        return FallbackOutcome.Completed;
                    }

                    var evaluatedResults = page * 10;

                    if (totalResults > evaluatedResults &&
                        page == MaximumSearchPages)
                    {
                        allCandidateResultsEvaluated = false;
                    }

                    if (totalResults <= evaluatedResults || search.Search.Count == 0)
                        break;
                }

                if (queryProducedPlausibleCandidate)
                    break;
            }

            var rankedCandidates = candidates.Values
                .Select(candidate => ScoreCandidate(parsedTitle, parsedYear, candidate))
                .Where(candidate =>
                    candidate.TitleSimilarity >= MinimumSuggestedTitleSimilarity)
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenByDescending(candidate => candidate.TitleSimilarity)
                .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (rankedCandidates.Count == 0)
                return FallbackOutcome.NoCandidate;

            var winner = rankedCandidates[0];
            var runnerUp = rankedCandidates.Skip(1).FirstOrDefault();
            var winnerMargin = runnerUp == null
                ? 100
                : winner.Confidence - runnerUp.Confidence;
            var hasClearWinner = allCandidateResultsEvaluated &&
                                 winnerMargin >= MinimumWinnerMargin;
            var canAutomaticallyAccept = !yearRelaxed &&
                                         winner.YearMatches &&
                                         winner.TitleSimilarity >=
                                         MinimumFuzzyTitleSimilarity &&
                                         winner.Confidence >=
                                         MinimumAutomaticFuzzyConfidence &&
                                         hasClearWinner;

            if (!canAutomaticallyAccept)
            {
                var failureType = winner.YearConflicts
                    ? MetadataLookupFailureType.FuzzyCandidateYearConflict
                    : !hasClearWinner
                        ? MetadataLookupFailureType.AmbiguousFuzzyCandidates
                        : MetadataLookupFailureType.FuzzyCandidateNeedsReview;
                var explanation = yearRelaxed && winner.YearConflicts
                    ? $"the filename year is {parsedYear} but OMDb reports {winner.Year}; the candidate was found after retrying fuzzy discovery without the year"
                    : yearRelaxed
                        ? "the candidate was found only after retrying fuzzy discovery without the year"
                        : winner.YearConflicts
                    ? "the candidate release year conflicts with the parsed year"
                    : !allCandidateResultsEvaluated
                        ? "the candidate search returned more results than the bounded search evaluated"
                        : !hasClearWinner
                            ? "another candidate scored too closely"
                            : "the candidate did not meet the automatic-match threshold";

                MarkCandidateForReview(
                    movie,
                    winner,
                    failureType,
                    explanation,
                    yearRelaxed
                        ? MetadataMatchOrigin.YearRelaxedFuzzySearch
                        : MetadataMatchOrigin.FuzzySearchTitleYear,
                    yearRelaxed
                        ? "Candidate found by bounded fuzzy search after removing the filename year; manual confirmation is required."
                        : "Candidate found by bounded fuzzy title search with the filename year constraint retained.");
                return FallbackOutcome.Completed;
            }

            await ApplyFuzzyWinnerAsync(movie, parsedTitle, parsedYear, winner);
            return FallbackOutcome.Completed;
        }

        private async Task ApplyFuzzyWinnerAsync(
            Movie movie,
            string parsedTitle,
            int? parsedYear,
            ScoredCandidate winner)
        {
            var url =
                $"https://www.omdbapi.com/?i={Uri.EscapeDataString(winner.ImdbId)}" +
                $"&type=movie&apikey={_apiKey}";
            var data = await FetchJsonAsync<OmdbResponse>(url, movie);

            if (data == null)
                return;

            if (string.IsNullOrWhiteSpace(data.Response))
            {
                MarkMalformedResponse(movie);
                return;
            }

            if (data.Response.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                MarkOmdbFailure(movie, data.Error);
                return;
            }

            if (!data.Response.Equals("True", StringComparison.OrdinalIgnoreCase))
            {
                MarkMalformedResponse(movie);
                return;
            }

            var returnedImdbId = NormalizeMetadataValue(data.ImdbID);
            var returnedYear = ParseYear(data.Year);

            if (!string.Equals(
                    returnedImdbId,
                    winner.ImdbId,
                    StringComparison.OrdinalIgnoreCase) ||
                returnedYear != winner.Year ||
                (parsedYear.HasValue && returnedYear != parsedYear))
            {
                MarkCandidateForReview(
                    movie,
                    winner,
                    MetadataLookupFailureType.FuzzyCandidateNeedsReview,
                    "the OMDb detail response did not agree with the selected candidate identity",
                    MetadataMatchOrigin.FuzzySearchTitleYear,
                    "Candidate found by bounded fuzzy title search with the filename year constraint retained.");
                return;
            }

            ApplySuccessfulResponse(
                movie,
                data,
                parsedTitle,
                parsedYear,
                null,
                false,
                winner.Confidence,
                MetadataMatchOrigin.FuzzySearchTitleYear,
                "Matched by bounded fuzzy title search with the filename year constraint retained.");
            movie.SuggestedTitle = winner.Title;
            movie.SuggestedYear = winner.Year;
            movie.SuggestedImdbId = winner.ImdbId;
        }

        private async Task<T?> FetchJsonAsync<T>(string url, Movie movie)
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
                return default;
            }

            string json;

            try
            {
                json = await response.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException)
            {
                MarkTimeoutFailure(movie);
                return default;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
            {
                MarkNetworkFailure(movie);
                return default;
            }

            try
            {
                var result = JsonSerializer.Deserialize<T>(json);

                if (result != null)
                    return result;
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
            {
                // Converted to the same fail-closed review state below.
            }

            MarkMalformedResponse(movie);
            return default;
        }

        private static IEnumerable<string> BuildCandidateQueries(string parsedTitle)
        {
            yield return parsedTitle;

            var tokens = Regex.Matches(parsedTitle, @"[A-Za-z0-9]+")
                .Select(match => match.Value)
                .Where(token => !token.Equals("the", StringComparison.OrdinalIgnoreCase) &&
                                !token.Equals("a", StringComparison.OrdinalIgnoreCase) &&
                                !token.Equals("an", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (tokens.Count == 0)
                yield break;

            var broadQuery = tokens.Count == 1
                ? ShortenSingleToken(tokens[0])
                : tokens.OrderByDescending(token => token.Length).First();

            if (!broadQuery.Equals(parsedTitle, StringComparison.OrdinalIgnoreCase))
                yield return broadQuery;
        }

        private static string ShortenSingleToken(string token)
        {
            var charactersToRemove = token.Length >= 8 ? 2 : 1;

            return token.Length - charactersToRemove >= 5
                ? token[..^charactersToRemove]
                : token;
        }

        private static bool IsUsableMovieCandidate(OmdbSearchItem? candidate)
        {
            return candidate != null &&
                   string.Equals(
                       candidate.Type,
                       "movie",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(candidate.Title) &&
                   !string.IsNullOrWhiteSpace(candidate.ImdbID);
        }

        private static ScoredCandidate ScoreCandidate(
            string parsedTitle,
            int? parsedYear,
            OmdbSearchItem candidate,
            int? spellingEditDistance = null)
        {
            var candidateYear = ParseYear(candidate.Year);
            var titleSimilarity = CalculateTitleSimilarity(parsedTitle, candidate.Title);
            var yearMatches = parsedYear.HasValue &&
                              candidateYear.HasValue &&
                              parsedYear.Value == candidateYear.Value;
            var yearConflicts = parsedYear.HasValue &&
                                candidateYear.HasValue &&
                                parsedYear.Value != candidateYear.Value;
            var confidence = yearMatches
                ? Math.Round((titleSimilarity * 80) + 20, 0)
                : yearConflicts
                    ? Math.Round(titleSimilarity * 70, 0)
                    : Math.Round(titleSimilarity * 80, 0);

            if (spellingEditDistance > 1)
                confidence = Math.Max(0, confidence - ((spellingEditDistance.Value - 1) * 5));

            return new ScoredCandidate(
                candidate.Title.Trim(),
                candidateYear,
                candidate.ImdbID.Trim(),
                titleSimilarity,
                confidence,
                yearMatches,
                yearConflicts,
                spellingEditDistance);
        }

        private static bool TryScoreResponse(
            string parsedTitle,
            int? parsedYear,
            OmdbResponse data,
            int? spellingEditDistance,
            out ScoredCandidate candidate)
        {
            var title = NormalizeMetadataValue(data.Title);
            var imdbId = NormalizeMetadataValue(data.ImdbID);

            if (title == null || imdbId == null)
            {
                candidate = default!;
                return false;
            }

            candidate = ScoreCandidate(
                parsedTitle,
                parsedYear,
                new OmdbSearchItem
                {
                    Title = title,
                    Year = data.Year,
                    ImdbID = imdbId,
                    Type = "movie"
                },
                spellingEditDistance);
            return true;
        }

        private static void MarkCandidateForReview(
            Movie movie,
            ScoredCandidate candidate,
            MetadataLookupFailureType failureType,
            string explanation,
            MetadataMatchOrigin matchOrigin,
            string discoveryReason)
        {
            var candidateYear = candidate.Year?.ToString() ?? "year unknown";
            var reason =
                $"Possible OMDb match: {candidate.Title} ({candidateYear}) " +
                $"({candidate.Confidence:0}% confidence). {discoveryReason} " +
                $"Manual confirmation required because {explanation}.";

            movie.MetadataFetched = false;
            movie.MetadataMatchedByImdbId = false;
            movie.SuggestedTitle = candidate.Title;
            movie.SuggestedYear = candidate.Year;
            movie.SuggestedImdbId = candidate.ImdbId;
            movie.MatchConfidence = candidate.Confidence;
            movie.IsFuzzyMatch = true;
            movie.MetadataMatchOrigin = matchOrigin;
            movie.MetadataDiscoveryReason = discoveryReason;
            movie.SetMetadataReview(reason, failureType, explanation);
            movie.Status = "Needs Review";
        }

        private void ApplySuccessfulResponse(
            Movie movie,
            OmdbResponse data,
            string? parsedTitle,
            int? parsedYear,
            string? originalImdbId,
            bool hasImdbId,
            double? acceptedFuzzyConfidence,
            MetadataMatchOrigin matchOrigin,
            string discoveryReason)
        {
            var metadataYear = ParseYear(data.Year);
            var metadataTitle = NormalizeMetadataValue(data.Title);
            var returnedImdbId = NormalizeMetadataValue(data.ImdbID);

            movie.SuggestedTitle = metadataTitle;
            movie.SuggestedYear = metadataYear;
            movie.SuggestedImdbId = returnedImdbId;
            movie.MatchConfidence = CalculateConfidence(
                parsedTitle,
                parsedYear,
                metadataTitle ?? string.Empty,
                metadataYear,
                hasImdbId);
            if (acceptedFuzzyConfidence.HasValue)
                movie.MatchConfidence = acceptedFuzzyConfidence.Value;

            movie.IsFuzzyMatch = acceptedFuzzyConfidence.HasValue;
            movie.MetadataMatchedByImdbId = hasImdbId;
            movie.MetadataMatchOrigin = matchOrigin;
            movie.MetadataDiscoveryReason = discoveryReason;
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

            // IMDb matches reach this point only after their canonical title/year
            // have been validated against the parsed filename identity.
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

            var failureType = ClassifyOmdbFailure(error);

            MarkLookupFailure(
                movie,
                failureType,
                error,
                $"OMDb lookup failed: {error}");
        }

        private static MetadataLookupFailureType ClassifyOmdbFailure(string? error)
        {
            if (error?.Contains("request limit", StringComparison.OrdinalIgnoreCase) == true ||
                error?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true)
            {
                return MetadataLookupFailureType.RequestLimitReached;
            }

            if (error?.Contains("api key", StringComparison.OrdinalIgnoreCase) == true)
                return MetadataLookupFailureType.InvalidApiKey;

            if (error?.TrimStart().StartsWith(
                    "Movie not found",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return MetadataLookupFailureType.MovieNotFound;
            }

            return MetadataLookupFailureType.OmdbError;
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
            movie.MetadataMatchOrigin = MetadataMatchOrigin.None;
            movie.MetadataDiscoveryReason = null;
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

            normalized = normalized.Replace("&", " and ", StringComparison.Ordinal);
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

                    if (i > 1 &&
                        j > 1 &&
                        left[i - 1] == right[j - 2] &&
                        left[i - 2] == right[j - 1])
                    {
                        matrix[i, j] = Math.Min(
                            matrix[i, j],
                            matrix[i - 2, j - 2] + 1);
                    }
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

        private class OmdbSearchResponse
        {
            public List<OmdbSearchItem>? Search { get; set; }

            [JsonPropertyName("totalResults")]
            public string TotalResults { get; set; } = string.Empty;

            public string Response { get; set; } = string.Empty;

            public string Error { get; set; } = string.Empty;
        }

        private class OmdbSearchItem
        {
            public string Title { get; set; } = string.Empty;

            public string Year { get; set; } = string.Empty;

            [JsonPropertyName("imdbID")]
            public string ImdbID { get; set; } = string.Empty;

            public string Type { get; set; } = string.Empty;
        }

        private sealed record ScoredCandidate(
            string Title,
            int? Year,
            string ImdbId,
            double TitleSimilarity,
            double Confidence,
            bool YearMatches,
            bool YearConflicts,
            int? SpellingEditDistance);

        private sealed record SpellingMatch(
            TitleSpellingCandidateGenerator.SpellingCandidate Spelling,
            ScoredCandidate Candidate,
            OmdbResponse Data);

        private enum FallbackOutcome
        {
            NoCandidate,
            Completed
        }
    }
}
