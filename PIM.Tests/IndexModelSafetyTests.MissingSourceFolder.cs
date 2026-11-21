using Microsoft.AspNetCore.Mvc;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.FileSystem;
using PIM.Infrastructure.Parsing;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// A scan of a source folder that is missing or unreachable (renamed, or on a
/// disconnected drive or network share) fails closed: it must not scan as 0
/// movies and replace the saved scan, its identifications, and review choices.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    [Fact]
    public async Task Scan_OfAMissingSourceFolder_IsRefused_AndChangesNothing()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        // The source folder is deliberately never created.
        Assert.False(Directory.Exists(workspace.SourceRoot));
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var previous = CreateMovie(workspace, profile, LibraryGoal.OrganizeNewMovies);
        store.SaveMovies(new List<Movie> { previous }, workspace.SourceRoot);
        var dryRun = CreateModel(store, configuration, profile);
        dryRun.DryRun = true;
        await dryRun.OnPostCommitAsync();
        var approval = store.GetDryRunApproval();
        Assert.NotNull(approval);
        var progress = new ScanProgress { LastScanSucceeded = true };
        var scanner = new CountingScanner();

        var model = CreateModel(store, configuration, profile, progress: progress, scanner: scanner);
        var result = Assert.IsType<JsonResult>(model.OnPostRescan()).Value?.ToString();

        Assert.Contains("started = False", result);
        Assert.Contains("can't be found or reached", result);
        Assert.Contains("Nothing was changed", result);
        Assert.Equal(0, scanner.CallCount);
        Assert.False(progress.LastScanSucceeded);
        Assert.Contains("scanSucceeded = False", model.OnGetProgress().Value?.ToString());
        Assert.False(progress.IsBusy);
        Assert.False(progress.IsRunning);
        Assert.Equal(approval, store.GetDryRunApproval());
        Assert.True(store.TryGetMovies(out var movies));
        var kept = Assert.Single(movies);
        Assert.Equal(previous.OriginalFilePath, kept.OriginalFilePath);
        Assert.Equal("tt1234567", kept.ImdbId);
        Assert.False(Directory.Exists(workspace.SourceRoot));
    }

    [Fact]
    public void Scan_WhenTheSourceFolderBecomesUnreachable_ReportsIt_AndKeepsThePreviousScan()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        Directory.CreateDirectory(workspace.SourceRoot);
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var previous = new Movie { OriginalFilePath = Path.Combine(workspace.SourceRoot, "Earlier.mp4") };
        store.SaveMovies(new List<Movie> { previous }, workspace.SourceRoot);
        var progress = new ScanProgress { LastScanSucceeded = true };

        var model = CreateModel(
            store,
            configuration,
            profile,
            progress: progress,
            scanner: new UnreachableScanner());
        Assert.Contains("started = True", Assert.IsType<JsonResult>(model.OnPostRescan()).Value?.ToString());

        Assert.True(SpinWait.SpinUntil(() => !progress.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.False(progress.LastScanSucceeded);
        var report = model.OnGetProgress().Value?.ToString();
        Assert.Contains("scanSucceeded = False", report);
        Assert.Contains("can't be found or reached", report);
        Assert.True(store.TryGetMovies(out var movies));
        Assert.Equal(previous.OriginalFilePath, Assert.Single(movies).OriginalFilePath);
    }

    [Fact]
    public void Scan_OfAMissingSourceFolder_WithTheRealScanner_KeepsThePreviousScan()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var previous = new Movie { OriginalFilePath = Path.Combine(workspace.SourceRoot, "Earlier.mp4") };
        store.SaveMovies(new List<Movie> { previous }, workspace.SourceRoot);
        var progress = new ScanProgress();

        var result = CreateModel(
            store,
            configuration,
            profile,
            progress: progress,
            scanner: new FileScanner(new FileNameParser())).OnPostRescan();

        Assert.Contains("started = False", Assert.IsType<JsonResult>(result).Value?.ToString());
        Assert.False(progress.LastScanSucceeded);
        Assert.True(store.TryGetMovies(out var movies));
        Assert.Equal(previous.OriginalFilePath, Assert.Single(movies).OriginalFilePath);
    }

    [Fact]
    public void Scan_OfAnExistingEmptyFolder_SucceedsWithNoMovies()
    {
        // An empty folder that is really there (for example, after every movie
        // was moved) is a genuine empty scan, not a failure.
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        Directory.CreateDirectory(workspace.SourceRoot);
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        store.SaveMovies(
            new List<Movie> { new() { OriginalFilePath = Path.Combine(workspace.SourceRoot, "Moved.mp4") } },
            workspace.SourceRoot);
        var progress = new ScanProgress();

        var model = CreateModel(
            store,
            configuration,
            profile,
            progress: progress,
            scanner: new FileScanner(new FileNameParser()));
        Assert.Contains("started = True", Assert.IsType<JsonResult>(model.OnPostRescan()).Value?.ToString());

        Assert.True(SpinWait.SpinUntil(() => !progress.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.True(progress.LastScanSucceeded);
        Assert.True(store.TryGetMovies(out var movies));
        Assert.Empty(movies);
    }

    private sealed class CountingScanner : IFileScanner
    {
        public int CallCount { get; private set; }

        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null)
        {
            CallCount++;
            return new List<string>();
        }
    }

    private sealed class UnreachableScanner : IFileScanner
    {
        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null) =>
            throw new DirectoryNotFoundException("The source folder stopped being reachable during the scan.");
    }
}
