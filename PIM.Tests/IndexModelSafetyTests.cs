using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using PIM.Web.Services;
using Xunit;

namespace PIM.Tests;

public sealed class IndexModelSafetyTests
{
    [Fact]
    public async Task LiveCommitWithoutMatchingDryRun_IsRejectedBeforeRenameService()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var rename = new RecordingRenameService();
        var model = CreateModel(
            store,
            configuration,
            profile,
            rename: rename);
        model.DryRun = false;

        await model.OnPostCommitAsync();

        Assert.Equal(0, rename.ExecuteCount);
        Assert.Contains(
            "does not have a matching dry-run approval",
            model.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task ProfileChange_RebuildsTargetWithoutMetadataCall()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var oldProfile = CreateProfile(workspace);
        var currentProfile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, oldProfile, LibraryGoal.OrganizeNewMovies);
        var originalTarget = movie.TargetPath;
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var metadata = new CountingMetadataService();
        var plan = new RecordingPlanService();
        var model = CreateModel(
            store,
            configuration,
            currentProfile,
            metadata: metadata,
            plan: plan);

        await model.OnGetAsync();

        Assert.Equal(1, plan.CallCount);
        Assert.Equal(0, metadata.CallCount);
        Assert.NotEqual(originalTarget, movie.TargetPath);
        Assert.StartsWith(
            currentProfile.DestinationRoot,
            movie.TargetPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkflowChange_RecalculatesPlanWithoutMetadataCall()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.ReorganizationMigration);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var metadata = new CountingMetadataService();
        var plan = new RecordingPlanService();
        var model = CreateModel(
            store,
            configuration,
            profile,
            metadata: metadata,
            plan: plan);

        await model.OnGetAsync();

