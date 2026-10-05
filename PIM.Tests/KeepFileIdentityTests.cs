using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// When OMDb finds a movie's IMDb ID under a different title or year than the
/// file, the owner may keep the file's own wording instead of OMDb's. The IMDb
/// ID does not change.
/// </summary>
public sealed class KeepFileIdentityTests
{
    private const string ImdbId1917 = "tt8579674";

    // IMDb lists 1917 as 2019; OMDb reports the 2020 wide release.
    private const string Omdb1917 = """
        {
          "Title": "1917",
          "Year": "2020",
          "Rated": "R",
          "Genre": "Action, Drama, War",
          "imdbID": "tt8579674",
          "Response": "True"
        }
        """;

    private const string OmdbMules = """
        {
          "Title": "2000 Mules",
          "Year": "2022",
          "Rated": "PG-13",
          "Genre": "Documentary",
          "imdbID": "tt18924506",
          "Response": "True"
        }
        """;

    [Fact]
    public async Task YearDifference_IsTheOwnersDecision_AndTheFileIdentityCanBeKept()
    {
        var handler = new QueuedHandler(Omdb1917, Omdb1917);
        var plan = new CountingPlanService();
        var (metadata, service) = CreateServices(handler, plan);
        var movie = FileMovie("1917", 2019, ImdbId1917);
        var movies = new List<Movie> { movie };

        await metadata.EnrichAsync(movie);

        // Unchanged rule: PIM does not settle the difference on its own.
        Assert.True(movie.NeedsReview);
        Assert.False(movie.MetadataFetched);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
        Assert.Equal(2020, movie.SuggestedYear);
        Assert.True(movie.CanAcceptMetadataSuggestion);
        Assert.True(movie.CanKeepFileIdentity);
        Assert.Equal("1917", movie.FileIdentityTitle);
        Assert.Equal(2019, movie.FileIdentityYear);

        Assert.True(await service.KeepFileIdentityAsync(
            movie,
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));

        Assert.Equal("1917", movie.Title);
        Assert.Equal(2019, movie.Year);
        Assert.Equal(ImdbId1917, movie.ImdbId);
        Assert.True(movie.IsFileIdentityKept);
        Assert.True(movie.MetadataFetched);
        Assert.True(movie.MetadataMatchedByImdbId);
        Assert.False(movie.NeedsReview);
        Assert.False(movie.HasMetadataReviewReason);
        Assert.False(movie.CanKeepFileIdentity);

        // OMDb's rating and genre for the ID are still recorded, and its
        // wording stays available for reference.
        Assert.Equal("R", movie.MpaRating);
        Assert.Equal("Action", movie.PrimaryGenre);
        Assert.Equal("1917", movie.SuggestedTitle);
        Assert.Equal(2020, movie.SuggestedYear);
        Assert.Contains("kept by the owner's decision", movie.MetadataDiscoveryReason);
        Assert.Contains("1917 (2020)", movie.MetadataDiscoveryReason);

        Assert.Equal(1, plan.CallCount);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.All(handler.RequestUris, uri => Assert.Contains($"i={ImdbId1917}", uri.Query));
    }

    [Fact]
    public async Task TitleTheFileNameDidNotProvide_ComesFromOmdb_WhileTheFileYearIsKept()
    {
        // "1917 (2019)" is read as two years, leaving no title.
        var handler = new QueuedHandler(Omdb1917, Omdb1917);
        var (metadata, service) = CreateServices(handler, new CountingPlanService());
        var movie = FileMovie(null, 2019, ImdbId1917);

        await metadata.EnrichAsync(movie);
        Assert.True(movie.CanKeepFileIdentity);
        Assert.Equal("1917", movie.FileIdentityTitle);
        Assert.Equal(2019, movie.FileIdentityYear);

        Assert.True(await KeepAsync(service, movie));

        Assert.Equal("1917", movie.Title);
        Assert.Equal(2019, movie.Year);
        Assert.False(movie.NeedsReview);
    }

    [Fact]
    public async Task TitleDifference_KeepsTheFileTitle()
    {
        var handler = new QueuedHandler(OmdbMules, OmdbMules);
        var (metadata, service) = CreateServices(handler, new CountingPlanService());
        var movie = FileMovie("Mules", 2022, "tt18924506");

        await metadata.EnrichAsync(movie);
        Assert.Equal("2000 Mules", movie.SuggestedTitle);
        Assert.True(movie.CanKeepFileIdentity);

        Assert.True(await KeepAsync(service, movie));

        Assert.Equal("Mules", movie.Title);
        Assert.Equal(2022, movie.Year);
        Assert.Equal("tt18924506", movie.ImdbId);
        Assert.True(movie.MetadataFetched);
    }

