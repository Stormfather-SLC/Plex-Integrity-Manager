using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Owner-approved rules (2026-09-28): the same movie in Plex as a different
/// file is a possible duplicate the user decides on (skip by default, or add
/// anyway); true collisions stay hard stops; only the exact file Plex tracks is
/// a plain reorganization.
/// </summary>
public sealed class PlexDuplicateDecisionTests
{
    private const string PlexCopy =
        @"G:\[PLEX]\Movies\Policy Movie (2024) {imdb-tt1234567}\Policy Movie (2024) {imdb-tt1234567}.mkv";

    [Theory]
    [InlineData(LibraryGoal.OrganizeNewMovies)]
    [InlineData(LibraryGoal.ReorganizationMigration)]
    public void AddingAnyway_LetsThisFileThroughForThatPlexCopy(LibraryGoal goal)
    {
        var movie = CreateMovie();
        movie.PlexDuplicateAcceptedPath = PlexCopy;

        Evaluate(movie, SameMovieElsewhere(PlexCopy), goal);

        Assert.True(movie.IsPossiblePlexDuplicate);
        Assert.True(movie.IsPlexDuplicateAccepted);
        Assert.False(movie.NeedsPlexDuplicateDecision);
        Assert.False(movie.HasPlexLibraryConflict);
        Assert.False(movie.NeedsReview);
        Assert.True(movie.ApprovedForCommit);
    }

    [Fact]
    public void AnAcceptanceForADifferentPlexCopy_DoesNotCarryOver()
    {
        var movie = CreateMovie();
        movie.PlexDuplicateAcceptedPath = @"G:\[PLEX]\Old Location\Policy Movie.mkv";

        Evaluate(movie, SameMovieElsewhere(PlexCopy), LibraryGoal.OrganizeNewMovies);

        Assert.True(movie.NeedsPlexDuplicateDecision);
        Assert.False(movie.IsPlexDuplicateAccepted);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
    }

    [Theory]
    [InlineData(PlexLibraryConflictType.SameImdbIdDifferentPath)]
    [InlineData(PlexLibraryConflictType.SameTitleYearDifferentPath)]
    [InlineData(PlexLibraryConflictType.ExistingAlternateVersion)]
    public void EveryKindOfPossibleDuplicate_WaitsForADecisionByDefault(PlexLibraryConflictType type)
    {
        var movie = CreateMovie();

        Evaluate(
            movie,
            PlexLibraryConflictResult.Conflict(type, "Plex already has this movie.", PlexCopy),
            LibraryGoal.OrganizeNewMovies);

        Assert.True(movie.NeedsPlexDuplicateDecision);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
    }

    [Theory]
    [InlineData(PlexLibraryConflictType.AlreadyExistsAtTargetPath)]
    [InlineData(PlexLibraryConflictType.LibraryMismatch)]
    public void HardStops_CannotBeOverriddenEvenWithARecordedAcceptance(PlexLibraryConflictType type)
    {
        var movie = CreateMovie();
        movie.PlexDuplicateAcceptedPath = PlexCopy;

        Evaluate(
            movie,
            PlexLibraryConflictResult.Conflict(type, "Hard Plex conflict.", PlexCopy),
            LibraryGoal.ReorganizationMigration);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPossiblePlexDuplicate);
        Assert.False(movie.IsPlexDuplicateAccepted);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public void Consolidation_KeepsPossibleDuplicatesBlocked()
    {
        var movie = CreateMovie();
        movie.PlexDuplicateAcceptedPath = PlexCopy;

        Evaluate(movie, SameMovieElsewhere(PlexCopy), LibraryGoal.Consolidation);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPossiblePlexDuplicate);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public void TheDecision_ChangesThePlanFingerprint()
    {
        var movie = CreateMovie();
        Evaluate(movie, SameMovieElsewhere(PlexCopy), LibraryGoal.OrganizeNewMovies);
        var profile = new DestinationProfile { DestinationRoot = @"X:\PIM\Destination" };
        var before = PlanFingerprintBuilder.Build(new[] { movie }, profile, LibraryGoal.OrganizeNewMovies);

        movie.PlexDuplicateAcceptedPath = PlexCopy;

        Assert.NotEqual(
            before,
            PlanFingerprintBuilder.Build(new[] { movie }, profile, LibraryGoal.OrganizeNewMovies));
    }

