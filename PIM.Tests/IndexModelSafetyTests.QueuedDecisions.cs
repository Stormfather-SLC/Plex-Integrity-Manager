using PIM.Core.Models;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Row choices wait in the page and are applied together: each is checked
/// as if made on its own, and the plan is rebuilt once.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    private static QueuedDecision Change(Movie movie, string kind, string? value = null) =>
        new() { MovieId = movie.Id, Kind = kind, Value = value };

    [Fact]
    public async Task ApplyDecisions_AppliesEveryWaitingChange_WithOnePlanRebuild()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var (a, b, c) = (setup.CloseMatches[0], setup.CloseMatches[1], setup.CloseMatches[2]);
        setup.Omdb.List("tt0088763", "Back to the Future", 1986);
        var lookupsBefore = setup.Omdb.RequestCount;
        var rebuildsBefore = setup.Plan.CallCount;

        var model = setup.Page();
        await model.OnPostApplyDecisionsAsync(new List<QueuedDecision>
        {
            Change(a, QueuedDecision.AcceptMatch),
            Change(b, QueuedDecision.KeepFileName),
            Change(setup.TitleGuess, QueuedDecision.ImdbId, "https://www.imdb.com/title/tt0088763/")
        });

        Assert.Equal(2020, a.Year);
        Assert.False(a.IsFileIdentityKept);
        Assert.True(b.IsFileIdentityKept);
        Assert.Equal(2018, b.Year);
        Assert.Equal("tt0088763", setup.TitleGuess.ImdbId);

        // Not part of the batch: still the owner's decision.
        Assert.True(c.NeedsReview);
        Assert.Equal(1990, c.Year);

        Assert.Equal(lookupsBefore + 3, setup.Omdb.RequestCount);
        Assert.Equal(rebuildsBefore + 1, setup.Plan.CallCount);
        Assert.Null(setup.Store.GetDryRunApproval());

        var message = model.TempData["Message"]?.ToString();
        Assert.StartsWith("Applied 3 changes. tt0088763 ", message);
        Assert.EndsWith("Run a new dry run before live commit.", message);
        Assert.False(setup.Progress.IsRunning);
        Assert.False(setup.Progress.IsBusy);
        Assert.Equal(string.Empty, setup.Progress.Step);
    }

    [Fact]
    public async Task ApplyDecisions_LeavesOutChangesThatNoLongerApply()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var (a, b) = (setup.CloseMatches[0], setup.CloseMatches[1]);
        var rebuildsBefore = setup.Plan.CallCount;
        var lookupsBefore = setup.Omdb.RequestCount;

        var model = setup.Page();
        await model.OnPostApplyDecisionsAsync(new List<QueuedDecision>
        {
            // An identified movie has no name choice to make.
            Change(setup.Identified, QueuedDecision.KeepFileName),
            // Two changes for one movie: neither is applied.
            Change(a, QueuedDecision.AcceptMatch),
            Change(a, QueuedDecision.KeepFileName),
            // Not an IMDb ID.
            Change(b, QueuedDecision.ImdbId, "not an id"),
            new() { MovieId = Guid.NewGuid(), Kind = QueuedDecision.AcceptMatch },
            Change(setup.DistantMatch, "deleteEverything")
        });

        Assert.Null(setup.Identified.FileIdentityKeptForImdbId);
        Assert.Equal(2019, a.Year);
        Assert.False(a.IsFileIdentityKept);
        Assert.Equal("tt6820256", b.ImdbId);
        Assert.Equal(1965, setup.DistantMatch.Year);
        Assert.Equal(rebuildsBefore, setup.Plan.CallCount);
        Assert.Equal(lookupsBefore, setup.Omdb.RequestCount);
        Assert.Equal(
            "None of the 6 waiting changes could be applied, so no movie was changed: " +
            $"Keep file's name for '{setup.Identified.FileName}', which no longer applies; " +
            $"Accept match for '{a.FileName}', which had more than one change waiting; " +
            $"Keep file's name for '{a.FileName}', which had more than one change waiting; " +
            $"IMDb ID for '{b.FileName}', which no longer applies; " +
            "Accept match for a movie no longer in the scan; " +
            $"Unknown change for '{setup.DistantMatch.FileName}', which no longer applies.",
            model.TempData["Message"]?.ToString());

        // Any change sent cancels the earlier dry run, applied or not.
        Assert.Null(setup.Store.GetDryRunApproval());
    }

    [Fact]
    public async Task ApplyDecisions_ReportsTheChangesLeftOut_AlongsideThoseApplied()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var a = setup.CloseMatches[0];

        var model = setup.Page();
        await model.OnPostApplyDecisionsAsync(new List<QueuedDecision>
        {
            Change(a, QueuedDecision.KeepFileName),
            Change(setup.Identified, QueuedDecision.UndoKeepFileName)
        });

        Assert.True(a.IsFileIdentityKept);
        Assert.Equal(
            "Applied 1 change. " +
            $"Left out: Undo keep name for '{setup.Identified.FileName}', which no longer applies. " +
            "Run a new dry run before live commit.",
            model.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task ApplyDecisions_WithNothingWaiting_ChangesNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var approval = setup.Store.GetDryRunApproval();

        var model = setup.Page();
        await model.OnPostApplyDecisionsAsync(null);

        Assert.Equal("No changes were waiting. Nothing was changed.", model.TempData["Message"]?.ToString());
        Assert.Equal(approval, setup.Store.GetDryRunApproval());
    }

    [Fact]
    public async Task ApplyDecisions_WhilePimIsBusy_IsRefusedAndChangesNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var setup = await BulkSetup.CreateAsync(workspace);
        var a = setup.CloseMatches[0];
        var approval = setup.Store.GetDryRunApproval();
        Assert.True(setup.Progress.TryBeginAction("a dry run"));

        var model = setup.Page();
        await model.OnPostApplyDecisionsAsync(new List<QueuedDecision>
        {
            Change(a, QueuedDecision.AcceptMatch)
        });

        Assert.StartsWith("PIM is busy with a dry run.", model.TempData["Message"]?.ToString());
        Assert.Equal(2019, a.Year);
        Assert.Equal(approval, setup.Store.GetDryRunApproval());
    }

    [Fact]
    public async Task RealFiles_ApplyDecisions_MovesNothingUntilANewDryRun()
    {
        using var workspace = new TempWorkspace("PIM-Index-QueuedDecisions");
        Directory.CreateDirectory(workspace.DestinationRoot);
        var configuration = CreateConfiguration(
            workspace,
            LibraryGoal.OrganizeNewMovies,
            removeEmptySourceFolders: false);
        configuration["Omdb:ApiKey"] = "test-key";
        var profile = new DestinationProfile
        {
            Name = "Queued Decisions Profile",
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
        Assert.All(movies, movie => Assert.True(movie.NeedsReview));

        var (applyRename, applyPlan) = CreateRealServices(configuration);
        var apply = CreateModel(
            store,
            configuration,
            profile,
            rename: applyRename,
            plan: applyPlan,
            suggestion: new MetadataSuggestionService(omdb, applyPlan));
        await apply.OnPostApplyDecisionsAsync(new List<QueuedDecision>
        {
            Change(movies[0], QueuedDecision.KeepFileName),
            Change(movies[1], QueuedDecision.AcceptMatch)
        });

        Assert.StartsWith("Applied 2 changes.", apply.TempData["Message"]?.ToString());
        Assert.All(movies, movie => Assert.False(movie.NeedsReview));

        // Applying the changes moves nothing, and a live commit still needs
        // a new dry run.
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
            "Awakenings (1991) {imdb-tt0099077}",
            "Awakenings (1991) {imdb-tt0099077}.mp4")));
    }
}
