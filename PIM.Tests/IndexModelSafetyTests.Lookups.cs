using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Which movies are looked up again, and how the progress display names the
/// part of the action its numbers belong to.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    [Fact]
    public async Task IdentifiedMovieWithoutARating_IsNotLookedUpAgain_EvenWhenTheProfileSortsByRating()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        profile.OrganizationLevels.Add(
            DestinationOrganizationLevel.Create(OrganizationLevelType.MpaRating));
        profile.OrganizationLevels.Add(
            DestinationOrganizationLevel.Create(OrganizationLevelType.PrimaryGenre));

        // Identified, but OMDb has no rating or genre for it.
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        Assert.True(movie.MetadataFetched);
        Assert.Null(movie.MpaRating);
        Assert.Null(movie.PrimaryGenre);
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var metadata = new CountingMetadataService();

        await CreateModel(store, configuration, profile, metadata: metadata).OnPostEnrichAsync();
        var dryRun = CreateModel(store, configuration, profile, metadata: metadata);
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();

        Assert.Equal(0, metadata.CallCount);
        Assert.NotNull(store.GetDryRunApproval());
    }

    [Theory]
    [InlineData(MetadataLookupFailureType.LowConfidence)]
    [InlineData(MetadataLookupFailureType.MovieNotFound)]
    [InlineData(MetadataLookupFailureType.NetworkFailure)]
    public async Task DryRun_DoesNotRepeatAnAttemptedLookup_ButIdentifyMoviesDoes(
        MetadataLookupFailureType earlierOutcome)
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var ready = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        var waiting = NotLookedUp(workspace, "Some.Film.2004.mkv");
        waiting.SetMetadataReview("Earlier lookup needs the owner", earlierOutcome);
        store.SaveMovies(new List<Movie> { ready, waiting }, workspace.SourceRoot);
        var metadata = new CountingMetadataService();
        var rename = new RecordingRenameService();

        var dryRun = CreateModel(store, configuration, profile, metadata: metadata, rename: rename);
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();

        // The dry run reports the plan as it stands: no OMDb call, the movie
        // still waits for the owner, and the ready movie is still approved.
        Assert.Equal(0, metadata.CallCount);
        Assert.True(waiting.NeedsReview);
        Assert.Equal("Earlier lookup needs the owner", waiting.MetadataReviewReason);
        Assert.Equal(new[] { (Approved: 1, NotApproved: 1) }, rename.Calls);
        Assert.NotNull(store.GetDryRunApproval());

        // Identify Movies is the explicit request to try again.
        await CreateModel(store, configuration, profile, metadata: metadata).OnPostEnrichAsync();
        Assert.Equal(1, metadata.CallCount);
    }

    [Fact]
    public async Task DryRun_StillLooksUpMoviesNeverLookedUp()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies),
                NotLookedUp(workspace, "First.Movie.2020.mkv"),
                NotLookedUp(workspace, "Second.Movie.2021.mkv")
            },
            workspace.SourceRoot);
        var metadata = new CountingMetadataService();
        var model = CreateModel(store, configuration, profile, metadata: metadata);
        model.DryRun = true;

        await model.OnPostCommitAsync();

        Assert.Equal(2, metadata.CallCount);
    }

    [Fact]
    public async Task Progress_NamesEachStepOfADryRun_SoLookupCountsAreNotMistakenForMoves()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies),
                NotLookedUp(workspace, "First.Movie.2020.mkv")
            },
            workspace.SourceRoot);
        var progress = new ScanProgress();
        var steps = new List<string>();
        var rename = new RecordingRenameService { OnExecute = () => steps.Add(progress.Step) };
        var model = CreateModel(
            store,
            configuration,
            profile,
            metadata: new StepRecordingMetadataService(progress, steps),
            rename: rename,
            plan: new StepRecordingPlanService(progress, steps),
            progress: progress);
        model.DryRun = true;

        await model.OnPostCommitAsync();

        Assert.Equal(
            new[]
            {
                "Step 1 of 3: Looking up 1 movie not looked up yet",
                "Step 2 of 3: Checking duplicates, the destination folder and Plex",
                "Step 3 of 3: Simulating 1 approved move"
            },
            steps);
        Assert.Equal(string.Empty, progress.Step);
    }

    [Fact]
    public async Task Progress_ADryRunWithNothingToLookUp_AndTheLiveCommit_HaveTwoSteps()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var second = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        second.FileName = "Another.Movie.2024.mkv";
        second.OriginalFilePath = Path.Combine(workspace.SourceRoot, second.FileName);
        second.ImdbId = "tt7777777";
        store.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies),
                second
            },
            workspace.SourceRoot);
        var progress = new ScanProgress();
        var steps = new List<string>();
        var metadata = new StepRecordingMetadataService(progress, steps);
        IndexModel Page() => CreateModel(
            store,
            configuration,
            profile,
            metadata: metadata,
            rename: new RecordingRenameService { OnExecute = () => steps.Add(progress.Step) },
            plan: new StepRecordingPlanService(progress, steps),
            progress: progress);

        var dryRun = Page();
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();

        Assert.Equal(
            new[]
            {
                "Step 1 of 2: Checking duplicates, the destination folder and Plex",
                "Step 2 of 2: Simulating 2 approved moves"
            },
            steps);
        Assert.Contains("step = ,", dryRun.OnGetProgress().Value?.ToString());

        steps.Clear();
        var live = Page();
        live.DryRun = false;
        await live.OnPostCommitAsync();

        Assert.Equal(
            new[]
            {
                "Step 1 of 2: Re-checking duplicates, the destination folder and Plex",
                "Step 2 of 2: Moving 2 approved files"
            },
            steps);
        Assert.Equal(string.Empty, progress.Step);
    }

    [Fact]
    public async Task Progress_NamesEachStepOfIdentifyMovies()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie>
            {
                NotLookedUp(workspace, "First.Movie.2020.mkv"),
                NotLookedUp(workspace, "Second.Movie.2021.mkv")
            },
            workspace.SourceRoot);
        var progress = new ScanProgress();
        var steps = new List<string>();
        IndexModel Page() => CreateModel(
            store,
            configuration,
            profile,
            metadata: new StepRecordingMetadataService(progress, steps),
            plan: new StepRecordingPlanService(progress, steps),
            progress: progress);

        await Page().OnPostEnrichAsync();

        Assert.Equal(
            new[]
            {
                "Step 1 of 2: Looking up 2 movies",
                "Step 2 of 2: Checking duplicates, the destination folder and Plex"
            },
            steps);

        // Nothing left to look up: a single part needs no step number.
        steps.Clear();
        await Page().OnPostEnrichAsync();

        Assert.Equal(
            new[] { "Checking duplicates, the destination folder and Plex" },
            steps);
        Assert.Equal(string.Empty, progress.Step);
    }

    /// <summary>
    /// Fake OMDb that identifies each movie and records which step the page
    /// would be showing while lookups run.
    /// </summary>
    private sealed class StepRecordingMetadataService : IMetadataService
    {
        private readonly ScanProgress _progress;
        private readonly List<string> _steps;

        public StepRecordingMetadataService(ScanProgress progress, List<string> steps)
        {
            _progress = progress;
            _steps = steps;
        }

        public Task EnrichAsync(Movie movie)
        {
            if (!_steps.Contains(_progress.Step))
                _steps.Add(_progress.Step);

            movie.MetadataFetched = true;
            movie.MetadataMatchOrigin = MetadataMatchOrigin.ExactTitleYear;
            movie.ImdbId = "tt" + Math.Abs(movie.FileName!.GetHashCode() % 10_000_000).ToString("0000000");
            movie.Year ??= 2020;
            return Task.CompletedTask;
        }
    }

    /// <summary>Records the step shown while the plan is rebuilt.</summary>
    private sealed class StepRecordingPlanService : IMoviePlanService
    {
        private readonly ScanProgress _progress;
        private readonly List<string> _steps;

        public StepRecordingPlanService(ScanProgress progress, List<string> steps)
        {
            _progress = progress;
            _steps = steps;
        }

        public void Rebuild(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            _steps.Add(_progress.Step);

            foreach (var movie in movies)
            {
                movie.TargetPath = Path.Combine(
                    profile.DestinationRoot,
                    movie.FileName ?? "movie.mkv");
                movie.DestinationProfileId = profile.Id;
                movie.DestinationProfileRevision = profile.Revision;
                movie.PlannedLibraryGoal = libraryGoal;
            }
        }
    }
}
