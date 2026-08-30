using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
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
        var service = CreateService(
            new StubHttpMessageHandler(new[]
            {
                Response(HttpStatusCode.OK, """
                {
                  "Response": "False",
                  "Error": "Movie not found for key secret-test-key!"
                }
                """)
            }),
            apiKey);
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
        var movie = new Movie { Title = "Inception", Year = 2010 };

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
        var movie = new Movie { Title = "Inception", Year = 2010 };

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
        var movie = new Movie { Title = "Inception", Year = 2010 };

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
    public async Task EnrichAsync_SuccessfulImdbLookup_PreservesUnrelatedReview()
    {
        var service = CreateService(Response(HttpStatusCode.OK, SuccessfulResponse));
        var movie = new Movie
        {
            Title = "Completely Wrong Parsed Title",
            Year = 1999,
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

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No stubbed OMDb response remains for this test.");
            }

            return Task.FromResult(_responses.Dequeue());
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
