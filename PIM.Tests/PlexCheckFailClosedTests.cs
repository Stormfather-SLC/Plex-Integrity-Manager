using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// The Plex library check must never be skipped silently: only an explicit
/// Plex:Enabled=false turns it off, and the page then says so.
/// </summary>
public sealed class PlexCheckFailClosedTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("yes", true)]
    [InlineData("flase", true)]
    [InlineData("false", false)]
    [InlineData(" False ", false)]
    public void IsLibraryCheckEnabled_IsOnUnlessExplicitlyFalse(string? value, bool expected)
    {
        Assert.Equal(expected, PlexSettings.IsLibraryCheckEnabled(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("yes")]
    public void EnabledSettingMissingOrUnreadable_WithoutToken_FailsClosedWithoutAnyRequest(string? enabled)
    {
        var handler = new CountingHandler();
        var service = CreateService(handler, enabled, token: null);

        var result = service.Check(CreateMovie());

        Assert.True(result.HasConflict);
        Assert.Equal(PlexLibraryConflictType.LibraryMismatch, result.ConflictType);
        Assert.Contains("Plex:Token is not configured", result.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void ExplicitlyDisabled_ReportsNoConflictWithoutAnyRequest()
    {
        var handler = new CountingHandler();
        var service = CreateService(handler, "false", token: "test-token");

        var result = service.Check(CreateMovie());

        Assert.False(result.HasConflict);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void EnabledSettingMissing_WithoutToken_BlocksTheMovieInThePipeline()
    {
        var plex = CreateService(new CountingHandler(), enabled: null, token: null);
        var detection = new MovieConflictDetectionService(new NoDestinationConflicts(), plex);
        var movie = CreateMovie();
        movie.ApprovedForCommit = true;

        detection.ApplyConflictDetection(
            new List<Movie> { movie },
            Path.Combine(Path.GetTempPath(), "PIM-Plex-Fail-Closed"));

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public void SetupWarnings_TokenRequiredUnlessPlexCheckExplicitlyDisabled()
    {
        Assert.Contains(
            IndexModel.GetMissingSetupItems(CreateConfiguration(enabled: null, token: null)),
            item => item.Contains("Plex:Token"));
        Assert.Contains(
            IndexModel.GetMissingSetupItems(CreateConfiguration(enabled: "true", token: null)),
            item => item.Contains("Plex:Token"));
        Assert.DoesNotContain(
            IndexModel.GetMissingSetupItems(CreateConfiguration(enabled: "false", token: null)),
            item => item.Contains("Plex:Token"));
        Assert.DoesNotContain(
            IndexModel.GetMissingSetupItems(CreateConfiguration(enabled: null, token: "test-token")),
            item => item.Contains("Plex:Token"));
    }

    private static PlexLibraryConflictService CreateService(
        HttpMessageHandler handler,
        string? enabled,
        string? token)
    {
        return new PlexLibraryConflictService(
            new HttpClient(handler),
            CreateConfiguration(enabled, token),
            NullLogger<PlexLibraryConflictService>.Instance);
    }

    private static IConfiguration CreateConfiguration(string? enabled, string? token)
    {
        var values = new Dictionary<string, string?>
        {
            ["Plex:BaseUrl"] = "http://127.0.0.1:9",
            ["Omdb:ApiKey"] = "test-key"
        };

        if (enabled != null)
            values[PlexSettings.EnabledKey] = enabled;

        if (token != null)
            values["Plex:Token"] = token;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static Movie CreateMovie()
    {
        return new Movie
        {
            Title = "Plex Check Movie",
            Year = 2022,
            ImdbId = "tt2222222",
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-Plex-Fail-Closed", "Source", "movie.mkv"),
            TargetPath = Path.Combine(
                Path.GetTempPath(),
                "PIM-Plex-Fail-Closed",
                "Plex Check Movie (2022) {imdb-tt2222222}",
                "Plex Check Movie (2022) {imdb-tt2222222}.mkv"),
            MetadataFetched = true
        };
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("No Plex request is expected in this test.");
        }
    }

    private sealed class NoDestinationConflicts : IDestinationConflictService
    {
        public void InvalidateCache()
        {
        }

        public void RecordDestinationEntry(string path)
        {
        }

        public DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false)
        {
            return DestinationConflictResult.NoConflict();
        }
    }
}
