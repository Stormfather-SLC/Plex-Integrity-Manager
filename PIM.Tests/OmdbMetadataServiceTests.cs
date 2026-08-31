using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Parsing;
using Xunit;

namespace PIM.Tests;

public sealed class OmdbMetadataServiceTests
{
    private const string SuccessfulResponse = """
    {
      "Title": "Inception",
      "Year": "2010",
      "Rated": "PG-13",
      "Genre": "Action, Adventure, Sci-Fi",
      "imdbID": "tt1375666",
      "Response": "True"
    }
    """;

    [Fact]
    public async Task EnrichAsync_SuccessfulTitleYearLookup_PopulatesRequiredIdentity()
    {
        var service = CreateService(Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie { Title = "Inception", Year = 2010 };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.MetadataMatchedByImdbId);
        Assert.Equal("tt1375666", movie.ImdbId);
        Assert.Equal("Inception", movie.Title);
        Assert.Equal(2010, movie.Year);
        Assert.Equal("PG-13", movie.MpaRating);
        Assert.Equal("Action", movie.PrimaryGenre);
        Assert.False(movie.NeedsReview);
        Assert.Null(movie.ReviewReason);
        Assert.Equal(
            MetadataLookupFailureType.None,
            movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_FailedTitleLookup_PreservesOmdbErrorWithoutApiKey()
    {
        const string apiKey = "secret-test-key";
        var handler = new StubHttpMessageHandler(new[]
        {
                Response(HttpStatusCode.OK, """
                {
                  "Response": "False",
                  "Error": "Movie not found for key secret-test-key!"
                }
                """),
                Response(HttpStatusCode.OK, """
                {
                  "Response": "False",
                  "Error": "Movie not found!"
                }
                """),
                Response(HttpStatusCode.OK, """
                {
                  "Response": "False",
                  "Error": "Movie not found!"
                }
                """),
                MovieNotFoundResponse(),
                MovieNotFoundResponse(),
            MovieNotFoundResponse()
        });
        var service = CreateService(handler, apiKey);
        var movie = new Movie { Title = "Not A Real Movie", Year = 2010 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Contains("Movie not found", movie.ReviewReason);
        Assert.DoesNotContain(apiKey, movie.ReviewReason);
        Assert.Equal(
            MetadataLookupFailureType.MovieNotFound,
            movie.MetadataLookupFailureType);
        Assert.DoesNotContain(apiKey, movie.MetadataLookupFailureDetail);
        Assert.Equal(6, handler.RequestUris.Count);
    }

    [Fact]
    public async Task EnrichAsync_TransposedTitleWithExactYear_AcceptsClearSpellingWinner()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "The Dark Knight",
              "Year": "2008",
              "Rated": "PG-13",
              "Genre": "Action, Crime, Drama",
              "imdbID": "tt0468569",
              "Response": "True"
            }
            """),
            MovieNotFoundResponse()
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "The Drak Knight", Year = 2008 };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.True(movie.IsFuzzyMatch);
        Assert.False(movie.NeedsReview);
        Assert.Equal("The Dark Knight", movie.Title);
        Assert.Equal(2008, movie.Year);
        Assert.Equal("tt0468569", movie.ImdbId);
        Assert.True(movie.MatchConfidence >= 85);
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.Contains("?t=", handler.RequestUris[0].Query);
        Assert.Contains("y=2008", handler.RequestUris[0].Query);
        Assert.DoesNotContain("&y=", handler.RequestUris[1].Query);
        Assert.Contains("t=The%20Dark%20Knight", handler.RequestUris[2].Query);
        Assert.Contains("y=2008", handler.RequestUris[2].Query);
    }

    [Fact]
    public async Task EnrichAsync_MisspelledSingleWordWithExactYear_UsesBoundedBroaderSearch()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Gladiator",
              "Year": "2000",
              "Rated": "R",
              "Genre": "Action, Adventure, Drama",
              "imdbID": "tt0172495",
              "Response": "True"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "Gladiater", Year = 2000 };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.True(movie.IsFuzzyMatch);
        Assert.False(movie.NeedsReview);
        Assert.Equal("Gladiator", movie.Title);
        Assert.Equal(MetadataMatchOrigin.SpellCorrectedTitleYear, movie.MetadataMatchOrigin);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.Contains("t=Gladiator", handler.RequestUris[2].Query);
        Assert.Contains("y=2000", handler.RequestUris[2].Query);
    }

    [Theory]
    [InlineData("Gladiater.2000.1080p.mkv", "Gladiator", 2000, "tt0172495")]
    [InlineData("Incepton.2010.1080p.mkv", "Inception", 2010, "tt1375666")]
    [InlineData("Interstllar.2014.720p.mkv", "Interstellar", 2014, "tt0816692")]
    [InlineData("Inceptioon.2010.1080p.mkv", "Inception", 2010, "tt1375666")]
    public async Task EnrichAsync_ObviousOneEditFilenameTypo_UsesVerifiedSpellingCorrection(
        string fileName,
        string expectedTitle,
        int expectedYear,
        string expectedImdbId)
    {
        var handler = new RoutingHttpMessageHandler(request =>
        {
            var query = request.RequestUri!.Query;

            if (query.Contains(
                    $"t={Uri.EscapeDataString(expectedTitle)}",
                    StringComparison.OrdinalIgnoreCase) &&
                query.Contains($"&y={expectedYear}", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, $$"""
                {
                  "Title": "{{expectedTitle}}",
                  "Year": "{{expectedYear}}",
                  "Rated": "PG-13",
                  "Genre": "Drama",
                  "imdbID": "{{expectedImdbId}}",
                  "Response": "True"
                }
                """);
            }

            return MovieNotFoundResponse();
        });
        var service = CreateService(handler);
        var movie = new Movie { FileName = fileName };
        new FileNameParser().Parse(movie);

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.NeedsReview);
        Assert.Equal(expectedTitle, movie.Title);
        Assert.Equal(expectedYear, movie.Year);
        Assert.Equal(expectedImdbId, movie.ImdbId);
        Assert.True(movie.MatchConfidence >= 85);
        Assert.Equal(MetadataMatchOrigin.SpellCorrectedTitleYear, movie.MetadataMatchOrigin);
        Assert.Contains("spelling correction", movie.MetadataDiscoveryReason);
        Assert.InRange(handler.RequestUris.Count, 3, 4);
    }

    [Fact]
    public async Task EnrichAsync_TitleOnlyCandidateWithMatchingYear_StillRequiresConfirmation()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Gladiator",
              "Year": "2000",
              "imdbID": "tt0172495",
              "Response": "True"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Gladiater",
            Year = 2000,
            ApprovedForCommit = true
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal("Gladiater", movie.Title);
        Assert.Equal(2000, movie.Year);
        Assert.Null(movie.ImdbId);
        Assert.Equal("Gladiator", movie.SuggestedTitle);
        Assert.Equal(2000, movie.SuggestedYear);
        Assert.Equal("tt0172495", movie.SuggestedImdbId);
        Assert.Equal(MetadataMatchOrigin.YearRelaxedTitle, movie.MetadataMatchOrigin);
        Assert.Contains("manual confirmation", movie.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task EnrichAsync_TitleOnlyCandidateWithoutYear_ShowsBlockedSuggestion()
    {
        var service = CreateService(
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Unknown Year Movie",
              "Year": "N/A",
              "imdbID": "tt1234567",
              "Response": "True"
            }
            """));
        var movie = new Movie { Title = "Unknown Year Movie", Year = 2000 };

