using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using Xunit;

namespace PIM.Tests;

public sealed class OmdbMetadataServiceTests
{
    [Fact]
    public async Task EnrichAsync_SuccessfulImdbLookup_PreservesExistingReview()
    {
        const string json = """
        {
          "Title": "A Quiet Place",
          "Year": "2018",
          "Rated": "PG-13",
          "Genre": "Drama, Horror, Sci-Fi",
          "imdbID": "tt6644200",
          "Response": "True"
        }
        """;

        var service = CreateService(HttpStatusCode.OK, json);
        var movie = new Movie
        {
            Title = "Completely Wrong Parsed Title",
            Year = 1999,
            ImdbId = "tt6644200",
            NeedsReview = true,
            ReviewReason = "Missing required metadata for rename"
        };

        await service.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.True(movie.MetadataMatchedByImdbId);
        Assert.True(movie.NeedsReview);
        Assert.Equal("Missing required metadata for rename", movie.ReviewReason);
        Assert.Equal("Needs Review", movie.Status);
        Assert.Equal("A Quiet Place", movie.Title);
        Assert.Equal(2018, movie.Year);
        Assert.Equal("PG-13", movie.MpaRating);
        Assert.Equal("Drama", movie.PrimaryGenre);
    }

    [Fact]
    public async Task EnrichAsync_FailedImdbLookup_UsesSpecificReviewReason()
    {
        const string json = """
        {
          "Response": "False",
          "Error": "Incorrect IMDb ID."
        }
        """;

        var service = CreateService(HttpStatusCode.OK, json);
        var movie = new Movie
        {
            Title = "Unknown",
            ImdbId = "tt0000000"
        };

        await service.EnrichAsync(movie);

        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.Equal("IMDb ID found, but OMDb lookup failed", movie.ReviewReason);
        Assert.Equal("IMDb ID Lookup Failed", movie.Status);
    }

    private static OmdbMetadataService CreateService(
        HttpStatusCode statusCode,
        string responseBody)
    {
        var handler = new StubHttpMessageHandler(statusCode, responseBody);
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Omdb:ApiKey"] = "test-key"
            })
            .Build();

        return new OmdbMetadataService(httpClient, configuration);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public StubHttpMessageHandler(
            HttpStatusCode statusCode,
            string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }
}
