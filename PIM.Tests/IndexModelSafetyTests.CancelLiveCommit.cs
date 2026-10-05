using PIM.Core.Models;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Page handling for stopping a live commit with Cancel.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    [Fact]
    public async Task LiveCommit_CanBeStoppedWhileItRuns_ButADryRunSimulationCannot()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie> { CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies) },
            workspace.SourceRoot);
        var progress = new ScanProgress();
        var cancelAvailable = new List<bool>();
        var rename = new RecordingRenameService
        {
            OnExecute = () => cancelAvailable.Add(progress.CanCancel)
        };
        IndexModel Page() => CreateModel(store, configuration, profile, rename: rename, progress: progress);

        var dryRun = Page();
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();

        var live = Page();
        live.DryRun = false;
        await live.OnPostCommitAsync();

        Assert.Equal(new[] { true, false }, rename.DryRunFlags);
        Assert.Equal(new[] { false, true }, cancelAvailable);
        Assert.False(rename.Tokens[0].CanBeCanceled);
        Assert.True(rename.Tokens[1].CanBeCanceled);

        // Cancel is offered only while the commit is running.
        Assert.False(progress.CanCancel);
        Assert.False(progress.IsBusy);
    }

    [Fact]
    public async Task LiveCommit_Stopped_SaysWhatMovedAndWhatDidNot_AndUsesUpTheApproval()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var movies = Enumerable.Range(1, 3)
            .Select(number =>
            {
                var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
                movie.FileName = $"Stoppable.Movie.{number}.2024.mkv";
                movie.OriginalFilePath = Path.Combine(workspace.SourceRoot, movie.FileName);
                movie.ImdbId = $"tt700000{number}";
                return movie;
            })
            .ToList();
        store.SaveMovies(movies, workspace.SourceRoot);
        var progress = new ScanProgress();
        var approving = CreateModel(store, configuration, profile, progress: progress);
        approving.DryRun = true;
        await approving.OnPostCommitAsync();
        Assert.NotNull(store.GetDryRunApproval());

        // The owner presses Cancel during the run; one file had been moved.
        var tokenStopped = false;
        var rename = new RecordingRenameService
        {
            OnExecute = () => Assert.True(progress.RequestCancel()),
            Outcome = (approved, token) =>
            {
                tokenStopped = token.IsCancellationRequested;
                return new RenameRunOutcome(MovedCount: 1, NotReachedCount: approved.Count - 1);
            }
        };
        var live = CreateModel(store, configuration, profile, rename: rename, progress: progress);
        live.DryRun = false;

        await live.OnPostCommitAsync();

        Assert.True(tokenStopped);
        Assert.Equal(
            "Live commit stopped. 1 of 3 approved files was moved and stays moved. " +
            "2 were not reached and are still where they were. " +
            "Run Scan Movies to refresh the list before you continue.",
            live.TempData["Message"]?.ToString());
        Assert.Null(store.GetDryRunApproval());
        Assert.False(progress.CanCancel);
        Assert.False(progress.CancelRequested);
        Assert.False(progress.IsBusy);
    }

    [Fact]
    public async Task LiveCommit_StoppedDuringTheReCheck_MovesNothing_AndKeepsTheApproval()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie> { CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies) },
            workspace.SourceRoot);
        var progress = new ScanProgress();
        var rename = new RecordingRenameService();
        var approving = CreateModel(store, configuration, profile, rename: rename, progress: progress);
        approving.DryRun = true;
        await approving.OnPostCommitAsync();
        var approval = store.GetDryRunApproval();
        Assert.NotNull(approval);

        // Cancel arrives while the plan is being re-checked, before any move.
        var stoppedEarly = CreateModel(
            store,
            configuration,
            profile,
            rename: rename,
            plan: new RecordingPlanService(_ => progress.RequestCancel()),
            progress: progress);
        stoppedEarly.DryRun = false;
        await stoppedEarly.OnPostCommitAsync();

        Assert.Equal(new[] { true }, rename.DryRunFlags);
        Assert.Contains(
            "stopped before any file was moved",
            stoppedEarly.TempData["Message"]?.ToString());
        Assert.Equal(approval, store.GetDryRunApproval());
        Assert.False(progress.CanCancel);
        Assert.False(progress.IsBusy);

        // The same approval still authorises the commit when it is run again.
        var live = CreateModel(store, configuration, profile, rename: rename, progress: progress);
        live.DryRun = false;
        await live.OnPostCommitAsync();

        Assert.Equal(new[] { true, false }, rename.DryRunFlags);
        Assert.StartsWith("Changes Applied", live.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task RealFiles_LiveCommitStoppedBetweenFiles_MovesOnlyTheFilesAlreadyStarted()
    {
        using var workspace = new TempWorkspace("PIM-Index-CancelCommit");
        Directory.CreateDirectory(workspace.DestinationRoot);
        var configuration = CreateConfiguration(
            workspace,
            LibraryGoal.OrganizeNewMovies,
            removeEmptySourceFolders: false);
        var profile = new DestinationProfile
        {
            Name = "Cancel Commit Profile",
            DestinationRoot = workspace.DestinationRoot
        };
        var movies = Enumerable.Range(1, 3)
            .Select(number =>
            {
                var file = Path.Combine(
                    workspace.SourceRoot,
                    $"Cancel Movie {number} (2020)",
                    $"Cancel.Movie.{number}.2020.mkv");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "movie");
                var movie = CreateIdentifiedFileMovie(file);
                movie.Title = $"Cancel Movie {number}";
                movie.ImdbId = $"tt202000{number}";
                return movie;
            })
            .ToList();
        var store = CreateStore(configuration);
        store.SaveMovies(movies, workspace.SourceRoot);
        var progress = new ScanProgress();

        async Task<IndexModel> CommitAsync(bool dryRun, bool stopAfterFirstMove = false)
        {
            var (rename, plan) = CreateRealServices(
                configuration,
                wrapJournal: stopAfterFirstMove
                    ? journal => new ActAfterMovesJournal(
                        journal,
                        afterMoves: 1,
                        () => Assert.True(progress.RequestCancel()))
                    : null);
            var model = CreateModel(
                store,
                configuration,
                profile,
                rename: rename,
                plan: plan,
                progress: progress);
            model.DryRun = dryRun;
            await model.OnPostCommitAsync();
            return model;
        }

        await CommitAsync(dryRun: true);
        Assert.All(movies, movie => Assert.True(movie.ApprovedForCommit));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        // The owner presses Cancel while the first file is being moved.
        var stopped = await CommitAsync(dryRun: false, stopAfterFirstMove: true);

        Assert.StartsWith(
            "Live commit stopped. 1 of 3 approved files was moved and stays moved. " +
            "2 were not reached and are still where they were.",
            stopped.TempData["Message"]?.ToString());
        var moved = Assert.Single(movies, movie => !File.Exists(movie.OriginalFilePath));
        Assert.True(File.Exists(moved.TargetPath!));
        Assert.Single(Directory.EnumerateFiles(
            workspace.DestinationRoot,
            "*",
            SearchOption.AllDirectories));
        Assert.Equal(2, movies.Count(movie => File.Exists(movie.OriginalFilePath)));
        Assert.Null(store.GetDryRunApproval());

        // The stopped commit used up the approval: nothing more moves until a
        // new dry run has been reviewed.
        var refused = await CommitAsync(dryRun: false);
        Assert.Contains(
            "does not have a matching dry-run approval",
            refused.TempData["Message"]?.ToString());
        Assert.Equal(2, movies.Count(movie => File.Exists(movie.OriginalFilePath)));
        Assert.Single(Directory.EnumerateFiles(
            workspace.DestinationRoot,
            "*",
            SearchOption.AllDirectories));
    }
}
