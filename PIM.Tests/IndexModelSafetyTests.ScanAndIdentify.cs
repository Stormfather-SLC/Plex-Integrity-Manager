using Microsoft.AspNetCore.Mvc;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Scan &amp; Identify goes on to identify the movies only after a scan that
/// succeeded; the page reads that from the progress report.
/// </summary>
public sealed partial class IndexModelSafetyTests
{
    [Fact]
    public void Scan_ThatSucceeds_ReportsSuccess_AndSavesTheNewMovieList()
    {
        using var workspace = new TempWorkspace("PIM-Index-Tests");
        Directory.CreateDirectory(workspace.SourceRoot);
        var configuration = CreateConfiguration(workspace, LibraryGoal.OrganizeNewMovies);
        var store = CreateStore(configuration);
        var profile = CreateProfile(workspace);
        var progress = new ScanProgress();
        var file = Path.Combine(workspace.SourceRoot, "Arctic (2018).mp4");
        File.WriteAllText(file, "movie");

        var model = CreateModel(
            store,
            configuration,
            profile,
            progress: progress,
            scanner: new FixedScanner(file));
        Assert.Contains("started = True", Assert.IsType<JsonResult>(model.OnPostRescan()).Value?.ToString());
        Assert.False(progress.LastScanSucceeded);

        Assert.True(SpinWait.SpinUntil(() => !progress.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.True(progress.LastScanSucceeded);
        Assert.Contains("scanSucceeded = True", model.OnGetProgress().Value?.ToString());
        Assert.True(store.TryGetMovies(out var movies));
        Assert.Equal(file, Assert.Single(movies).OriginalFilePath);
    }

    [Fact]
    public void Scan_ThatFails_ReportsFailure_AndKeepsThePreviousScan()
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
            scanner: new FailingScanner());
        Assert.Contains("started = True", Assert.IsType<JsonResult>(model.OnPostRescan()).Value?.ToString());

        Assert.True(SpinWait.SpinUntil(() => !progress.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.False(progress.LastScanSucceeded);
        Assert.Contains("scanSucceeded = False", model.OnGetProgress().Value?.ToString());
        Assert.True(store.TryGetMovies(out var movies));
        Assert.Equal(previous.OriginalFilePath, Assert.Single(movies).OriginalFilePath);
    }

    private sealed class FixedScanner : IFileScanner
    {
        private readonly string[] _files;

        public FixedScanner(params string[] files) => _files = files;

        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null) => _files.ToList();
    }

    private sealed class FailingScanner : IFileScanner
    {
        public List<Movie> Scan(string rootPath, string? excludedRootPath = null) => new();

        public List<string> GetFiles(string rootPath, string? excludedRootPath = null) =>
            throw new IOException("The source folder could not be read.");
    }
}
