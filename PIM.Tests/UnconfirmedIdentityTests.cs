using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// A low-confidence title match is only a suggestion. It must never become the
/// movie's own identity unless a human accepts it; otherwise the next
/// enrichment looks the guess up by its own IMDb ID and "confirms" it.
/// Reproduces owner test case PIM_27 Case_06: "Gladiator (2001).mkv" matched
/// by title/year to an unrelated 2001 film.
/// </summary>
public sealed class UnconfirmedIdentityTests
{
    private const string WrongMovie = """
        {
          "Title": "Gladiator Eroticvs: The Lesbian Warriors",
          "Year": "2001",
          "Rated": "Unrated",
          "Genre": "Adult",
          "imdbID": "tt0256056",
          "Response": "True"
        }
        """;

    [Fact]
    public async Task LowConfidenceTitleMatch_KeepsTheFilesOwnIdentity()
    {
        var handler = new RoutingHandler();
        var movie = CreateGladiator();

        await CreateService(handler).EnrichAsync(movie);

        Assert.True(movie.NeedsReview);
        Assert.StartsWith("Low confidence metadata match", movie.ReviewReason);
        Assert.Equal("Gladiator", movie.Title);
        Assert.Equal(2001, movie.Year);
        Assert.Null(movie.ImdbId);
        Assert.False(movie.MetadataFetched);
        Assert.Null(movie.MpaRating);
        Assert.Empty(movie.Genres);
        Assert.Equal("Gladiator Eroticvs: The Lesbian Warriors", movie.SuggestedTitle);
        Assert.Equal("tt0256056", movie.SuggestedImdbId);
        Assert.True(movie.CanAcceptMetadataSuggestion);
    }

    [Fact]
    public async Task ReidentifyingALowConfidenceMatch_NeverConfirmsTheGuessByItself()
    {
        var handler = new RoutingHandler();
        var service = CreateService(handler);
        var movie = CreateGladiator();
        var movies = new List<Movie> { movie };
        var planner = CreatePlanner();

        // Identify, then the re-identification a dry run performs for every
        // item with a metadata review reason.
        await service.EnrichAsync(movie);
        planner.Rebuild(movies, CreateProfile(), string.Empty, LibraryGoal.OrganizeNewMovies);
        await service.EnrichAsync(movie);
        planner.Rebuild(movies, CreateProfile(), string.Empty, LibraryGoal.OrganizeNewMovies);

        Assert.DoesNotContain(handler.RequestUris, uri => uri.Query.Contains("i=tt0256056"));
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.False(movie.MetadataMatchedByImdbId);
        Assert.Null(movie.ImdbId);
        Assert.Null(movie.TargetPath);
    }

    [Fact]
    public async Task AcceptingTheSuggestion_IsStillTheWayToAdoptIt()
    {
        var handler = new RoutingHandler();
        var service = CreateService(handler);
        var movie = CreateGladiator();
        var movies = new List<Movie> { movie };
        await service.EnrichAsync(movie);

        var accepted = await new MetadataSuggestionService(service, CreatePlanner()).AcceptAsync(
            movie,
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);

        Assert.True(accepted);
        Assert.Contains(handler.RequestUris, uri => uri.Query.Contains("i=tt0256056"));
        Assert.Equal("tt0256056", movie.ImdbId);
        Assert.True(movie.MetadataMatchedByImdbId);
        Assert.False(movie.HasMetadataReviewReason);
    }

    [Fact]
    public async Task TitleResultMissingRequiredFields_KeepsTheFilesOwnIdentity()
    {
        var handler = new RoutingHandler("""
            {
              "Title": "Gladiator Eroticvs: The Lesbian Warriors",
              "Year": "N/A",
              "imdbID": "tt0256056",
              "Response": "True"
            }
            """);
        var movie = CreateGladiator();

        await CreateService(handler).EnrichAsync(movie);

        Assert.True(movie.NeedsReview);
        Assert.Equal("Gladiator", movie.Title);
        Assert.Equal(2001, movie.Year);
        Assert.Null(movie.ImdbId);
    }

    private static Movie CreateGladiator()
    {
        return new Movie
        {
            Title = "Gladiator",
            Year = 2001,
            FileName = "Gladiator (2001).mkv",
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-Unconfirmed-Identity", "Gladiator (2001).mkv"),
            FileSizeBytes = 1000
        };
    }

    private static OmdbMetadataService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Omdb:ApiKey"] = "test-key"
            })
            .Build();

        return new OmdbMetadataService(new HttpClient(handler), configuration);
    }

    private static MoviePlanService CreatePlanner()
    {
        var configuration = new ConfigurationBuilder().Build();
        var destination = new NoDestinationConflicts();
        var rename = new RenameService(
            destination,
            new DestinationPathBuilder(),
            configuration,
            new ScanProgress(),
            new SourceCleanupStatus());

        return new MoviePlanService(
            new DuplicateService(),
            rename,
            new MovieConflictDetectionService(destination, new NoPlexConflicts()));
    }

    private static DestinationProfile CreateProfile() => new()
    {
        DestinationRoot = Path.Combine(Path.GetTempPath(), "PIM-Unconfirmed-Identity-Destination")
    };

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public RoutingHandler(string response = WrongMovie)
        {
            _response = response;
        }

        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            // OMDb's title lookup and its IMDb lookup both return the same
            // unrelated movie here, as happened with the real Case_06 data.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response)
            });
        }
    }

    private sealed class NoPlexConflicts : IPlexLibraryConflictService
    {
        public PlexLibraryConflictResult Check(Movie movie) =>
            PlexLibraryConflictResult.NoConflict();
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