    [Fact]
    public void PlexService_RecognizesTheExactFilePlexTracks()
    {
        var movie = CreateMovie();
        var service = CreatePlexService("Policy Movie", 2024, movie.OriginalFilePath);

        var result = service.Check(movie);

        Assert.Equal(PlexLibraryConflictType.TracksThisFile, result.ConflictType);
        Assert.False(result.IsPossibleDuplicate);
    }

    [Fact]
    public void PlexService_TreatsThisFileTrackedAsAnotherMovieAsAHardMismatch()
    {
        var movie = CreateMovie();
        var service = CreatePlexService("Another Movie", 1999, movie.OriginalFilePath);

        var result = service.Check(movie);

        Assert.Equal(PlexLibraryConflictType.LibraryMismatch, result.ConflictType);
        Assert.False(result.IsPossibleDuplicate);
    }

    [Fact]
    public void PlexService_SameMovieAsAnotherFile_IsAPossibleDuplicate()
    {
        var movie = CreateMovie();
        var service = CreatePlexService("Policy Movie", 2024, PlexCopy);

        var result = service.Check(movie);

        Assert.Equal(PlexLibraryConflictType.SameImdbIdDifferentPath, result.ConflictType);
        Assert.True(result.IsPossibleDuplicate);
    }

    private static void Evaluate(Movie movie, PlexLibraryConflictResult plexResult, LibraryGoal goal)
    {
        var detector = new MovieConflictDetectionService(
            new NoDestinationConflicts(),
            new FixedPlexResult(plexResult));
        var movies = new List<Movie> { movie };

        detector.ClearConflictState(movies);
        new DuplicateService().Process(movies);
        detector.ApplyConflictDetection(movies, @"X:\PIM\Destination", @"D:\PIM\Source", goal);
    }

    private static PlexLibraryConflictResult SameMovieElsewhere(string path) =>
        PlexLibraryConflictResult.Conflict(
            PlexLibraryConflictType.SameImdbIdDifferentPath,
            "Plex already contains IMDb tt1234567 with the same edition at a different path.",
            path,
            "1080p",
            8_000_000_000);

    private static Movie CreateMovie()
    {
        return new Movie
        {
            Title = "Policy Movie",
            Year = 2024,
            ImdbId = "tt1234567",
            FileName = "Policy.Movie.2024.2160p.mkv",
            OriginalFilePath = @"D:\PIM\Source\Policy.Movie.2024.2160p.mkv",
            TargetPath = @"X:\PIM\Destination\Policy Movie (2024) {imdb-tt1234567}\Policy Movie (2024) {imdb-tt1234567}.mkv",
            FileSizeBytes = 40_000_000_000,
            SourceResolution = "4K",
            MetadataFetched = true,
            MetadataMatchedByImdbId = true,
            MatchConfidence = 100,
            ApprovedForCommit = true
        };
    }

    private static PlexLibraryConflictService CreatePlexService(string title, int year, string file)
    {
        var escapedFile = System.Text.Json.JsonSerializer.Serialize(file);
        var library = $$"""
            {
              "MediaContainer": {
                "totalSize": 1,
                "Metadata": [
                  {
                    "title": "{{title}}",
                    "year": {{year}},
                    "Guid": [ { "id": "imdb://{{(title == "Policy Movie" ? "tt1234567" : "tt7654321")}}" } ],
                    "Media": [ { "videoResolution": "1080", "Part": [ { "file": {{escapedFile}}, "size": 8000000000 } ] } ]
                  }
                ]
              }
            }
            """;
        const string sections = """
            { "MediaContainer": { "Directory": [ { "key": "1", "type": "movie", "title": "Movies" } ] } }
            """;
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/all", StringComparison.Ordinal)
                ? library
                : sections);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plex:Enabled"] = "true",
                ["Plex:Token"] = "stub-token",
                ["Plex:BaseUrl"] = "http://localhost:32400"
            })
            .Build();

        return new PlexLibraryConflictService(
            new HttpClient(handler),
            configuration,
            NullLogger<PlexLibraryConflictService>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _body;

        public StubHandler(Func<HttpRequestMessage, string> body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body(request), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixedPlexResult : IPlexLibraryConflictService
    {
        private readonly PlexLibraryConflictResult _result;

        public FixedPlexResult(PlexLibraryConflictResult result)
        {
            _result = result;
        }

        public PlexLibraryConflictResult Check(Movie movie) => _result;
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
