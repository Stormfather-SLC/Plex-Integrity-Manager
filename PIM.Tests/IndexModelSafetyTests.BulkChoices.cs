using System.Net;
using Microsoft.Extensions.Configuration;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// One choice (Accept match or Keep file's name) applied to several ticked
/// movies at once. Only close matches that keep the same IMDb ID qualify;
/// the server applies that rule whatever the page sends.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    [Fact]
    public async Task BulkKeepFileNames_AppliesToEveryTickedCloseMatch_WithOnePlanRebuild()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var (a, b, c) = (setup.CloseMatches[0], setup.CloseMatches[1], setup.CloseMatches[2]);
        var lookupsBefore = setup.Omdb.RequestCount;
        var rebuildsBefore = setup.Plan.CallCount;

        var model = setup.Page();
        await model.OnPostKeepSelectedFileNamesAsync(new List<Guid> { a.Id, b.Id });

        foreach (var kept in new[] { a, b })
        {
            Assert.True(kept.IsFileIdentityKept);
            Assert.True(kept.MetadataFetched);
            Assert.False(kept.HasMetadataReviewReason);
            Assert.Equal("PG-13", kept.MpaRating);
        }

        // The file's years, not OMDb's later ones.
        Assert.Equal(2019, a.Year);
        Assert.Equal(2018, b.Year);

        // Not ticked: untouched and still the owner's decision.
        Assert.False(c.IsFileIdentityKept);
        Assert.True(c.NeedsReview);
        Assert.Equal(MetadataLookupFailureType.ImdbIdentityConflict, c.MetadataLookupFailureType);

        Assert.Equal(lookupsBefore + 2, setup.Omdb.RequestCount);
        Assert.Equal(rebuildsBefore + 1, setup.Plan.CallCount);
        Assert.Null(setup.Store.GetDryRunApproval());
        Assert.Equal(
            "Keep file's name was applied to 2 movies. Run a new dry run before live commit.",
            model.TempData["Message"]?.ToString());
        Assert.False(setup.Progress.IsRunning);
        Assert.False(setup.Progress.IsBusy);
        Assert.Equal(string.Empty, setup.Progress.Step);
    }

    [Fact]
    public async Task BulkAcceptMatches_NamesEveryTickedCloseMatchAsOmdbDoes()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var (a, b, c) = (setup.CloseMatches[0], setup.CloseMatches[1], setup.CloseMatches[2]);
        var rebuildsBefore = setup.Plan.CallCount;

        var model = setup.Page();
        await model.OnPostAcceptSelectedMatchesAsync(new List<Guid> { a.Id, b.Id });

        Assert.Equal(2020, a.Year);
        Assert.Equal(2019, b.Year);
        Assert.All(new[] { a, b }, accepted =>
        {
            Assert.True(accepted.MetadataFetched);
            Assert.False(accepted.IsFileIdentityKept);
            Assert.False(accepted.HasMetadataReviewReason);
        });
        Assert.True(c.NeedsReview);
        Assert.Equal(1990, c.Year);
        Assert.Equal(rebuildsBefore + 1, setup.Plan.CallCount);
        Assert.Null(setup.Store.GetDryRunApproval());
        Assert.Equal(
            "Accept match was applied to 2 movies. Run a new dry run before live commit.",
            model.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task BulkChoice_LeavesAloneTickedMoviesThatMustBeDecidedOneAtATime()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var close = setup.CloseMatches[0];
        var ticked = new List<Guid>
        {
            close.Id,
            setup.DistantMatch.Id,
            setup.TitleGuess.Id,
            setup.Identified.Id,
            Guid.NewGuid()
        };

        var model = setup.Page();
        await model.OnPostKeepSelectedFileNamesAsync(ticked);

        Assert.True(close.IsFileIdentityKept);
        Assert.Equal(
            "Keep file's name was applied to 1 movie. " +
            "4 ticked movies were left unchanged, to be decided one at a time. " +
            "Run a new dry run before live commit.",
            model.TempData["Message"]?.ToString());

        // A two-year gap can mean the file carries the wrong IMDb ID.
        Assert.False(setup.DistantMatch.IsFileIdentityKept);
        Assert.True(setup.DistantMatch.NeedsReview);
        Assert.Equal(1965, setup.DistantMatch.Year);
        Assert.Equal(1967, setup.DistantMatch.SuggestedYear);

        // Accepting a title-based guess would give the movie an IMDb ID.
        Assert.Null(setup.TitleGuess.ImdbId);
        Assert.True(setup.TitleGuess.NeedsReview);

        Assert.Null(setup.Identified.FileIdentityKeptForImdbId);
        Assert.Equal("tt1234567", setup.Identified.ImdbId);
    }

    [Fact]
    public async Task BulkChoice_WithNothingTickedOrNothingThatQualifies_ChangesNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var approval = setup.Store.GetDryRunApproval();
        Assert.NotNull(approval);
        var lookupsBefore = setup.Omdb.RequestCount;
        var rebuildsBefore = setup.Plan.CallCount;

        var nothingTicked = setup.Page();
        await nothingTicked.OnPostAcceptSelectedMatchesAsync(null);
        var emptyList = setup.Page();
        await emptyList.OnPostKeepSelectedFileNamesAsync(new List<Guid>());
        var noneQualify = setup.Page();
        await noneQualify.OnPostAcceptSelectedMatchesAsync(
            new List<Guid> { setup.DistantMatch.Id, setup.TitleGuess.Id, setup.Identified.Id });

        Assert.Contains("No movies were ticked", nothingTicked.TempData["Message"]?.ToString());
        Assert.Contains("No movies were ticked", emptyList.TempData["Message"]?.ToString());
        Assert.Equal(
            "None of the ticked movies can use Accept match together with others. Nothing was changed.",
            noneQualify.TempData["Message"]?.ToString());
        Assert.Equal(approval, setup.Store.GetDryRunApproval());
        Assert.Equal(lookupsBefore, setup.Omdb.RequestCount);
        Assert.Equal(rebuildsBefore, setup.Plan.CallCount);
        Assert.Equal(1965, setup.DistantMatch.Year);
        Assert.Null(setup.TitleGuess.ImdbId);
    }

    [Fact]
    public async Task RealFiles_BulkKeepFileNames_MovesNothingUntilANewDryRun_ThenMovesUnderTheFilesNames()
    {
        using var workspace = new TempWorkspace("PIM-Index-BulkKeep");
        Directory.CreateDirectory(workspace.DestinationRoot);
        var configuration = CreateConfiguration(
            workspace,
            LibraryGoal.OrganizeNewMovies,
            removeEmptySourceFolders: false);
        configuration["Omdb:ApiKey"] = "test-key";
        var profile = new DestinationProfile
        {
            Name = "Bulk Keep Profile",
            DestinationRoot = workspace.DestinationRoot
        };
        var handler = new OmdbByIdHandler();
        var omdb = new OmdbMetadataService(new HttpClient(handler), configuration);
        var movies = new List<Movie>();

        foreach (var (title, fileYear, imdbId) in new[]
                 {
                     ("Arctic", 2018, "tt6820256"),
                     ("Awakenings", 1990, "tt0099077")
                 })
        {
            handler.List(imdbId, title, fileYear + 1);
            var name = $"{title} ({fileYear}) {{imdb-{imdbId}}}";
            var file = Path.Combine(workspace.SourceRoot, name, $"{name}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "movie");
            var movie = new Movie
            {
                Title = title,
                Year = fileYear,
                ImdbId = imdbId,
                OriginalFilePath = file,
                FileName = Path.GetFileName(file),
                DirectoryPath = Path.GetDirectoryName(file),
                FileSizeBytes = new FileInfo(file).Length
            };
            await omdb.EnrichAsync(movie);
            Assert.True(MovieResultRow.IsCloseMatchNameChoice(movie));
            movies.Add(movie);
        }

        var store = CreateStore(configuration);
        store.SaveMovies(movies, workspace.SourceRoot);
        var sourceFiles = movies.Select(movie => movie.OriginalFilePath).ToList();

        async Task<IndexModel> CommitAsync(bool dryRun)
        {
            var (rename, plan) = CreateRealServices(configuration);
            var model = CreateModel(store, configuration, profile, rename: rename, plan: plan);
            model.DryRun = dryRun;
            await model.OnPostCommitAsync();
            return model;
        }

        await CommitAsync(dryRun: true);
        Assert.All(movies, movie => Assert.False(movie.ApprovedForCommit));

        var (bulkRename, bulkPlan) = CreateRealServices(configuration);
        var bulk = CreateModel(
            store,
            configuration,
            profile,
            rename: bulkRename,
            plan: bulkPlan,
            suggestion: new MetadataSuggestionService(omdb, bulkPlan));
        await bulk.OnPostKeepSelectedFileNamesAsync(movies.Select(movie => movie.Id).ToList());

        Assert.StartsWith("Keep file's name was applied to 2 movies.", bulk.TempData["Message"]?.ToString());
        Assert.All(movies, movie => Assert.False(movie.NeedsReview));

        // The choice alone moves nothing, and a live commit still needs a
        // new dry run.
        var refused = await CommitAsync(dryRun: false);
        Assert.Contains("does not have a matching dry-run approval", refused.TempData["Message"]?.ToString());
        Assert.All(sourceFiles, file => Assert.True(File.Exists(file)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        await CommitAsync(dryRun: true);
        Assert.All(sourceFiles, file => Assert.True(File.Exists(file)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        var live = await CommitAsync(dryRun: false);

        Assert.StartsWith("Changes Applied", live.TempData["Message"]?.ToString());
        Assert.All(sourceFiles, file => Assert.False(File.Exists(file)));
        Assert.True(File.Exists(Path.Combine(
            workspace.DestinationRoot,
            "Arctic (2018) {imdb-tt6820256}",
            "Arctic (2018) {imdb-tt6820256}.mp4")));
        Assert.True(File.Exists(Path.Combine(
            workspace.DestinationRoot,
            "Awakenings (1990) {imdb-tt0099077}",
            "Awakenings (1990) {imdb-tt0099077}.mp4")));
    }

    /// <summary>
    /// A scan with three close matches, one distant match, one title-based
    /// guess and one identified movie, an approved dry run, and a stubbed OMDb.
    /// </summary>
    private sealed class BulkSetup
    {
        public required JsonWorkflowStateStore Store { get; init; }

        public required IConfiguration Configuration { get; init; }

        public required DestinationProfile Profile { get; init; }

        public required OmdbByIdHandler Omdb { get; init; }

        public required OmdbMetadataService Metadata { get; init; }

        public required RecordingPlanService Plan { get; init; }

        public required ScanProgress Progress { get; init; }

        public required List<Movie> CloseMatches { get; init; }

        public required Movie DistantMatch { get; init; }

        public required Movie TitleGuess { get; init; }

        public required Movie Identified { get; init; }

        public IndexModel Page() => CreateModel(
            Store,
            Configuration,
            Profile,
            plan: Plan,
            suggestion: new MetadataSuggestionService(Metadata, Plan),
            progress: Progress);

        public static async Task<BulkSetup> CreateAsync(TempWorkspace workspace)
        {
            var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
            configuration["Omdb:ApiKey"] = "test-key";
            var profile = CreateProfile(workspace);
            var handler = new OmdbByIdHandler();
            var metadata = new OmdbMetadataService(new HttpClient(handler), configuration);

            async Task<Movie> ListedDifferentlyAsync(string title, int fileYear, int omdbYear, string imdbId)
            {
                handler.List(imdbId, title, omdbYear);
                var name = $"{title} ({fileYear}) {{imdb-{imdbId}}}.mkv";
                var movie = new Movie
                {
                    Title = title,
                    Year = fileYear,
                    ImdbId = imdbId,
                    FileName = name,
                    OriginalFilePath = Path.Combine(workspace.SourceRoot, name)
                };
                await metadata.EnrichAsync(movie);
                return movie;
            }

            var closeMatches = new List<Movie>
            {
                await ListedDifferentlyAsync("1917", 2019, 2020, "tt8579674"),
                await ListedDifferentlyAsync("Arctic", 2018, 2019, "tt6820256"),
                await ListedDifferentlyAsync("Awakenings", 1990, 1991, "tt0099077")
            };
            var distantMatch = await ListedDifferentlyAsync(
                "For a Few Dollars More", 1965, 1967, "tt0059578");

            var titleGuess = new Movie
            {
                Title = "Back to the Future",
                Year = 1986,
                FileName = "Back.to.the.Future.1986.mkv",
                OriginalFilePath = Path.Combine(workspace.SourceRoot, "Back.to.the.Future.1986.mkv"),
                SuggestedTitle = "Back to the Future",
                SuggestedYear = 1985,
                SuggestedImdbId = "tt0088763",
                MatchConfidence = 90
            };
            titleGuess.SetMetadataReview(
                "Possible OMDb match: Back to the Future (1985)",
                MetadataLookupFailureType.FuzzyCandidateYearConflict);

            var identified = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);

            Assert.All(closeMatches, movie => Assert.True(MovieResultRow.IsCloseMatchNameChoice(movie)));
            Assert.False(MovieResultRow.IsCloseMatchNameChoice(distantMatch));
            Assert.False(MovieResultRow.IsCloseMatchNameChoice(titleGuess));
            Assert.False(MovieResultRow.IsCloseMatchNameChoice(identified));

            var store = CreateStore(configuration);
            store.SaveMovies(
                closeMatches.Concat(new[] { distantMatch, titleGuess, identified }).ToList(),
                workspace.SourceRoot);

            var setup = new BulkSetup
            {
                Store = store,
                Configuration = configuration,
                Profile = profile,
                Omdb = handler,
                Metadata = metadata,
                Plan = new RecordingPlanService(),
                Progress = new ScanProgress(),
                CloseMatches = closeMatches,
                DistantMatch = distantMatch,
                TitleGuess = titleGuess,
                Identified = identified
            };

            // An approved dry run, to show which choices cancel it.
            var dryRun = setup.Page();
            dryRun.DryRun = true;
            await dryRun.OnPostCommitAsync();
            Assert.NotNull(store.GetDryRunApproval());

            return setup;
        }
    }

    /// <summary>Stubbed OMDb that answers lookups by IMDb ID.</summary>
    private sealed class OmdbByIdHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _listings =
            new(StringComparer.OrdinalIgnoreCase);

        public int RequestCount { get; private set; }

        public void List(string imdbId, string title, int year)
        {
            _listings[imdbId] =
                $$"""{"Title":"{{title}}","Year":"{{year}}","Rated":"PG-13","Genre":"Drama","imdbID":"{{imdbId}}","Response":"True"}""";
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            var query = request.RequestUri!.Query.TrimStart('?').Split('&');
            var imdbId = query
                .Where(part => part.StartsWith("i=", StringComparison.Ordinal))
                .Select(part => Uri.UnescapeDataString(part[2..]))
                .FirstOrDefault();
            var body = imdbId != null && _listings.TryGetValue(imdbId, out var listing)
                ? listing
                : """{"Response":"False","Error":"Incorrect IMDb ID."}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