    [Fact]
    public async Task KeptIdentity_IsNotQuestionedAgainByALaterLookup()
    {
        var handler = new QueuedHandler(Omdb1917, Omdb1917, Omdb1917);
        var (metadata, service) = CreateServices(handler, new CountingPlanService());
        var movie = FileMovie("1917", 2019, ImdbId1917);
        await metadata.EnrichAsync(movie);
        Assert.True(await KeepAsync(service, movie));

        await metadata.EnrichAsync(movie);

        Assert.True(movie.MetadataFetched);
        Assert.False(movie.NeedsReview);
        Assert.Equal("1917", movie.Title);
        Assert.Equal(2019, movie.Year);
        Assert.True(movie.IsFileIdentityKept);
    }

    [Fact]
    public async Task Undo_ReturnsTheDecisionToTheOwner()
    {
        var handler = new QueuedHandler(Omdb1917, Omdb1917, Omdb1917);
        var plan = new CountingPlanService();
        var (metadata, service) = CreateServices(handler, plan);
        var movie = FileMovie("1917", 2019, ImdbId1917);
        var movies = new List<Movie> { movie };
        await metadata.EnrichAsync(movie);
        Assert.True(await KeepAsync(service, movie));

        Assert.True(await service.UndoKeepFileIdentityAsync(
            movie,
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));

        Assert.False(movie.IsFileIdentityKept);
        Assert.Null(movie.FileIdentityKeptForImdbId);
        Assert.False(movie.MetadataFetched);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
        Assert.True(movie.CanKeepFileIdentity);
        Assert.True(movie.CanAcceptMetadataSuggestion);
        Assert.Equal(2, plan.CallCount);

        // Nothing left to undo.
        Assert.False(await service.UndoKeepFileIdentityAsync(
            movie,
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));
        Assert.Equal(2, plan.CallCount);
    }

    [Fact]
    public async Task AcceptingOmdbsNameInstead_UsesOmdbsYear_AndIsNotAKeptIdentity()
    {
        var handler = new QueuedHandler(Omdb1917, Omdb1917);
        var (metadata, service) = CreateServices(handler, new CountingPlanService());
        var movie = FileMovie("1917", 2019, ImdbId1917);
        await metadata.EnrichAsync(movie);

        Assert.True(await service.AcceptAsync(
            movie,
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));

        Assert.Equal(2020, movie.Year);
        Assert.True(movie.MetadataFetched);
        Assert.False(movie.IsFileIdentityKept);
        Assert.Null(movie.FileIdentityKeptForImdbId);
    }

    [Fact]
    public async Task MovieWithoutThatChoice_IsRefusedWithoutAnyLookup()
    {
        var handler = new QueuedHandler();
        var plan = new CountingPlanService();
        var (_, service) = CreateServices(handler, plan);

        // A title-based guess: there is no IMDb ID on the movie to keep.
        var guess = FileMovie("Gladiator", 2001, null);
        guess.SuggestedTitle = "Gladiator";
        guess.SuggestedYear = 2000;
        guess.SuggestedImdbId = "tt0172495";
        guess.SetMetadataReview("Possible OMDb match", MetadataLookupFailureType.LowConfidence);

        // Already identified: nothing is in question.
        var identified = FileMovie("1917", 2019, ImdbId1917);
        identified.MetadataFetched = true;

        // An error blocks every identity decision.
        var broken = FileMovie("1917", 2019, ImdbId1917);
        broken.SuggestedTitle = "1917";
        broken.SuggestedYear = 2020;
        broken.SuggestedImdbId = ImdbId1917;
        broken.SetMetadataReview(
            "The provided IMDb ID does not match the movie year provided.",
            MetadataLookupFailureType.ImdbIdentityConflict);
        broken.ErrorMessage = "The source file could not be read.";

        foreach (var movie in new[] { guess, identified, broken })
        {
            var title = movie.Title;
            var year = movie.Year;

            Assert.False(movie.CanKeepFileIdentity);
            Assert.False(await KeepAsync(service, movie));
            Assert.Equal(title, movie.Title);
            Assert.Equal(year, movie.Year);
            Assert.Null(movie.FileIdentityKeptForImdbId);
        }

        Assert.Empty(handler.RequestUris);
        Assert.Equal(0, plan.CallCount);
    }

