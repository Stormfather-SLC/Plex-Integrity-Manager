using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using PIM.Web.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// PIM does one thing at a time: while one action is running, every other
/// action is refused and changes nothing.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    private const string RunningAction = "Identify Movies";

    [Fact]
    public void Gate_AdmitsOneActionAtATime()
    {
        var progress = new ScanProgress();
        Assert.False(progress.IsBusy);
        Assert.Null(progress.BusyWith);

        Assert.True(progress.TryBeginAction("a dry run"));
        Assert.True(progress.IsBusy);
        Assert.Equal("a dry run", progress.BusyWith);

        Assert.False(progress.TryBeginAction("a live commit"));
        Assert.Equal("a dry run", progress.BusyWith);

        progress.EndAction();
        Assert.False(progress.IsBusy);
        Assert.Null(progress.BusyWith);
        Assert.True(progress.TryBeginAction("a live commit"));
    }

    [Fact]
    public async Task Busy_DryRunAndLiveCommitAreRefused_AndNothingIsExecuted()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie> { CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies) },
            workspace.SourceRoot);
        var approvingDryRun = CreateModel(store, configuration, profile);
        approvingDryRun.DryRun = true;
        await approvingDryRun.OnPostCommitAsync();
        var approval = store.GetDryRunApproval();
        Assert.NotNull(approval);

        var progress = new ScanProgress();
        Assert.True(progress.TryBeginAction(RunningAction));
        var rename = new RecordingRenameService();
        var plan = new RecordingPlanService();

        var live = CreateModel(store, configuration, profile, rename: rename, plan: plan, progress: progress);
        live.DryRun = false;
        await live.OnPostCommitAsync();

        var dryRun = CreateModel(store, configuration, profile, rename: rename, plan: plan, progress: progress);
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();

        Assert.Equal(0, rename.ExecuteCount);
        Assert.Equal(0, plan.CallCount);
        Assert.Contains("PIM is busy with Identify Movies", live.TempData["Message"]?.ToString());
        Assert.Contains("Nothing was changed", dryRun.TempData["Message"]?.ToString());
        Assert.Equal(approval, store.GetDryRunApproval());

        // A refused action must not release the running action's hold.
        Assert.Equal(RunningAction, progress.BusyWith);

        // Once the running action ends, the approved live commit goes ahead.
        progress.EndAction();
        var afterwards = CreateModel(store, configuration, profile, rename: rename, progress: progress);
        afterwards.DryRun = false;
        await afterwards.OnPostCommitAsync();
        Assert.Equal(new[] { false }, rename.DryRunFlags);
        Assert.False(progress.IsBusy);
    }

    [Fact]
    public async Task Busy_ReviewDecisionsAreRefused_AndKeepTheDryRunApproval()
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
        var approvingDryRun = CreateModel(store, configuration, profile);
        approvingDryRun.DryRun = true;
        await approvingDryRun.OnPostCommitAsync();
        var approval = store.GetDryRunApproval();
        Assert.NotNull(approval);

        var progress = new ScanProgress();
        Assert.True(progress.TryBeginAction(RunningAction));
        var plan = new RecordingPlanService();
        var suggestion = new NoOpSuggestionService();
        IndexModel Page() => CreateModel(
            store,
            configuration,
            profile,
            plan: plan,
            suggestion: suggestion,
            progress: progress);

        var refused = new List<IndexModel>();
        IndexModel Refused()
        {
            var page = Page();
            refused.Add(page);
            return page;
        }

        Refused().OnPostKeepDuplicateCopy(first.Id);
        Refused().OnPostConfirmFileName(first.Id);
        Refused().OnPostAddPlexDuplicate(first.Id);
        Refused().OnPostUndoPlexDuplicate(first.Id);
        await Refused().OnPostSetImdbIdAsync(first.Id, "tt7654321");
        await Refused().OnPostAcceptSuggestedMatchAsync(first.Id);
        await Refused().OnPostKeepFileIdentityAsync(first.Id);
        await Refused().OnPostUndoKeepFileIdentityAsync(first.Id);

        Assert.All(
            refused,
            page => Assert.Contains("PIM is busy with Identify Movies", page.TempData["Message"]?.ToString()));
        Assert.Equal(approval, store.GetDryRunApproval());
        Assert.Equal(0, plan.CallCount);
        Assert.Empty(suggestion.AppliedImdbIds);
        Assert.Empty(suggestion.KeptFileIdentities);
        Assert.Empty(suggestion.UndoneFileIdentities);
        Assert.False(first.IsManuallyKept);
        Assert.Equal("tt1234567", first.ImdbId);
        Assert.Equal(RunningAction, progress.BusyWith);

        // The same decision is accepted once the running action ends.
        progress.EndAction();
        Page().OnPostKeepDuplicateCopy(first.Id);
        Assert.True(first.IsManuallyKept);
        Assert.Null(store.GetDryRunApproval());
        Assert.False(progress.IsBusy);
    }

    [Fact]
    public async Task Busy_IdentifyScanAndSaveSettingsAreRefused_AndChangeNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var settingsDirectory = Path.Combine(workspace.Root, "Settings");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        configuration[UserSettingsLocation.ConfigurationKey] = settingsDirectory;
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var originalDestination = profile.DestinationRoot;
        var notLookedUp = NotLookedUp(workspace, "First.Movie.2020.mkv");
        store.SaveMovies(
            new List<Movie>
            {
                CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies),
                notLookedUp
            },
            workspace.SourceRoot);
        var approvingDryRun = CreateModel(store, configuration, profile);
        approvingDryRun.DryRun = true;
        await approvingDryRun.OnPostCommitAsync();
        var approval = store.GetDryRunApproval();
        Assert.NotNull(approval);
        notLookedUp.MetadataFetched = false;

        var progress = new ScanProgress();
        Assert.True(progress.TryBeginAction(RunningAction));
        var metadata = new CountingMetadataService();
        var plan = new RecordingPlanService();
        IndexModel Page() => CreateModel(
            store,
            configuration,
            profile,
            metadata: metadata,
            plan: plan,
            progress: progress);

        var identify = Page();
        await identify.OnPostEnrichAsync();

        var scan = Assert.IsType<JsonResult>(Page().OnPostRescan());

        var settings = Page();
        settings.ScanPath = workspace.SourceRoot;
        settings.OutputPath = Path.Combine(workspace.Root, "Somewhere Else");
        settings.LibraryGoal = LibraryGoal.Consolidation;
        settings.OnPostSaveSettings();

        Assert.Equal(0, metadata.CallCount);
        Assert.Equal(0, plan.CallCount);
        Assert.Contains("PIM is busy with Identify Movies", identify.TempData["Message"]?.ToString());
        Assert.Contains("started = False", scan.Value?.ToString());
        Assert.Contains("PIM is busy with Identify Movies", scan.Value?.ToString());
        Assert.Contains("PIM is busy with Identify Movies", settings.TempData["Message"]?.ToString());
        Assert.False(Directory.Exists(settingsDirectory));
        Assert.Equal(originalDestination, profile.DestinationRoot);
        Assert.Equal(LibraryGoal.OrganizeNewMovies.ToString(), configuration["PIM:LibraryGoal"]);

        // The scan and its dry-run approval are untouched.
        Assert.True(store.TryGetMovies(out var movies));
        Assert.Equal(2, movies.Count);
        Assert.Equal(approval, store.GetDryRunApproval());
        Assert.Equal(RunningAction, progress.BusyWith);
    }

    [Fact]
    public async Task Busy_ShowingThePageDoesNotRebuildTheListBeingWorkedOn()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var oldProfile = CreateProfile(workspace);
        var currentProfile = CreateProfile(workspace);
        var movie = CreateMovie(workspace, oldProfile, LibraryGoal.OrganizeNewMovies);
        var originalTarget = movie.TargetPath;
        store.SaveMovies(new List<Movie> { movie }, workspace.SourceRoot);
        var progress = new ScanProgress();
        Assert.True(progress.TryBeginAction(RunningAction));
        var plan = new RecordingPlanService();
        var busyPage = CreateModel(store, configuration, currentProfile, plan: plan, progress: progress);

        await busyPage.OnGetAsync();

        // The page reports the running action so it can start out locked.
        Assert.Equal(RunningAction, busyPage.BusyWith);
        Assert.Contains("busy = True", busyPage.OnGetProgress().Value?.ToString());
        Assert.Equal(0, plan.CallCount);
        Assert.Equal(originalTarget, movie.TargetPath);
        Assert.False(busyPage.LiveCommit.Enabled);
        Assert.Equal(RunningAction, progress.BusyWith);

        // The stale plan is refreshed as usual once PIM is idle again.
        progress.EndAction();
        var idlePage = CreateModel(store, configuration, currentProfile, plan: plan, progress: progress);
        await idlePage.OnGetAsync();
        Assert.Null(idlePage.BusyWith);
        Assert.Contains("busy = False", idlePage.OnGetProgress().Value?.ToString());
        Assert.Equal(1, plan.CallCount);
        Assert.False(progress.IsBusy);
    }

    [Fact]
    public void Busy_CancelIsStillAccepted()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var progress = new ScanProgress();
        Assert.True(progress.TryBeginAction(RunningAction));
        var cancellation = progress.BeginCancellableOperation();
        var model = CreateModel(
            CreateStore(configuration),
            configuration,
            CreateProfile(workspace),
            progress: progress);

        var result = model.OnPostCancel();

        Assert.Contains("requested = True", result.Value?.ToString());
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ActionsReleaseTheGate_WhenTheyFinishAndWhenTheyFail()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie> { CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies) },
            workspace.SourceRoot);
        var progress = new ScanProgress();

        var failing = CreateModel(
            store,
            configuration,
            profile,
            plan: new RecordingPlanService(_ => throw new InvalidOperationException("Planning failed.")),
            progress: progress);
        failing.DryRun = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.OnPostCommitAsync());
        Assert.False(progress.IsBusy);

        var failingIdentify = CreateModel(
            store,
            configuration,
            profile,
            plan: new RecordingPlanService(_ => throw new InvalidOperationException("Planning failed.")),
            progress: progress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingIdentify.OnPostEnrichAsync());
        Assert.False(progress.IsBusy);

        var working = CreateModel(store, configuration, profile, progress: progress);
        working.DryRun = true;
        await working.OnPostCommitAsync();
        Assert.False(progress.IsBusy);
        Assert.NotNull(store.GetDryRunApproval());
    }

    [Fact]
    public async Task Scan_HoldsTheGateUntilTheBackgroundScanEnds()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        Directory.CreateDirectory(workspace.SourceRoot);
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var progress = new ScanProgress();
        using var scanner = new BlockingScanner();
        var rename = new RecordingRenameService();

        var started = Assert.IsType<JsonResult>(
            CreateModel(store, configuration, profile, progress: progress, scanner: scanner).OnPostRescan());
        Assert.Contains("started = True", started.Value?.ToString());
        Assert.True(scanner.Entered.Wait(TimeSpan.FromSeconds(10)));

        // The handler has returned, but the scan is still running.
        Assert.Equal("Scan Movies", progress.BusyWith);
        var dryRun = CreateModel(store, configuration, profile, rename: rename, progress: progress);
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();
        Assert.Equal(0, rename.ExecuteCount);
        Assert.Contains("PIM is busy with Scan Movies", dryRun.TempData["Message"]?.ToString());
        var secondScan = Assert.IsType<JsonResult>(
            CreateModel(store, configuration, profile, progress: progress, scanner: scanner).OnPostRescan());
        Assert.Contains("started = False", secondScan.Value?.ToString());
        Assert.Equal("Scan Movies", progress.BusyWith);

        scanner.Release.Set();
        Assert.True(SpinWait.SpinUntil(() => !progress.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.False(progress.IsRunning);
    }

    [Fact]
    public void Scan_ThatCannotStart_ReleasesTheGate()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        configuration["PIM:ScanPath"] = string.Empty;
        var progress = new ScanProgress();
        var model = CreateModel(
            CreateStore(configuration),
            configuration,
            CreateProfile(workspace),
            progress: progress);

        var result = Assert.IsType<JsonResult>(model.OnPostRescan());

        Assert.Contains("started = False", result.Value?.ToString());
        Assert.Contains("source folder must be configured", result.Value?.ToString());
        Assert.False(progress.IsBusy);
    }

    [Fact]
    public void Busy_DestinationProfileChangesAreRefused()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var progress = new ScanProgress();
        var model = new DestinationProfilesModel(
            new FixedProfileStore(CreateProfile(workspace)),
            new DestinationPathBuilder(),
            configuration,
            CreateStore(configuration),
            progress);
        var idlePost = ProfilePageRequest(model, HttpMethods.Post);
        model.OnPageHandlerExecuting(idlePost);
        Assert.Null(idlePost.Result);

        Assert.True(progress.TryBeginAction(RunningAction));
        var busyGet = ProfilePageRequest(model, HttpMethods.Get);
        model.OnPageHandlerExecuting(busyGet);
        var busyPost = ProfilePageRequest(model, HttpMethods.Post);
        model.OnPageHandlerExecuting(busyPost);

        Assert.Null(busyGet.Result);
        Assert.IsType<RedirectToPageResult>(busyPost.Result);
        Assert.Contains(
            "PIM is busy with Identify Movies",
            model.TempData["ProfileMessage"]?.ToString());
        Assert.Equal(RunningAction, progress.BusyWith);
    }

    private static PageHandlerExecutingContext ProfilePageRequest(
        DestinationProfilesModel model,
        string method)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        var pageContext = new PageContext(new ActionContext(
            httpContext,
            new RouteData(),
            new CompiledPageActionDescriptor()));
        model.PageContext = pageContext;
        model.TempData = new TempDataDictionary(
            httpContext,
            new InMemoryTempDataProvider());

        return new PageHandlerExecutingContext(
            pageContext,
            new List<IFilterMetadata>(),
            new HandlerMethodDescriptor(),
            new Dictionary<string, object?>(),
            model);
    }

    /// <summary>A scan that waits inside the background task until released.</summary>
    private sealed class BlockingScanner : IFileScanner, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return new List<string>();
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
