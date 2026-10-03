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

    [Fact]
    public async Task WrongFilenameYear_SuggestsTheExactTitleOneYearOffAt90Percent()
    {
        // Real OMDb behaviour for "Gladiator (2001).mkv": the title+year lookup
        // returns an unrelated 2001 film; the same title without a year
        // returns Gladiator (2000).
        var handler = new RoutingHandler(query =>
            query.Contains("i=tt0172495") ? RealGladiator
            : query.Contains("t=Gladiator&y=2001") ? WrongMovie
            : query.Contains("t=Gladiator&type=") ? RealGladiator
            : NotFound);
        var service = CreateService(handler);
        var movie = CreateGladiator();
        var movies = new List<Movie> { movie };

        await service.EnrichAsync(movie);
        CreatePlanner().Rebuild(movies, CreateProfile(), string.Empty, LibraryGoal.OrganizeNewMovies);

        Assert.Equal("Gladiator", movie.SuggestedTitle);
        Assert.Equal(2000, movie.SuggestedYear);
        Assert.Equal("tt0172495", movie.SuggestedImdbId);
        Assert.Equal(90, movie.MatchConfidence);
        Assert.True(movie.CanAcceptMetadataSuggestion);

        // Still only a suggestion: the year conflict needs a human decision.
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal(2001, movie.Year);
        Assert.Null(movie.ImdbId);
        Assert.Equal(MetadataLookupFailureType.FuzzyCandidateYearConflict, movie.MetadataLookupFailureType);

        Assert.True(await new MetadataSuggestionService(service, CreatePlanner()).AcceptAsync(
            movie,
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));

        Assert.Equal("tt0172495", movie.ImdbId);
        Assert.Equal(2000, movie.Year);
        Assert.False(movie.HasMetadataReviewReason);
    }

    [Fact]
    public async Task ExactTitleTwoYearsOff_IsNotRaisedTo90Percent()
    {
        var handler = new RoutingHandler(query =>
            query.Contains("t=Gladiator&y=2002") ? WrongMovie.Replace("2001", "2002")
            : query.Contains("t=Gladiator&type=") ? RealGladiator
            : NotFound);
        var movie = CreateGladiator();
        movie.Year = 2002;

        await CreateService(handler).EnrichAsync(movie);

        Assert.Equal("tt0172495", movie.SuggestedImdbId);
        Assert.Equal(70, movie.MatchConfidence);
        Assert.True(movie.NeedsReview);
    }

    [Fact]
    public async Task WeakTitleMatch_IsKeptWhenRecoveryFindsNothingBetter()
    {
        var handler = new RoutingHandler();
        var movie = CreateGladiator();

        await CreateService(handler).EnrichAsync(movie);

        Assert.Contains(handler.RequestUris, uri => uri.Query.Contains("t=Gladiator&type="));
        Assert.Equal("tt0256056", movie.SuggestedImdbId);
        Assert.Equal(21, movie.MatchConfidence);
        Assert.StartsWith("Low confidence metadata match", movie.ReviewReason);
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

    private const string NotFound = """{ "Response": "False", "Error": "Movie not found!" }""";

    private const string RealGladiator = """
        {
          "Title": "Gladiator",
          "Year": "2000",
          "Rated": "R",
          "Genre": "Action, Adventure, Drama",
          "imdbID": "tt0172495",
          "Response": "True"
        }
        """;

    /// <summary>
    /// Fake OMDb. By default only the title+year lookup (and an IMDb lookup of
    /// the wrong film) return the unrelated 2001 film, as with the real Case_06
    /// data; every other request is "not found", so recovery finds nothing.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _respond;

        public RoutingHandler(string yearLookupResponse = WrongMovie)
            : this(query =>
                query.Contains("t=Gladiator&y=2001") || query.Contains("i=tt0256056")
                    ? yearLookupResponse
                    : NotFound)
        {
        }

        public RoutingHandler(Func<string, string> respond)
        {
            _respond = respond;
        }

        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(Uri.UnescapeDataString(request.RequestUri!.Query)))
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