    [Fact]
    public async Task KeptDecision_AppliesOnlyToTheImdbIdItWasMadeFor()
    {
        // The decision lapses by itself when the movie's ID is different.
        var changed = FileMovie("1917", 2019, "tt0000002");
        changed.FileIdentityKeptForImdbId = ImdbId1917;
        Assert.False(changed.IsFileIdentityKept);

        // OMDb answering for a different ID than the one asked about is not
        // covered by the decision either: the difference is raised as usual.
        var redirected = """
            {
              "Title": "1917",
              "Year": "2020",
              "Rated": "R",
              "Genre": "War",
              "imdbID": "tt9999999",
              "Response": "True"
            }
            """;
        var handler = new QueuedHandler(redirected);
        var (metadata, _) = CreateServices(handler, new CountingPlanService());
        var movie = FileMovie("1917", 2019, ImdbId1917);
        movie.FileIdentityKeptForImdbId = ImdbId1917;

        await metadata.EnrichAsync(movie);

        Assert.True(movie.NeedsReview);
        Assert.False(movie.MetadataFetched);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
        Assert.Equal(2019, movie.Year);
    }

    [Fact]
    public async Task EnteringAnImdbId_StartsAfresh_WhateverWasKeptBefore()
    {
        var betterOffDead = """
            {
              "Title": "Better Off Dead...",
              "Year": "1985",
              "Rated": "PG",
              "Genre": "Comedy, Romance",
              "imdbID": "tt0088794",
              "Response": "True"
            }
            """;
        var handler = new QueuedHandler(betterOffDead);
        var (_, service) = CreateServices(handler, new CountingPlanService());
        var movie = FileMovie("Some Other Movie", 2003, null);
        movie.FileIdentityKeptForImdbId = "tt0088794";
        movie.SetMetadataReview(
            "IMDb ID could not be determined",
            MetadataLookupFailureType.MovieNotFound);

        Assert.True(await service.ApplyImdbIdAsync(
            movie,
            "tt0088794",
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));

        // The entered ID is checked against the file, not waved through by an
        // old decision.
        Assert.Null(movie.FileIdentityKeptForImdbId);
        Assert.True(movie.NeedsReview);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
    }

    [Fact]
    public void KeepingTheFileIdentity_ChangesThePlanFingerprint()
    {
        var profile = CreateProfile();
        var movie = FileMovie("1917", 2019, ImdbId1917);
        movie.MetadataFetched = true;
        var movies = new List<Movie> { movie };
        var before = PlanFingerprintBuilder.Build(movies, profile, LibraryGoal.OrganizeNewMovies);

        movie.FileIdentityKeptForImdbId = ImdbId1917;

        Assert.NotEqual(
            before,
            PlanFingerprintBuilder.Build(movies, profile, LibraryGoal.OrganizeNewMovies));
    }

    private static Task<bool> KeepAsync(MetadataSuggestionService service, Movie movie)
    {
        return service.KeepFileIdentityAsync(
            movie,
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);
    }

    private static (OmdbMetadataService Metadata, MetadataSuggestionService Service) CreateServices(
        HttpMessageHandler handler,
        IMoviePlanService plan)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Omdb:ApiKey"] = "test-key"
            })
            .Build();
        var metadata = new OmdbMetadataService(new HttpClient(handler), configuration);

        return (metadata, new MetadataSuggestionService(metadata, plan));
    }

    private static Movie FileMovie(string? title, int year, string? imdbId)
    {
        var name = $"{title ?? "Untitled"} ({year})";

        return new Movie
        {
            Title = title,
            Year = year,
            ImdbId = imdbId,
            FileName = $"{name}.mkv",
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-Keep-File-Identity", $"{name}.mkv")
        };
    }

    private static DestinationProfile CreateProfile() => new()
    {
        DestinationRoot = Path.Combine(Path.GetTempPath(), "PIM-Keep-File-Identity-Destination")
    };

    private sealed class CountingPlanService : IMoviePlanService
    {
        public int CallCount { get; private set; }

        public void Rebuild(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            CallCount++;
        }
    }

    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueuedHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            if (_responses.Count == 0)
                throw new InvalidOperationException("No stubbed OMDb response remains for this test.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue())
            });
        }
    }
}