        Assert.Equal(1, plan.CallCount);
        Assert.Equal(0, metadata.CallCount);
        Assert.Equal(
            LibraryGoal.ReorganizationMigration,
            movie.PlannedLibraryGoal);
    }

    [Fact]
    public async Task MovieList_ShowsErrorsThenNeedsReviewBeforeReadyMovies()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var ready = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        ready.Title = "Aardvark";
        var review = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        review.Title = "Zebra";
        review.RequireReview("Low confidence metadata match");
        var error = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        error.Title = "Middle";
        error.ErrorMessage = "Source file could not be read.";
        store.SaveMovies(new List<Movie> { ready, review, error }, workspace.SourceRoot);
        var model = CreateModel(store, configuration, profile);

        await model.OnGetAsync();

        Assert.Equal(
            new[] { error.Id, review.Id, ready.Id },
            model.Movies.Select(movie => movie.Id));
    }

    [Fact]
    public async Task RecommendedOnlyFilter_NeverHidesNeedsReviewOrErrorDuplicates()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var skippedDuplicate = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        skippedDuplicate.IsDuplicate = true;
        var reviewDuplicate = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        reviewDuplicate.IsDuplicate = true;
        reviewDuplicate.RequireReview("Duplicate needs a human decision");
        var errorDuplicate = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        errorDuplicate.IsDuplicate = true;
        errorDuplicate.ErrorMessage = "Source file could not be read.";
        store.SaveMovies(
            new List<Movie> { skippedDuplicate, reviewDuplicate, errorDuplicate },
            workspace.SourceRoot);
        var model = CreateModel(store, configuration, profile);

        await model.OnGetAsync(showOnlyRecommended: true);

        Assert.DoesNotContain(model.Movies, movie => movie.Id == skippedDuplicate.Id);
        Assert.Contains(model.Movies, movie => movie.Id == reviewDuplicate.Id);
        Assert.Contains(model.Movies, movie => movie.Id == errorDuplicate.Id);
    }

    [Fact]
    public async Task PreviewTree_IsBuiltForReviewItemsEvenWithoutTargetPaths()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var review = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        review.TargetPath = null;
        review.RequireReview("IMDb ID could not be determined");
        store.SaveMovies(new List<Movie> { review }, workspace.SourceRoot);
        var model = CreateModel(store, configuration, profile);

        await model.OnGetAsync();

        Assert.NotNull(model.PreviewTree);
        Assert.Contains(model.PreviewTree!.Children, node => node.Name == "Needs Review");
    }

    [Fact]
    public async Task Restart_ScanAndDryRunPreviewAreShownWithoutRescanOrMetadataCalls()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var profile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        var firstStore = CreateStore(configuration);
        firstStore.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var beforeRestart = CreateModel(firstStore, configuration, profile);
        beforeRestart.DryRun = true;
        await beforeRestart.OnPostCommitAsync();

        var metadata = new CountingMetadataService();
        var afterRestart = CreateModel(
            CreateStore(configuration),
            configuration,
            profile,
            metadata: metadata);
        await afterRestart.OnGetAsync();

        var restored = Assert.Single(afterRestart.Movies);
        Assert.Equal(movie.Id, restored.Id);
        Assert.Equal(movie.TargetPath, restored.TargetPath);
        Assert.NotNull(afterRestart.DryRunPreview);
        Assert.Equal(0, metadata.CallCount);
    }

    [Fact]
    public async Task Restart_LiveCommitAcceptsDryRunApprovedBeforeRestart()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var profile = CreateProfile(workspace);
        var firstStore = CreateStore(configuration);
        firstStore.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies)
            },
            workspace.SourceRoot);
        var dryRunRename = new RecordingRenameService();
        var beforeRestart = CreateModel(
            firstStore,
            configuration,
            profile,
            rename: dryRunRename);
        beforeRestart.DryRun = true;
        await beforeRestart.OnPostCommitAsync();

        var liveRename = new RecordingRenameService();
        var afterRestart = CreateModel(
            CreateStore(configuration),
            configuration,
            profile,
            rename: liveRename);
        afterRestart.DryRun = false;
        await afterRestart.OnPostCommitAsync();

        Assert.Equal(new[] { true }, dryRunRename.DryRunFlags);
        Assert.Equal(new[] { false }, liveRename.DryRunFlags);
    }

    [Fact]
    public async Task Restart_LiveCommitRejectedWhenPlanChangedSinceDryRun()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var profile = CreateProfile(workspace);
        var firstStore = CreateStore(configuration);
        firstStore.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies)
            },
            workspace.SourceRoot);
        var beforeRestart = CreateModel(firstStore, configuration, profile);
        beforeRestart.DryRun = true;
        await beforeRestart.OnPostCommitAsync();

        // The plan rebuilt after restart now requires review (for example, a
        // Plex title that appeared while PIM was stopped).
        var liveRename = new RecordingRenameService();
        var afterRestart = CreateModel(
            CreateStore(configuration),
            configuration,
            profile,
            rename: liveRename,
            plan: new RecordingPlanService(
                movie => movie.RequireReview("Plex library already contains this movie")));
        afterRestart.DryRun = false;
        await afterRestart.OnPostCommitAsync();

        Assert.Equal(0, liveRename.ExecuteCount);
        Assert.Contains(
            "does not have a matching dry-run approval",
            afterRestart.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task Restart_LiveCommitRejectedWhenDryRunApprovalHasExpired()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var profile = CreateProfile(workspace);
        var firstStore = CreateStore(configuration, clock);
        firstStore.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies)
            },
            workspace.SourceRoot);
        var beforeRestart = CreateModel(firstStore, configuration, profile);
        beforeRestart.DryRun = true;
        await beforeRestart.OnPostCommitAsync();

        clock.Advance(DryRunApproval.MaxAge + TimeSpan.FromMinutes(1));
        var liveRename = new RecordingRenameService();
        var afterRestart = CreateModel(
            CreateStore(configuration, clock),
            configuration,
            profile,
            rename: liveRename);
        afterRestart.DryRun = false;
        await afterRestart.OnPostCommitAsync();

        Assert.Equal(0, liveRename.ExecuteCount);
    }

    [Fact]
    public async Task Restart_RealFiles_DryRunMovesNothingAndApprovedLiveCommitMovesAfterRestart()
    {
        using var workspace = new TempWorkspace("PIM-Index-Restart-Files");
        var sourceFile = Path.Combine(
            workspace.SourceRoot,
            "Restart Movie (2024)",
            "Restart.Movie.2024.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        Directory.CreateDirectory(workspace.DestinationRoot);
        File.WriteAllText(sourceFile, "movie");

        var configuration = CreateConfiguration(
            workspace,
            LibraryGoal.OrganizeNewMovies,
            removeEmptySourceFolders: false);
        var profile = new DestinationProfile
        {
            Name = "Restart Profile",
            DestinationRoot = workspace.DestinationRoot
        };
        var movie = new Movie
        {
            Title = "Restart Movie",
            Year = 2024,
            ImdbId = "tt7654321",
            OriginalFilePath = sourceFile,
            FileName = Path.GetFileName(sourceFile),
            DirectoryPath = Path.GetDirectoryName(sourceFile),
            FileSizeBytes = new FileInfo(sourceFile).Length,
            MetadataFetched = true,
            MatchConfidence = 100,
            Status = "IMDb ID Match"
        };

        var firstStore = CreateStore(configuration);
        firstStore.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var (firstRename, firstPlan) = CreateRealServices(configuration);
        var beforeRestart = CreateModel(
            firstStore,
            configuration,
            profile,
            rename: firstRename,
            plan: firstPlan);
        beforeRestart.DryRun = true;
        await beforeRestart.OnPostCommitAsync();

        var plannedTarget = Assert.Single(beforeRestart.DryRunPreview!.Items).TargetPath;
        Assert.True(File.Exists(sourceFile));
        Assert.False(File.Exists(plannedTarget));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));

        var (secondRename, secondPlan) = CreateRealServices(configuration);
        var afterRestart = CreateModel(
            CreateStore(configuration),
            configuration,
            profile,
            rename: secondRename,
            plan: secondPlan);
        afterRestart.DryRun = false;
        await afterRestart.OnPostCommitAsync();

        Assert.StartsWith("Changes Applied", afterRestart.TempData["Message"]?.ToString());
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(plannedTarget));
        Assert.StartsWith(
            workspace.DestinationRoot,
            plannedTarget,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeepDuplicateCopy_InvalidatesAnEarlierDryRunApproval()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var first = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        var second = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        second.FileName = "Handler.Movie.2024.Copy.mkv";
        foreach (var movie in new[] { first, second })
        {
            movie.ApprovedForCommit = false;
            movie.RequireReview("No clear best file for Standard Version");
        }
        store.SaveMovies(new List<Movie> { first, second }, workspace.SourceRoot);
        var dryRun = CreateModel(store, configuration, profile);
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();
        Assert.NotNull(store.GetDryRunApproval());

        var choose = CreateModel(store, configuration, profile);
        choose.OnPostKeepDuplicateCopy(first.Id);

        Assert.Null(store.GetDryRunApproval());
        Assert.True(first.IsManuallyKept);
        Assert.False(second.IsManuallyKept);
        var rename = new RecordingRenameService();
        var live = CreateModel(store, configuration, profile, rename: rename);
        live.DryRun = false;
        await live.OnPostCommitAsync();
        Assert.Equal(0, rename.ExecuteCount);
    }

    [Fact]
    public void KeepDuplicateCopy_ForAMovieWithoutATie_ChangesNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var plan = new RecordingPlanService();
        var model = CreateModel(store, configuration, profile, plan: plan);

        model.OnPostKeepDuplicateCopy(movie.Id);

        Assert.False(movie.IsManuallyKept);
        Assert.Equal(0, plan.CallCount);
        Assert.Contains("Nothing was changed", model.TempData["Message"]?.ToString());
    }

    [Fact]
    public async Task RealFiles_TiedDuplicates_OnlyTheChosenCopyMovesAndTheOtherIsUntouched()
    {
        using var workspace = new TempWorkspace("PIM-Index-Duplicate-Files");
        Directory.CreateDirectory(workspace.DestinationRoot);
        var keepFile = Path.Combine(workspace.SourceRoot, "Copy A", "Tied.Movie.2020.mkv");
        var skipFile = Path.Combine(workspace.SourceRoot, "Copy B", "Tied.Movie.2020.mkv");
        foreach (var file in new[] { keepFile, skipFile })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "movie");
        }

        var configuration = CreateConfiguration(
            workspace,
            LibraryGoal.OrganizeNewMovies,
            removeEmptySourceFolders: false);
        var profile = new DestinationProfile
        {
            Name = "Duplicate Profile",
            DestinationRoot = workspace.DestinationRoot
        };
        var keep = CreateIdentifiedFileMovie(keepFile);
        var skip = CreateIdentifiedFileMovie(skipFile);
        var store = CreateStore(configuration);
        store.SaveMovies(new List<Movie> { keep, skip }, workspace.SourceRoot);

        async Task<IndexModel> CommitAsync(bool dryRun)
        {
            var (rename, plan) = CreateRealServices(configuration);
            var model = CreateModel(store, configuration, profile, rename: rename, plan: plan);
            model.DryRun = dryRun;
            await model.OnPostCommitAsync();
            return model;
        }

        await CommitAsync(dryRun: true);
        Assert.True(keep.HasDuplicateTieReview);
        Assert.True(skip.HasDuplicateTieReview);
        Assert.False(keep.ApprovedForCommit);
        Assert.False(skip.ApprovedForCommit);

        var (chooseRename, choosePlan) = CreateRealServices(configuration);
        CreateModel(store, configuration, profile, rename: chooseRename, plan: choosePlan)
            .OnPostKeepDuplicateCopy(keep.Id);

        var dryRunAfterChoice = await CommitAsync(dryRun: true);
        Assert.StartsWith("Dry Run Complete", dryRunAfterChoice.TempData["Message"]?.ToString());
        Assert.True(keep.ApprovedForCommit);
        Assert.False(keep.NeedsReview);
        Assert.False(skip.ApprovedForCommit);
        Assert.False(skip.NeedsReview);
        Assert.True(File.Exists(keepFile));
        Assert.True(File.Exists(skipFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.DestinationRoot));
        var target = keep.TargetPath!;

        var live = await CommitAsync(dryRun: false);

        Assert.StartsWith("Changes Applied", live.TempData["Message"]?.ToString());
        Assert.False(File.Exists(keepFile));
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(skipFile));
        Assert.Single(Directory.EnumerateFiles(
            workspace.DestinationRoot,
            "*",
            SearchOption.AllDirectories));
    }

    private static Movie CreateIdentifiedFileMovie(string file)
    {
        return new Movie
        {
            Title = "Tied Movie",
            Year = 2020,
            ImdbId = "tt2020202",
            OriginalFilePath = file,
            FileName = Path.GetFileName(file),
            DirectoryPath = Path.GetDirectoryName(file),
            FileSizeBytes = new FileInfo(file).Length,
            MetadataFetched = true,
            MetadataMatchedByImdbId = true,
            MatchConfidence = 100,
            Status = "IMDb ID Match"
        };
    }

    private static (IRenameService Rename, IMoviePlanService Plan) CreateRealServices(
        IConfiguration configuration)
    {
        var progress = new ScanProgress();
        var destination = new DestinationConflictService(progress);
        var rename = new RenameService(
            destination,
            new DestinationPathBuilder(),
            configuration,
            progress,
            new SourceCleanupStatus());
        var conflicts = new MovieConflictDetectionService(
            destination,
            new NoPlexConflictService());
        var plan = new MoviePlanService(
            new DuplicateService(),
            rename,
            conflicts);

        return (rename, plan);
    }

    private static IConfiguration CreateConfiguration(
        TempWorkspace workspace,
        LibraryGoal workflow,
        bool removeEmptySourceFolders = true)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:ScanPath"] = workspace.SourceRoot,
                ["PIM:LibraryGoal"] = workflow.ToString(),
                ["PIM:MetadataDelayMs"] = "0",
                ["PIM:RemoveEmptySourceFolders"] = removeEmptySourceFolders.ToString(),
                ["PIM:WorkflowStateDirectory"] = workspace.StateDirectory
            })
            .Build();
    }

    private static JsonWorkflowStateStore CreateStore(
        IConfiguration configuration,
        TimeProvider? clock = null)
    {
        return new JsonWorkflowStateStore(
            configuration,
            NullLogger<JsonWorkflowStateStore>.Instance,
            clock ?? TimeProvider.System);
    }

    private static IndexModel CreateModel(
        IWorkflowStateStore store,
        IConfiguration configuration,
        DestinationProfile profile,
        CountingMetadataService? metadata = null,
        IRenameService? rename = null,
        IMoviePlanService? plan = null)
    {
        metadata ??= new CountingMetadataService();
        rename ??= new RecordingRenameService();
        plan ??= new RecordingPlanService();
        var model = new IndexModel(
            new EmptyScanner(),
            new NoOpParser(),
            metadata,
            rename,
            configuration,
            store,
            new ScanProgress(),
            new PreviewTreeService(),
            new DryRunPreviewService(),
            new FixedProfileStore(profile),
            plan,
            new NoOpSuggestionService(),
            new DuplicateService(),
            NullLogger<IndexModel>.Instance);
        var httpContext = new DefaultHttpContext();
        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };
        model.TempData = new TempDataDictionary(
            httpContext,
            new InMemoryTempDataProvider());

        return model;
    }

    private static DestinationProfile CreateProfile(TempWorkspace workspace)
    {
        return new DestinationProfile
        {
            Name = "Test Profile",
            DestinationRoot = Path.Combine(
                workspace.Root,
                "Destination-" + Guid.NewGuid().ToString("N"))
        };
    }

    private static Movie CreateMovie(
        TempWorkspace workspace,
        DestinationProfile profile,
        LibraryGoal plannedWorkflow)
    {
        return new Movie
        {
            Title = "Handler Movie",
            Year = 2024,
            ImdbId = "tt1234567",
            OriginalFilePath = Path.Combine(
                workspace.SourceRoot,
                "Handler.Movie.2024.mkv"),
            FileName = "Handler.Movie.2024.mkv",
            MetadataFetched = true,
            MatchConfidence = 100,
            TargetPath = Path.Combine(
                profile.DestinationRoot,
                "old-target.mkv"),
            DestinationProfileId = profile.Id,
            DestinationProfileRevision = profile.Revision,
            PlannedLibraryGoal = plannedWorkflow,
            ApprovedForCommit = true
        };
    }

    private sealed class CountingMetadataService : IMetadataService
    {
        public int CallCount { get; private set; }

        public Task EnrichAsync(Movie movie)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlanService : IMoviePlanService
    {
        private readonly Action<Movie>? _afterRebuild;

        public RecordingPlanService(Action<Movie>? afterRebuild = null)
        {
            _afterRebuild = afterRebuild;
        }

        public int CallCount { get; private set; }

        public void Rebuild(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            CallCount++;

            foreach (var movie in movies)
            {
                movie.TargetPath = Path.Combine(
                    profile.DestinationRoot,
                    movie.FileName ?? "movie.mkv");
                movie.DestinationProfileId = profile.Id;
                movie.DestinationProfileRevision = profile.Revision;
                movie.PlannedLibraryGoal = libraryGoal;
                _afterRebuild?.Invoke(movie);
            }
        }
    }

    private sealed class RecordingRenameService : IRenameService
    {
        public List<bool> DryRunFlags { get; } = new();

        public int ExecuteCount => DryRunFlags.Count;

        public string? LastJournalLocation => null;

        public void GeneratePreview(
            List<Movie> movies,
            DestinationProfile profile,
            string sourceRoot)
        {
        }

        public void ExecuteChanges(
            List<Movie> movies,
            bool dryRun,
            string destinationRoot)
        {
            DryRunFlags.Add(dryRun);
        }
    }

    private sealed class NoPlexConflictService : IPlexLibraryConflictService
    {
        public PlexLibraryConflictResult Check(Movie movie) =>
            PlexLibraryConflictResult.NoConflict();
    }

    private sealed class FixedProfileStore : IDestinationProfileStore
    {
        private readonly DestinationProfile _profile;

        public FixedProfileStore(DestinationProfile profile)
        {
            _profile = profile;
        }

        public IReadOnlyList<DestinationProfile> GetProfiles() => new[] { _profile };

        public DestinationProfile GetActiveProfile() => _profile;

        public DestinationProfile? GetProfile(Guid profileId) =>
            profileId == _profile.Id ? _profile : null;

        public DestinationProfile Save(DestinationProfile profile) => profile;

        public bool Delete(Guid profileId) => false;

        public bool SetActive(Guid profileId) => profileId == _profile.Id;
    }

    private sealed class EmptyScanner : IFileScanner
    {
        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null) => new();
    }

    private sealed class NoOpParser : IFileNameParser
    {
        public void Parse(Movie movie)
        {
        }
    }

    private sealed class NoOpSuggestionService : IMetadataSuggestionService
    {
        public Task<bool> AcceptAsync(
            Movie movie,
            List<Movie> allMovies,
            DestinationProfile profile,
            string sourceRoot,
            LibraryGoal libraryGoal)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = new();

        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>(_values);

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
            _values = new Dictionary<string, object>(values);
        }
    }
}
