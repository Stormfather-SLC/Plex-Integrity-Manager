using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class ManualImdbIdTests
{
    private const string BetterOffDead = """
        {
          "Title": "Better Off Dead...",
          "Year": "1985",
          "Rated": "PG",
          "Genre": "Comedy, Romance",
          "imdbID": "tt0088794",
          "Response": "True"
        }
        """;

    [Theory]
    [InlineData("tt0088794", "tt0088794")]
    [InlineData("  TT0088794  ", "tt0088794")]
    [InlineData("https://www.imdb.com/title/tt0088794/?ref_=nv_sr_srsg_0", "tt0088794")]
    [InlineData("tt123456789", "tt123456789")]
    public void ImdbIdInput_AcceptsBareIdsAndImdbLinks(string input, string expected)
    {
        Assert.True(ImdbIdInput.TryNormalize(input, out var imdbId));
        Assert.Equal(expected, imdbId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0088794")]
    [InlineData("tt123")]
    [InlineData("tt1234567890")]
    [InlineData("xtt0088794")]
    [InlineData("Better Off Dead")]
    [InlineData("tt0088794 tt0093779")]
    public void ImdbIdInput_RejectsAnythingThatIsNotExactlyOneId(string? input)
    {
        Assert.False(ImdbIdInput.TryNormalize(input, out _));
    }

    [Fact]
    public async Task MatchingId_IdentifiesTheMovieAndRebuildsThePlan()
    {
        var handler = new RecordingHandler(BetterOffDead);
        var plan = new CountingPlanService();
        var movie = CreateUnidentifiedMovie("Better Off Dead", 1985);

        var applied = await CreateService(handler, plan).ApplyImdbIdAsync(
            movie,
            "https://www.imdb.com/title/tt0088794/",
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);

        Assert.True(applied);
        Assert.Contains("i=tt0088794", Assert.Single(handler.RequestUris).Query);
        Assert.Equal("tt0088794", movie.ImdbId);
        Assert.True(movie.MetadataFetched);
        Assert.False(movie.HasMetadataReviewReason);
        Assert.Equal(1, plan.CallCount);
    }

    [Fact]
    public async Task IdThatDisagreesWithTheFile_StaysInReviewUntilTheUserConfirmsIt()
    {
        var handler = new RecordingHandler(BetterOffDead, BetterOffDead);
        var plan = new CountingPlanService();
        var service = CreateService(handler, plan);
        var movie = CreateUnidentifiedMovie("Some Other Movie", 2003);
        var movies = new List<Movie> { movie };

        await service.ApplyImdbIdAsync(
            movie,
            "tt0088794",
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);

        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, movie.MetadataLookupFailureType);
        Assert.Equal("Some Other Movie", movie.Title);
        Assert.Equal("Better Off Dead...", movie.SuggestedTitle);
        Assert.Equal(1985, movie.SuggestedYear);
        Assert.Equal("tt0088794", movie.SuggestedImdbId);
        Assert.True(movie.CanAcceptMetadataSuggestion);

        // The user confirms the identity the ID actually points to.
        Assert.True(await service.AcceptAsync(
            movie,
            movies,
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies));

        Assert.False(movie.HasMetadataReviewReason);
        Assert.Equal("Better Off Dead...", movie.Title);
        Assert.Equal(1985, movie.Year);
        Assert.True(movie.MetadataFetched);
    }

    [Fact]
    public async Task AlreadyIdentifiedMovie_IsRefusedWithoutAnyLookup()
    {
        var handler = new RecordingHandler();
        var plan = new CountingPlanService();
        var movie = new Movie
        {
            Title = "Better Off Dead...",
            Year = 1985,
            ImdbId = "tt0088794",
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "Better.Off.Dead.1985.mkv"),
            MetadataFetched = true
        };

        var applied = await CreateService(handler, plan).ApplyImdbIdAsync(
            movie,
            "tt0093779",
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);

        Assert.False(applied);
        Assert.Equal("tt0088794", movie.ImdbId);
        Assert.Empty(handler.RequestUris);
        Assert.Equal(0, plan.CallCount);
    }

    [Theory]
    [InlineData("not an id")]
    [InlineData("tt0088794 tt0093779")]
    public async Task InvalidInput_IsRefusedWithoutAnyLookup(string input)
    {
        var handler = new RecordingHandler();
        var plan = new CountingPlanService();
        var movie = CreateUnidentifiedMovie("Better Off Dead", 1985);

        var applied = await CreateService(handler, plan).ApplyImdbIdAsync(
            movie,
            input,
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);

        Assert.False(applied);
        Assert.Null(movie.ImdbId);
        Assert.True(movie.NeedsReview);
        Assert.Empty(handler.RequestUris);
        Assert.Equal(0, plan.CallCount);
    }

    [Fact]
    public async Task MovieWithAnError_IsRefused()
    {
        var handler = new RecordingHandler();
        var movie = CreateUnidentifiedMovie("Better Off Dead", 1985);
        movie.ErrorMessage = "The source file could not be read.";

        var applied = await CreateService(handler, new CountingPlanService()).ApplyImdbIdAsync(
            movie,
            "tt0088794",
            new List<Movie> { movie },
            CreateProfile(),
            string.Empty,
            LibraryGoal.OrganizeNewMovies);

        Assert.False(applied);
        Assert.Empty(handler.RequestUris);
    }

    private static MetadataSuggestionService CreateService(
        HttpMessageHandler handler,
        IMoviePlanService plan)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Omdb:ApiKey"] = "test-key"
            })
            .Build();

        return new MetadataSuggestionService(
            new OmdbMetadataService(new HttpClient(handler), configuration),
            plan);
    }

    private static Movie CreateUnidentifiedMovie(string title, int year)
    {
        var movie = new Movie
        {
            Title = title,
            Year = year,
            FileName = $"{title}.{year}.mkv",
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-Manual-Imdb", $"{title}.{year}.mkv")
        };
        movie.SetMetadataReview(
            "IMDb ID could not be determined",
            MetadataLookupFailureType.MovieNotFound,
            "OMDb did not find the movie.");

        return movie;
    }

    private static DestinationProfile CreateProfile() => new()
    {
        DestinationRoot = Path.Combine(Path.GetTempPath(), "PIM-Manual-Imdb-Destination")
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public RecordingHandler(params string[] responses)
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
