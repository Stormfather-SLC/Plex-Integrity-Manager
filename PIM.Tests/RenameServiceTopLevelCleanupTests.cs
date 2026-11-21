using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// Source cleanup for folders directly inside the scan folder: a movie's own
/// folder is removed once it is empty, while organizing folders (Amazon, PG)
/// are kept, and every kept folder is journaled with the reason.
/// </summary>
public sealed class RenameServiceTopLevelCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PIM-TopLevel-Cleanup-Tests",
        Guid.NewGuid().ToString("N"));

    private string SourceRoot => Path.Combine(_root, "Source");
    private string DestinationRoot => Path.Combine(_root, "Destination");
    private string JournalDirectory => Path.Combine(_root, "Journal");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DryRun_MovesNothing_AndRemovesNoFolder()
    {
        var movie = CreateMovie("Better Off Dead (1985) {imdb-tt0088794}", "Better Off Dead", 1985, "tt0088794");
        var folder = Path.GetDirectoryName(movie.OriginalFilePath)!;

        CreateService().ExecuteChanges(new List<Movie> { movie }, dryRun: true, DestinationRoot);

        Assert.True(File.Exists(movie.OriginalFilePath));
        Assert.True(Directory.Exists(folder));
        Assert.False(File.Exists(movie.TargetPath!));
        Assert.DoesNotContain(ReadJournal(), entry => Event(entry) is "SourceFolderRemoved" or "SourceFolderKept");
    }

    [Fact]
    public void LiveCommit_RemovesEmptiedMovieFolder_DirectlyInsideScanFolder()
    {
        var movie = CreateMovie("Better Off Dead (1985) {imdb-tt0088794}", "Better Off Dead", 1985, "tt0088794");
        var folder = Path.GetDirectoryName(movie.OriginalFilePath)!;

        CreateService().ExecuteChanges(new List<Movie> { movie }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(movie.TargetPath!));
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(SourceRoot));
        Assert.Contains(ReadJournal(), entry =>
            Event(entry) == "SourceFolderRemoved" && SourcePathOf(entry) == folder);
    }

    [Theory]
    [InlineData("Awakenings (1990)", "Awakenings", 1991, "tt0099077")]
    [InlineData("Awakenings tt0099077", "Awakenings", 1991, "tt0099077")]
    public void LiveCommit_RemovesMovieFolder_NamedByYearOneOffOrImdbId(
        string folderName,
        string title,
        int year,
        string imdbId)
    {
        var movie = CreateMovie(folderName, title, year, imdbId);
        var folder = Path.GetDirectoryName(movie.OriginalFilePath)!;

        CreateService().ExecuteChanges(new List<Movie> { movie }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(movie.TargetPath!));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void LiveCommit_KeepsOrganizingFolder_ThatHeldSeveralMovies_EvenWhenEmptied()
    {
        var first = CreateMovie("Amazon", "Masters of the Universe", 1987, "tt0093507");
        var second = CreateMovie("Amazon", "RAD", 1986, "tt0091817");
        var amazon = Path.Combine(SourceRoot, "Amazon");

        CreateService().ExecuteChanges(new List<Movie> { first, second }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(first.TargetPath!));
        Assert.True(File.Exists(second.TargetPath!));
        Assert.True(Directory.Exists(amazon));
        Assert.Empty(Directory.EnumerateFileSystemEntries(amazon));
        Assert.Contains(ReadJournal(), entry =>
            Event(entry) == "SourceFolderKept" &&
            SourcePathOf(entry) == amazon &&
            Detail(entry).Contains("directly inside the scan folder"));
    }

    [Fact]
    public void LiveCommit_KeepsFolder_WhoseOnlyMovieWasLooseButNotNamedForIt()
    {
        var movie = CreateMovie("Amazon", "Son-In-Law", 1993, "tt0108186");
        var amazon = Path.Combine(SourceRoot, "Amazon");

        CreateService().ExecuteChanges(new List<Movie> { movie }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(movie.TargetPath!));
        Assert.True(Directory.Exists(amazon));
    }

    [Fact]
    public void LiveCommit_KeepsMovieFolder_WhenANotApprovedMovieWasScannedInsideIt()
    {
        var moved = CreateMovie("Back to the Future (1985)", "Back to the Future", 1985, "tt0088763");
        var folder = Path.GetDirectoryName(moved.OriginalFilePath)!;
        var notApproved = CreateMovie(
            Path.Combine("Back to the Future (1985)", "Part II"),
            "Back to the Future Part II",
            1989,
            "tt0096874");
        notApproved.ApprovedForCommit = false;
        notApproved.NeedsReview = true;

        CreateService().ExecuteChanges(
            new List<Movie> { moved },
            dryRun: false,
            DestinationRoot,
            new List<Movie> { notApproved });

        Assert.True(File.Exists(moved.TargetPath!));
        Assert.True(File.Exists(notApproved.OriginalFilePath));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void LiveCommit_KeepsTopLevelFolder_HoldingTwoMovedMovies_ButRemovesTheEmptiedSubfolder()
    {
        var first = CreateMovie("Back to the Future (1985)", "Back to the Future", 1985, "tt0088763");
        var second = CreateMovie(
            Path.Combine("Back to the Future (1985)", "Part II"),
            "Back to the Future Part II",
            1989,
            "tt0096874");
        var topLevel = Path.GetDirectoryName(first.OriginalFilePath)!;
        var subfolder = Path.GetDirectoryName(second.OriginalFilePath)!;

        CreateService().ExecuteChanges(new List<Movie> { first, second }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(first.TargetPath!));
        Assert.True(File.Exists(second.TargetPath!));
        Assert.False(Directory.Exists(subfolder));
        Assert.True(Directory.Exists(topLevel));
    }

    [Fact]
    public void LiveCommit_KeepsMovieFolder_WithLeftoverFile_AndJournalsWhy()
    {
        var movie = CreateMovie("Better Off Dead (1985) {imdb-tt0088794}", "Better Off Dead", 1985, "tt0088794");
        var folder = Path.GetDirectoryName(movie.OriginalFilePath)!;
        var leftover = Path.Combine(folder, "Better Off Dead.nfo");
        File.WriteAllText(leftover, "info");

        CreateService().ExecuteChanges(new List<Movie> { movie }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(movie.TargetPath!));
        Assert.True(File.Exists(leftover));
        Assert.Contains(ReadJournal(), entry =>
            Event(entry) == "SourceFolderKept" &&
            SourcePathOf(entry) == folder &&
            Detail(entry) == "Kept: it still contains 1 file or folder, such as 'Better Off Dead.nfo'.");
    }

    [Fact]
    public void LiveCommit_WithCleanupOff_RemovesNoFolder()
    {
        var movie = CreateMovie("Better Off Dead (1985) {imdb-tt0088794}", "Better Off Dead", 1985, "tt0088794");
        var folder = Path.GetDirectoryName(movie.OriginalFilePath)!;

        CreateService(removeEmptySourceFolders: false)
            .ExecuteChanges(new List<Movie> { movie }, dryRun: false, DestinationRoot);

        Assert.True(File.Exists(movie.TargetPath!));
        Assert.True(Directory.Exists(folder));
    }

    private static string? Event(JsonElement entry) => entry.GetProperty("Event").GetString();

    private static string? SourcePathOf(JsonElement entry) =>
        entry.TryGetProperty("SourcePath", out var path) ? path.GetString() : null;

    private static string Detail(JsonElement entry) =>
        entry.TryGetProperty("Detail", out var detail) ? detail.GetString() ?? string.Empty : string.Empty;

    private List<JsonElement> ReadJournal()
    {
        var file = Assert.Single(Directory.GetFiles(JournalDirectory));

        return File.ReadAllLines(file)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private RenameService CreateService(bool removeEmptySourceFolders = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:ScanPath"] = SourceRoot,
                ["PIM:RemoveEmptySourceFolders"] = removeEmptySourceFolders.ToString(),
                ["PIM:JournalDirectory"] = JournalDirectory
            })
            .Build();

        return new RenameService(
            new NoConflictDestinationService(),
            new DestinationPathBuilder(),
            configuration,
            new ScanProgress(),
            new SourceCleanupStatus(),
            new JsonLinesOperationJournal(configuration));
    }

    /// <summary>A movie file at <c>Source\{folder}\{title}.mp4</c>, approved for a move.</summary>
    private Movie CreateMovie(string folder, string title, int year, string imdbId)
    {
        var sourceFolder = Path.Combine(SourceRoot, folder);
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(DestinationRoot);

        var sourceFile = Path.Combine(sourceFolder, $"{title}.mp4");
        File.WriteAllText(sourceFile, "movie");

        var name = $"{title} ({year}) {{imdb-{imdbId}}}";

        return new Movie
        {
            Title = title,
            Year = year,
            ImdbId = imdbId,
            OriginalFilePath = sourceFile,
            FileName = Path.GetFileName(sourceFile),
            DirectoryPath = sourceFolder,
            FileSizeBytes = new FileInfo(sourceFile).Length,
            TargetPath = Path.Combine(DestinationRoot, name, $"{name}.mp4"),
            ApprovedForCommit = true,
            Status = "Rename preview generated"
        };
    }

    private sealed class NoConflictDestinationService : IDestinationConflictService
    {
        public void InvalidateCache()
        {
        }

        public void RecordDestinationEntry(string path)
        {
        }

        public DestinationConflictResult Check(Movie movie, string outputPath, bool refresh = false) =>
            DestinationConflictResult.NoConflict();
    }
}