        await service.EnrichAsync(movie);

        Assert.True(movie.HasMetadataSuggestion);
        Assert.False(movie.CanAcceptMetadataSuggestion);
        Assert.Null(movie.SuggestedYear);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Contains("year is unavailable", movie.ReviewReason);
    }

    [Fact]
    public async Task EnrichAsync_StrongTitleMatchWithConflictingYear_SuggestsWithoutOverridingIdentity()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Coming to America",
              "Year": "1988",
              "imdbID": "tt0094898",
              "Response": "True"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "Coming To America", Year = 1998 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal("Coming To America", movie.Title);
        Assert.Equal(1998, movie.Year);
        Assert.Null(movie.ImdbId);
        Assert.Equal("Coming to America", movie.SuggestedTitle);
        Assert.Equal(1988, movie.SuggestedYear);
        Assert.Equal("tt0094898", movie.SuggestedImdbId);
        Assert.Equal(
            MetadataLookupFailureType.FuzzyCandidateYearConflict,
            movie.MetadataLookupFailureType);
        Assert.Equal(MetadataMatchOrigin.YearRelaxedTitle, movie.MetadataMatchOrigin);
        Assert.Contains("filename year is 1998", movie.ReviewReason);
        Assert.Contains("retrying without the year", movie.ReviewReason);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("y=1998", handler.RequestUris[0].Query);
        Assert.DoesNotContain("&y=", handler.RequestUris[1].Query);
    }

    [Fact]
    public async Task EnrichAsync_AmbiguousCandidates_SuggestsWinnerWithoutAutomaticAcceptance()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Dark",
              "Year": "2000",
              "imdbID": "tt0100001",
              "Response": "True"
            }
            """),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Draw",
              "Year": "2000",
              "imdbID": "tt0100002",
              "Response": "True"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "Drak", Year = 2000 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Null(movie.ImdbId);
        Assert.NotNull(movie.SuggestedTitle);
        Assert.Equal(
            MetadataLookupFailureType.AmbiguousFuzzyCandidates,
            movie.MetadataLookupFailureType);
        Assert.Contains("too closely", movie.ReviewReason);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task EnrichAsync_AmbiguousFuzzySearchCandidates_RemainNeedsReview()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Search": [
                { "Title": "Batman Returns", "Year": "1992", "imdbID": "tt0103776", "Type": "movie" },
                { "Title": "Batmen Returns", "Year": "1992", "imdbID": "tt9999999", "Type": "movie" }
              ],
              "totalResults": "2",
              "Response": "True"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "Batm4n Returns", Year = 1992 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.NotNull(movie.SuggestedTitle);
        Assert.Equal(
            MetadataLookupFailureType.AmbiguousFuzzyCandidates,
            movie.MetadataLookupFailureType);
        Assert.Equal(MetadataMatchOrigin.FuzzySearchTitleYear, movie.MetadataMatchOrigin);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task EnrichAsync_FuzzyWinnerDetailDisagrees_RemainsNeedsReview()
    {
        var service = CreateService(
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Search": [
                { "Title": "Gladiator", "Year": "2000", "imdbID": "tt0172495", "Type": "movie" }
              ],
              "totalResults": "1",
              "Response": "True"
            }
            """),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Gladiator",
              "Year": "2001",
              "imdbID": "tt0172495",
              "Response": "True"
            }
            """));
        var movie = new Movie { Title = "Gladiat0r", Year = 2000 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal("Gladiat0r", movie.Title);
        Assert.Equal(2000, movie.Year);
        Assert.Null(movie.ImdbId);
        Assert.Equal("Gladiator", movie.SuggestedTitle);
        Assert.Contains("did not agree", movie.ReviewReason);
    }

    [Fact]
    public async Task EnrichAsync_NonNotFoundOmdbFailure_DoesNotTriggerCandidateSearch()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            Response(HttpStatusCode.OK, """
            {
              "Response": "False",
              "Error": "Request limit reached!"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "The Drak Knight", Year = 2008 };

        await service.EnrichAsync(movie);

        Assert.Equal(
            MetadataLookupFailureType.RequestLimitReached,
            movie.MetadataLookupFailureType);
        Assert.Single(handler.RequestUris);
        Assert.DoesNotContain("s=", handler.RequestUris[0].Query);
    }

    [Theory]
    [InlineData("Invalid API key!", MetadataLookupFailureType.InvalidApiKey)]
    [InlineData("Request limit reached!", MetadataLookupFailureType.RequestLimitReached)]
    public async Task EnrichAsync_NonNotFoundTitleOnlyFailure_StopsAllLaterFallbacks(
        string error,
        MetadataLookupFailureType expectedFailure)
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, $$"""
            {
              "Response": "False",
              "Error": "{{error}}"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie { Title = "Gladiater", Year = 2000 };

        await service.EnrichAsync(movie);

        Assert.Equal(expectedFailure, movie.MetadataLookupFailureType);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Query.Contains("?s="));
    }

    [Fact]
    public async Task EnrichAsync_RecoveryRequestCount_IsBoundedAtTwelveTotalRequests()
    {
        var handler = new RoutingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("?s=", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, """
                {
                  "Search": [
                    { "Title": "Entirely Unrelated", "Year": "1980", "imdbID": "tt0000001", "Type": "movie" }
                  ],
                  "totalResults": "20",
                  "Response": "True"
                }
                """);
            }

            return MovieNotFoundResponse();
        }, maximumRequests: 12);
        var service = CreateService(handler);
        var movie = new Movie { Title = "Drak Movi", Year = 2000 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal(MetadataLookupFailureType.MovieNotFound, movie.MetadataLookupFailureType);
        Assert.Equal(12, handler.RequestUris.Count);
    }

    [Fact]
    public async Task EnrichAsync_FailedLookupThenSuccessfulRetry_ClearsMetadataReview()
    {
        var service = CreateService(
            Response(HttpStatusCode.OK, """
            {
              "Response": "False",
              "Error": "Request limit reached!"
            }
            """),
            Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie { Title = "Inception", Year = 2010 };

        await service.EnrichAsync(movie);
        Assert.True(movie.NeedsReview);
        Assert.Contains("Request limit reached", movie.ReviewReason);

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.Equal("tt1375666", movie.ImdbId);
        Assert.False(movie.NeedsReview);
        Assert.Null(movie.ReviewReason);
    }

    [Fact]
    public async Task EnrichAsync_SuccessfulRetry_PreservesUnrelatedReviewReason()
    {
        var service = CreateService(Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie { Title = "Inception", Year = 2010 };
        movie.RequireReview("IMDb ID could not be determined");
        movie.RequireReview("Missing IMDb ID");
        movie.RequireReview("Missing required metadata for rename");
        movie.RequireReview("Low confidence metadata match (42% confidence)");
        movie.RequireReview("Duplicate ambiguity requires human verification");

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal(
            "Duplicate ambiguity requires human verification",
            movie.ReviewReason);
    }

    [Theory]
    [InlineData(
        "Request limit reached!",
        "Request limit reached",
        MetadataLookupFailureType.RequestLimitReached)]
    [InlineData(
        "Invalid API key!",
        "Invalid API key",
        MetadataLookupFailureType.InvalidApiKey)]
    public async Task EnrichAsync_OmdbApiFailure_PreservesSpecificReason(
        string omdbError,
        string expectedReason,
        MetadataLookupFailureType expectedFailureType)
    {
        var service = CreateService(Response(HttpStatusCode.OK, $$"""
        {
          "Response": "False",
          "Error": "{{omdbError}}"
        }
        """));
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.NeedsReview);
        Assert.Contains(expectedReason, movie.ReviewReason);
        Assert.Equal(expectedFailureType, movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_HttpFailure_ProducesUsefulReviewState()
    {
        var service = CreateService(Response(
            HttpStatusCode.ServiceUnavailable,
            "Service unavailable"));
        var movie = new Movie { Title = "Inception", Year = 2010 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Contains("HTTP 503", movie.ReviewReason);
        Assert.Equal(
            MetadataLookupFailureType.HttpFailure,
            movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_NetworkFailure_ProducesUsefulReviewStateWithoutThrowing()
    {
        var service = CreateService(new ThrowingHttpMessageHandler(
            new HttpRequestException("Simulated network failure")));
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Contains("network", movie.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            MetadataLookupFailureType.NetworkFailure,
            movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_Timeout_ProducesUsefulReviewStateWithoutThrowing()
    {
        var service = CreateService(new ThrowingHttpMessageHandler(
            new TaskCanceledException("Simulated timeout")));
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Contains("timed out", movie.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            MetadataLookupFailureType.Timeout,
            movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_MalformedResponse_ProducesUsefulReviewStateWithoutThrowing()
    {
        var service = CreateService(Response(HttpStatusCode.OK, "not-json"));
        var movie = new Movie { Title = "Inception", Year = 2010 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Contains("malformed", movie.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            MetadataLookupFailureType.MalformedResponse,
            movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_SuccessWithoutImdbId_IdentifiesMissingRequiredField()
    {
        var service = CreateService(Response(HttpStatusCode.OK, """
        {
          "Title": "Inception",
          "Year": "2010",
          "Response": "True"
        }
        """));
        var movie = new Movie { Title = "Inception", Year = 2010 };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Contains("IMDb ID", movie.ReviewReason);
        Assert.Equal(
            MetadataLookupFailureType.MissingRequiredFields,
            movie.MetadataLookupFailureType);
    }

    [Fact]
    public async Task EnrichAsync_ValidImdbIdWithMatchingIdentity_EnrichesNormally()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            Response(HttpStatusCode.OK, SuccessfulResponse)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.True(movie.MetadataMatchedByImdbId);
        Assert.False(movie.NeedsReview);
        Assert.Equal("Inception", movie.Title);
        Assert.Equal(2010, movie.Year);
        Assert.Equal("tt1375666", movie.ImdbId);
        Assert.Equal("PG-13", movie.MpaRating);
        Assert.Equal("Action", movie.PrimaryGenre);
        Assert.Equal(MetadataLookupFailureType.None, movie.MetadataLookupFailureType);
        Assert.Single(handler.RequestUris);
        Assert.Contains("?i=tt1375666", handler.RequestUris[0].Query);
    }

    [Theory]
    [InlineData(
        "Some Other Movie",
        2010,
        "The provided IMDb ID does not match the movie title provided.")]
    [InlineData(
        "Inception",
        1999,
        "The provided IMDb ID does not match the movie year provided.")]
    [InlineData(
        "Some Other Movie",
        1999,
        "The provided IMDb ID does not match the movie title or year provided.")]
    public async Task EnrichAsync_ValidImdbIdentityConflict_BlocksWithoutFallbackOrOverwrite(
        string parsedTitle,
        int parsedYear,
        string expectedReason)
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            Response(HttpStatusCode.OK, SuccessfulResponse)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = parsedTitle,
            Year = parsedYear,
            ImdbId = "tt1375666",
            ApprovedForCommit = true
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.MetadataMatchedByImdbId);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal(expectedReason, movie.ReviewReason);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
        Assert.Equal(parsedTitle, movie.Title);
        Assert.Equal(parsedYear, movie.Year);
        Assert.Equal("tt1375666", movie.ImdbId);
        Assert.Equal("Inception", movie.SuggestedTitle);
        Assert.Equal(2010, movie.SuggestedYear);
        Assert.Equal("tt1375666", movie.SuggestedImdbId);
        Assert.Contains("Provided:", movie.MetadataLookupFailureDetail);
        Assert.Contains("IMDb ID identifies:", movie.MetadataLookupFailureDetail);
        Assert.Single(handler.RequestUris);
        Assert.Contains("?i=", handler.RequestUris[0].Query);
    }

    [Fact]
    public async Task EnrichAsync_ValidImdbConflictWithHighNumericalConfidence_StillRequiresReview()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            Response(HttpStatusCode.OK, SuccessfulResponse)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Inceptiom",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MatchConfidence >= 85);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.MetadataFetched);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
        Assert.Single(handler.RequestUris);
    }

    [Theory]
    [InlineData("  INCEPTION!!!  ")]
    [InlineData("The Inception")]
    public async Task EnrichAsync_ValidImdbIdWithHarmlessTitleFormatting_DoesNotConflict(
        string parsedTitle)
    {
        var service = CreateService(Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie
        {
            Title = parsedTitle,
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.NeedsReview);
        Assert.Equal(MetadataLookupFailureType.None, movie.MetadataLookupFailureType);
        Assert.Equal("Inception", movie.Title);
    }

    [Fact]
    public async Task EnrichAsync_ValidImdbIdWithoutParsedYear_DoesNotInventYearConflict()
    {
        var service = CreateService(Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie
        {
            Title = "Inception",
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.NeedsReview);
        Assert.Equal(2010, movie.Year);
    }

    [Fact]
    public async Task EnrichAsync_UnresolvedImdbId_RecoversByExactTitleYearAndReplacesId()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, SuccessfulResponse)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt0000000"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.MetadataMatchedByImdbId);
        Assert.False(movie.NeedsReview);
        Assert.Equal("tt1375666", movie.ImdbId);
        Assert.Equal(MetadataMatchOrigin.ExactTitleYear, movie.MetadataMatchOrigin);
        Assert.Contains("did not resolve", movie.MetadataDiscoveryReason);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("?i=tt0000000", handler.RequestUris[0].Query);
        Assert.Contains("?t=Inception", handler.RequestUris[1].Query);
        Assert.Contains("&y=2010", handler.RequestUris[1].Query);
    }

    [Fact]
    public async Task EnrichAsync_UnresolvedImdbAndTitleYear_TitleOnlyCandidateUsesExistingReviewPolicy()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, SuccessfulResponse)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2011,
            ImdbId = "tt0000000"
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal("Inception", movie.Title);
        Assert.Equal(2011, movie.Year);
        Assert.Equal("tt0000000", movie.ImdbId);
        Assert.Equal("Inception", movie.SuggestedTitle);
        Assert.Equal(2010, movie.SuggestedYear);
        Assert.Equal("tt1375666", movie.SuggestedImdbId);
        Assert.Equal(MetadataMatchOrigin.YearRelaxedTitle, movie.MetadataMatchOrigin);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.Contains("&y=2011", handler.RequestUris[1].Query);
        Assert.DoesNotContain("&y=", handler.RequestUris[2].Query);
    }

    [Fact]
    public async Task EnrichAsync_UnresolvedImdbWithMisspelledTitle_PreservesBoundedTypoRecovery()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            MovieNotFoundResponse(),
            Response(HttpStatusCode.OK, """
            {
              "Title": "Gladiator",
              "Year": "2000",
              "Rated": "R",
              "Genre": "Action, Adventure, Drama",
              "imdbID": "tt0172495",
              "Response": "True"
            }
            """)
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Gladiater",
            Year = 2000,
            ImdbId = "tt0000000"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.NeedsReview);
        Assert.Equal("Gladiator", movie.Title);
        Assert.Equal(2000, movie.Year);
        Assert.Equal("tt0172495", movie.ImdbId);
        Assert.Equal(MetadataMatchOrigin.SpellCorrectedTitleYear, movie.MetadataMatchOrigin);
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.Contains("?i=", handler.RequestUris[0].Query);
        Assert.Contains("&y=2000", handler.RequestUris[1].Query);
        Assert.DoesNotContain("&y=", handler.RequestUris[2].Query);
        Assert.Contains("t=Gladiator", handler.RequestUris[3].Query);
    }

    [Fact]
    public async Task EnrichAsync_UnresolvedImdbWithNoRecovery_RemainsBoundedNeedsReview()
    {
        var handler = new RoutingHttpMessageHandler(
            _ => MovieNotFoundResponse(),
            maximumRequests: 13);
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Drak Movi",
            Year = 2000,
            ImdbId = "tt0000000"
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal("tt0000000", movie.ImdbId);
        Assert.Equal(MetadataLookupFailureType.MovieNotFound, movie.MetadataLookupFailureType);
        Assert.Equal(9, handler.RequestUris.Count);
        Assert.InRange(handler.RequestUris.Count, 1, 13);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EnrichAsync_ImdbHttpFailure_DoesNotFallBackToTitle(HttpStatusCode statusCode)
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            Response(statusCode, "Service failure")
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal(MetadataLookupFailureType.HttpFailure, movie.MetadataLookupFailureType);
        Assert.Single(handler.RequestUris);
        Assert.Contains("?i=", handler.RequestUris[0].Query);
    }

    [Fact]
    public async Task EnrichAsync_MalformedImdbResponse_DoesNotFallBackToTitle()
    {
        var handler = new StubHttpMessageHandler(new[]
        {
            Response(HttpStatusCode.OK, "not-json")
        });
        var service = CreateService(handler);
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };

        await service.EnrichAsync(movie);

        Assert.Equal(MetadataLookupFailureType.MalformedResponse, movie.MetadataLookupFailureType);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task EnrichAsync_SuccessfulImdbLookup_PreservesUnrelatedReview()
    {
        var service = CreateService(Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666"
        };
        movie.RequireReview("Edition requires human verification");

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.True(movie.MetadataMatchedByImdbId);
        Assert.True(movie.NeedsReview);
        Assert.Equal("Edition requires human verification", movie.ReviewReason);
        Assert.Equal("Needs Review", movie.Status);
        Assert.Equal("Inception", movie.Title);
        Assert.Equal(2010, movie.Year);
    }

    private static OmdbMetadataService CreateService(
        HttpMessageHandler handler,
        string apiKey = "test-key")
    {
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Omdb:ApiKey"] = apiKey
            })
            .Build();

        return new OmdbMetadataService(httpClient, configuration);
    }

    private static OmdbMetadataService CreateService(
        params HttpResponseMessage[] responses)
    {
        return CreateService(new StubHttpMessageHandler(responses));
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string responseBody)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody)
        };
    }

    private static HttpResponseMessage MovieNotFoundResponse()
    {
        return Response(HttpStatusCode.OK, """
        {
          "Response": "False",
          "Error": "Movie not found!"
        }
        """);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public List<Uri> RequestUris { get; } = new();

        public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No stubbed OMDb response remains for this test.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private readonly int _maximumRequests;

        public List<Uri> RequestUris { get; } = new();

        public RoutingHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
            int maximumRequests = 12)
        {
            _responseFactory = responseFactory;
            _maximumRequests = maximumRequests;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            if (RequestUris.Count > _maximumRequests)
            {
                throw new InvalidOperationException(
                    $"OMDb recovery exceeded the {_maximumRequests}-request safety bound.");
            }

            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }
}
