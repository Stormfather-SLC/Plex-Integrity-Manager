using System.Net;
using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Page handlers for keeping the title and year from the file name when OMDb
/// lists the movie's IMDb ID under different wording.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    private const string Omdb1917Listing = """
        {
          "Title": "1917",
          "Year": "2020",
          "Rated": "R",
          "Genre": "Action, Drama, War",
          "imdbID": "tt8579674",
          "Response": "True"
        }
        """;

    [Fact]
    public async Task KeepFileIdentity_InvalidatesAnEarlierDryRunApproval_AndUndoDoesToo()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        MarkAsListedDifferentlyByOmdb(movie);
        var ready = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        ready.FileName = "Another.Movie.2024.mkv";
        ready.OriginalFilePath = Path.Combine(workspace.SourceRoot, ready.FileName);
        ready.ImdbId = "tt7777777";
        store.SaveMovies(new List<Movie> { movie, ready }, workspace.SourceRoot);
        var suggestion = new NoOpSuggestionService();
        IndexModel Page() => CreateModel(store, configuration, profile, suggestion: suggestion);

        async Task DryRunAsync()
        {
            var dryRun = Page();
            dryRun.DryRun = true;
            await dryRun.OnPostCommitAsync();
            Assert.NotNull(store.GetDryRunApproval());
        }

        await DryRunAsync();
        var keep = Page();
        await keep.OnPostKeepFileIdentityAsync(movie.Id);

        Assert.Null(store.GetDryRunApproval());
        Assert.Equal(new[] { movie.Id }, suggestion.KeptFileIdentities);
        Assert.Contains("keeps the name from its file, Handler Movie (2024)", keep.TempData["Message"]?.ToString());
        var rename = new RecordingRenameService();
        var live = CreateModel(store, configuration, profile, rename: rename);
        live.DryRun = false;
        await live.OnPostCommitAsync();
        Assert.Equal(0, rename.ExecuteCount);

        await DryRunAsync();
        var undo = Page();
        await undo.OnPostUndoKeepFileIdentityAsync(movie.Id);

        Assert.Null(store.GetDryRunApproval());
        Assert.Equal(new[] { movie.Id }, suggestion.UndoneFileIdentities);
        Assert.Contains("no longer keeps the name from its file", undo.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task KeepFileIdentity_ForAMovieWithoutThatChoice_ChangesNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var suggestion = new NoOpSuggestionService();
        var plan = new RecordingPlanService();
        IndexModel Page() => CreateModel(
            store,
            configuration,
            profile,
            plan: plan,
            suggestion: suggestion);

        var keep = Page();
        await keep.OnPostKeepFileIdentityAsync(movie.Id);
        var undo = Page();
        await undo.OnPostUndoKeepFileIdentityAsync(movie.Id);
        var unknown = Page();
        await unknown.OnPostKeepFileIdentityAsync(Guid.NewGuid());

        Assert.Empty(suggestion.KeptFileIdentities);
        Assert.Empty(suggestion.UndoneFileIdentities);
        Assert.Equal(0, plan.CallCount);
        Assert.Null(movie.FileIdentityKeptForImdbId);
        Assert.Contains("Nothing was changed", keep.TempData["Message"]?.ToString());
        Assert.Contains("Nothing was changed", undo.TempData["Message"]?.ToString());
        Assert.Contains("Nothing was changed", unknown.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task RealFiles_KeepFileIdentity_MovesUnderTheFilesOwnNameOnlyAfterTheDecisionAndANewDryRun()
    {
        using var workspace = new TempWorkspace("PIM-Index-KeepName");
        Directory.CreateDirectory(workspace.DestinationRoot);
        var sourceFile = Path.Combine(
            workspace.SourceRoot,
            "1917 (2019) {imdb-tt8579674}",
            "1917 (2019) {imdb-tt8579674}.m4v");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "movie");
        var configuration = CreateConfiguration(
            workspace,
            LibraryGoal.OrganizeNewMovies,
            removeEmptySourceFolders: false);
        configuration["Omdb:ApiKey"] = "test-key";
        var profile = new DestinationProfile
        {
            Name = "Keep Name Profile",
            DestinationRoot = workspace.DestinationRoot
        };

        // Stubbed OMDb: lists the file's IMDb ID as 1917 (2020).
        var omdb = new OmdbMetadataService(
            new HttpClient(new RepeatingOmdbHandler(Omdb1917Listing)),
            configuration);
        var movie = new Movie
        {
            Title = "1917",
            Year = 2019,
            ImdbId = "tt8579674",
            OriginalFilePath = sourceFile,
            FileName = Path.GetFileName(sourceFile),
            DirectoryPath = Path.GetDirectoryName(sourceFile),
            FileSizeBytes = new FileInfo(sourceFile).Length
        };
        await omdb.EnrichAsync(movie);
        Assert.True(movie.CanKeepFileIdentity);
        var store = CreateStore(configuration);
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);

        async Task<IndexModel> CommitAsync(bool dryRun)
        {
            var (rename, plan) = CreateRealServices(configuration);
            var model = CreateModel(store, configuration, profile, rename: rename, plan: plan);
            model.DryRun = dryRun;
            await model.OnPostCommitAsync();
            return model;
        }

        // Undecided: the dry run plans no move for it, and nothing moves.
        await CommitAsync(dryRun: true);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.True(File.Exists(sourceFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        var (keepRename, keepPlan) = CreateRealServices(configuration);
        var keep = CreateModel(
            store,
            configuration,
            profile,
            rename: keepRename,
            plan: keepPlan,
            suggestion: new MetadataSuggestionService(omdb, keepPlan));
        await keep.OnPostKeepFileIdentityAsync(movie.Id);

        Assert.Equal("1917", movie.Title);
        Assert.Equal(2019, movie.Year);
        Assert.Equal("tt8579674", movie.ImdbId);
        Assert.False(movie.NeedsReview);
        Assert.Contains("keeps the name from its file, 1917 (2019)", keep.TempData["Message"]?.ToString());
        var row = MovieResultRow.Create(movie, workspace.SourceRoot, workspace.DestinationRoot);
        Assert.Equal(MovieResultState.Ready, row.State);
        Assert.True(row.CanUndoKeepFileIdentity);

        // The decision alone moves nothing, and a live commit still needs a
        // new dry run.
        var refused = await CommitAsync(dryRun: false);
        Assert.Contains("does not have a matching dry-run approval", refused.TempData["Message"]?.ToString());
        Assert.True(File.Exists(sourceFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        await CommitAsync(dryRun: true);
        Assert.True(movie.ApprovedForCommit);
        Assert.True(File.Exists(sourceFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        var live = await CommitAsync(dryRun: false);

        var expectedTarget = Path.Combine(
            workspace.DestinationRoot,
            "1917 (2019) {imdb-tt8579674}",
            "1917 (2019) {imdb-tt8579674}.m4v");
        Assert.StartsWith("Changes Applied", live.TempData["Message"]?.ToString());
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(expectedTarget));
        Assert.Single(Directory.EnumerateFiles(workspace.DestinationRoot, "*", SearchOption.AllDirectories));
        var journal = Directory.GetFiles(Path.Combine(workspace.Root, "Journal"), "*live-commit*.jsonl")
            .OrderBy(file => file)
            .Last();
        Assert.Contains(
            File.ReadAllLines(journal),
            line => line.Contains("\"Event\":\"Moved\"") &&
                    line.Contains("kept the title and year from the file name") &&
                    line.Contains("1917 (2020)"));
    }

    /// <summary>
    /// A movie in the state OMDb leaves it in when it finds the IMDb ID under
    /// a different year than the file.
    /// </summary>
    private static void MarkAsListedDifferentlyByOmdb(Movie movie)
    {
        movie.MetadataFetched = false;
        movie.ApprovedForCommit = false;
        movie.SuggestedTitle = movie.Title;
        movie.SuggestedYear = movie.Year + 1;
        movie.SuggestedImdbId = movie.ImdbId;
        movie.MetadataMatchOrigin = MetadataMatchOrigin.ImdbId;
        movie.SetMetadataReview(
            "The provided IMDb ID does not match the movie year provided.",
            MetadataLookupFailureType.ImdbIdentityConflict);
    }

    private sealed class RepeatingOmdbHandler : HttpMessageHandler
    {
        private readonly string _response;

        public RepeatingOmdbHandler(string response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response)
            });
        }
    }
}
