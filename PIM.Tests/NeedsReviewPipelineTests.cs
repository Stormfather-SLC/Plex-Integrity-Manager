using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class NeedsReviewPipelineTests
{
    [Fact]
    public void DuplicateProcessing_PreservesExistingReviewState()
    {
        var movie = CreateMovie("A Quiet Place", 2018, "tt6644200");
        movie.RequireReview("Metadata requires human verification");

        new DuplicateService().Process(new List<Movie> { movie });

        Assert.True(movie.NeedsReview);
        Assert.Equal("Metadata requires human verification", movie.ReviewReason);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public async Task ImdbLookupFailure_StaysBlockedThroughPreviewPipeline()
    {
        var movie = CreateMovie("Unknown", 2018, "tt0000000");
        var metadata = CreateMetadataService("""
        {
          "Response": "False",
          "Error": "Incorrect IMDb ID."
        }
        """);

        await metadata.EnrichAsync(movie);
        RunDecisionAndPreviewPipeline(movie);

        Assert.True(movie.NeedsReview);
        Assert.Contains("IMDb ID found, but OMDb lookup failed", movie.ReviewReason);
        Assert.False(movie.ApprovedForCommit);
        Assert.NotNull(movie.TargetPath);
    }

    [Fact]
    public async Task LowConfidenceTitleMatch_StaysBlockedThroughPreviewPipeline()
    {
        var movie = CreateMovie("Completely Different", 1999, imdbId: null);
        var metadata = CreateMetadataService(SuccessfulResponse);

        await metadata.EnrichAsync(movie);
        RunDecisionAndPreviewPipeline(movie);

        Assert.True(movie.MatchConfidence < 85);
        Assert.True(movie.NeedsReview);
        Assert.Contains("Low confidence metadata match", movie.ReviewReason);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public async Task HighConfidenceTitleMatch_RemainsEligibleForCommit()
    {
        var movie = CreateMovie("A Quiet Place", 2018, imdbId: null);
        var metadata = CreateMetadataService(SuccessfulResponse);

        await metadata.EnrichAsync(movie);
        RunDecisionAndPreviewPipeline(movie);

        Assert.True(movie.MatchConfidence >= 85);
        Assert.False(movie.NeedsReview);
        Assert.Null(movie.ReviewReason);
        Assert.True(movie.ApprovedForCommit);
        Assert.NotNull(movie.TargetPath);
    }

    [Fact]
    public void DuplicateGroup_PreservesReviewedCopyAndSelectsValidWinner()
    {
        var reviewedCopy = CreateMovie("A Quiet Place", 2018, "tt6644200");
        reviewedCopy.FileSizeBytes = 100 * 1024 * 1024;
        reviewedCopy.RequireReview("Source identity requires human verification");

        var clearWinner = CreateMovie("A Quiet Place", 2018, "tt6644200");
        clearWinner.FileName = "A Quiet Place larger.mkv";
        clearWinner.FileSizeBytes = 200 * 1024 * 1024;

        new DuplicateService().Process(new List<Movie> { reviewedCopy, clearWinner });

        Assert.True(reviewedCopy.NeedsReview);
        Assert.Contains("Source identity requires human verification", reviewedCopy.ReviewReason);
        Assert.False(reviewedCopy.ApprovedForCommit);
        Assert.True(clearWinner.KeepRecommended);
        Assert.True(clearWinner.ApprovedForCommit);
        Assert.False(clearWinner.NeedsReview);
    }

    private static void RunDecisionAndPreviewPipeline(Movie movie)
    {
        new DuplicateService().Process(new List<Movie> { movie });

        var progress = new ScanProgress();
        var rename = new RenameService(
            new DestinationConflictService(progress),
            new DestinationPathBuilder(),
            new ConfigurationBuilder().Build(),
            progress,
            new SourceCleanupStatus());
        var profile = new DestinationProfile
        {
            Name = "NeedsReview Pipeline Test",
            DestinationRoot = Path.Combine(
                Path.GetTempPath(),
                "PIM-NeedsReview-Destination",
                Guid.NewGuid().ToString("N"))
        };

        rename.GeneratePreview(
            new List<Movie> { movie },
            profile,
            Path.Combine(Path.GetTempPath(), "PIM-NeedsReview-Source"));
    }

    private static Movie CreateMovie(
        string title,
        int year,
        string? imdbId)
    {
        return new Movie
        {
            Title = title,
            Year = year,
            ImdbId = imdbId,
            FileName = title + ".mkv",
            OriginalFilePath = Path.Combine(
                Path.GetTempPath(),
                "PIM-NeedsReview-Source",
                Guid.NewGuid().ToString("N") + ".mkv")
        };
    }

    private static OmdbMetadataService CreateMetadataService(string responseBody)
    {
        var httpClient = new HttpClient(
            new StubHttpMessageHandler(HttpStatusCode.OK, responseBody));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Omdb:ApiKey"] = "test-key"
            })
            .Build();

        return new OmdbMetadataService(httpClient, configuration);
    }

    private const string SuccessfulResponse = """
    {
      "Title": "A Quiet Place",
      "Year": "2018",
      "Rated": "PG-13",
      "Genre": "Drama, Horror, Sci-Fi",
      "imdbID": "tt6644200",
      "Response": "True"
    }
    """;

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
