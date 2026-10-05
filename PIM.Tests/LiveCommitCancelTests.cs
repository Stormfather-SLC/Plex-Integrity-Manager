using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// The owner can stop a live commit. The request is honoured only between
/// files: a file is either moved completely or not started, and everything
/// not reached stays exactly where it is. Uses real files in an isolated
/// temporary folder.
/// </summary>
public sealed class LiveCommitCancelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PIM-Cancel-Commit-Tests",
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
    public void StopDuringALiveCommit_FinishesTheCurrentFile_AndLeavesTheRestUntouched()
    {
        var first = CreateMovie("First Movie", 2001, "tt0000001");
        var second = CreateMovie("Second Movie", 2002, "tt0000002");
        var third = CreateMovie("Third Movie", 2003, "tt0000003");
        using var stop = new CancellationTokenSource();

        // The owner presses Cancel while the first file is being moved.
        var service = CreateService(stopAfterMoves: 1, stop.Cancel);

        var outcome = service.ExecuteChanges(
            new List<Movie> { first, second, third },
            dryRun: false,
            DestinationRoot,
            cancellationToken: stop.Token);

        Assert.True(outcome.StoppedByUser);
        Assert.Equal(1, outcome.MovedCount);
        Assert.Equal(2, outcome.NotReachedCount);

        // The file in progress was finished, not abandoned half-way.
        Assert.Equal("Committed", first.Status);
        Assert.False(File.Exists(first.OriginalFilePath));
        Assert.True(File.Exists(first.TargetPath!));
        Assert.Equal("movie", File.ReadAllText(first.TargetPath!));

        // Nothing was started for the others: no move, no destination folder.
        foreach (var untouched in new[] { second, third })
        {
            Assert.Equal(RenameService.NotReachedStatus, untouched.Status);
            Assert.True(untouched.ApprovedForCommit);
            Assert.False(untouched.NeedsReview);
            Assert.True(File.Exists(untouched.OriginalFilePath));
            Assert.Equal("movie", File.ReadAllText(untouched.OriginalFilePath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(untouched.TargetPath!)));
        }

        Assert.Single(Directory.EnumerateFiles(DestinationRoot, "*", SearchOption.AllDirectories));

        // Source cleanup covers only the folder the completed move emptied.
        Assert.False(Directory.Exists(Path.GetDirectoryName(first.OriginalFilePath)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(second.OriginalFilePath)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(third.OriginalFilePath)));

        var entries = ReadSingleJournal("live-commit");
        var events = entries.Select(entry => entry.GetProperty("Event").GetString()).ToList();
        Assert.Equal(1, events.Count(name => name == "MoveStarting"));
        Assert.Equal(1, events.Count(name => name == "Moved"));
        Assert.Equal("RunStopped", events.Last());
        Assert.Contains(
            "2 approved file(s) were not reached",
            entries.Last().GetProperty("Detail").GetString());

        var skipped = entries
            .Where(entry => entry.GetProperty("Event").GetString() == "Skipped")
            .ToList();
        Assert.Equal(
            new[] { second.OriginalFilePath, third.OriginalFilePath },
            skipped.Select(entry => entry.GetProperty("SourcePath").GetString()));
        Assert.All(
            skipped,
            entry => Assert.Contains(
                "stopped by the user before this file",
                entry.GetProperty("Detail").GetString()));
    }

    [Fact]
    public void StopBeforeTheFirstFile_MovesNothing()
    {
        var first = CreateMovie("First Movie", 2001, "tt0000001");
        var second = CreateMovie("Second Movie", 2002, "tt0000002");
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var outcome = CreateService().ExecuteChanges(
            new List<Movie> { first, second },
            dryRun: false,
            DestinationRoot,
            cancellationToken: stop.Token);

        Assert.True(outcome.StoppedByUser);
        Assert.Equal(0, outcome.MovedCount);
        Assert.Equal(2, outcome.NotReachedCount);
        Assert.True(File.Exists(first.OriginalFilePath));
        Assert.True(File.Exists(second.OriginalFilePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(DestinationRoot));

        var events = ReadSingleJournal("live-commit")
            .Select(entry => entry.GetProperty("Event").GetString())
            .ToList();
        Assert.DoesNotContain("MoveStarting", events);
        Assert.DoesNotContain("SourceFolderRemoved", events);
        Assert.Equal("RunStopped", events.Last());
    }

    [Fact]
    public void StopAfterTheLastFile_IsACompletedRun()
    {
        var first = CreateMovie("First Movie", 2001, "tt0000001");
        var second = CreateMovie("Second Movie", 2002, "tt0000002");
        using var stop = new CancellationTokenSource();
        var service = CreateService(stopAfterMoves: 2, stop.Cancel);

        var outcome = service.ExecuteChanges(
            new List<Movie> { first, second },
            dryRun: false,
            DestinationRoot,
            cancellationToken: stop.Token);

        // Nothing was left to stop, so this is an ordinary finished commit.
        Assert.False(outcome.StoppedByUser);
        Assert.Equal(2, outcome.MovedCount);
        Assert.Equal(0, outcome.NotReachedCount);
        Assert.True(File.Exists(first.TargetPath!));
        Assert.True(File.Exists(second.TargetPath!));
        Assert.Equal(
            "RunCompleted",
            ReadSingleJournal("live-commit").Last().GetProperty("Event").GetString());
    }

    [Fact]
    public void WithoutAStop_EveryApprovedFileIsMoved_AndADryRunMovesNone()
    {
        var first = CreateMovie("First Movie", 2001, "tt0000001");
        var second = CreateMovie("Second Movie", 2002, "tt0000002");
        var movies = new List<Movie> { first, second };

        var dryRun = CreateService().ExecuteChanges(movies, dryRun: true, DestinationRoot);

        Assert.False(dryRun.StoppedByUser);
        Assert.Equal(0, dryRun.MovedCount);
        Assert.True(File.Exists(first.OriginalFilePath));
        Assert.True(File.Exists(second.OriginalFilePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(DestinationRoot));

        var live = CreateService().ExecuteChanges(movies, dryRun: false, DestinationRoot);

        Assert.False(live.StoppedByUser);
        Assert.Equal(2, live.MovedCount);
        Assert.Equal(0, live.NotReachedCount);
        Assert.False(File.Exists(first.OriginalFilePath));
        Assert.False(File.Exists(second.OriginalFilePath));
    }

    private List<JsonElement> ReadSingleJournal(string mode)
    {
        var file = Assert.Single(Directory.GetFiles(JournalDirectory));
        Assert.Contains(mode, Path.GetFileName(file));

        return File.ReadAllLines(file)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:ScanPath"] = SourceRoot,
                ["PIM:RemoveEmptySourceFolders"] = "true",
                ["PIM:JournalDirectory"] = JournalDirectory
            })
            .Build();

    private RenameService CreateService(int stopAfterMoves = 0, Action? stop = null)
    {
        var configuration = CreateConfiguration();
        IOperationJournal journal = new JsonLinesOperationJournal(configuration);

        if (stop != null)
            journal = new ActAfterMovesJournal(journal, stopAfterMoves, stop);

        return new RenameService(
            new NoConflictDestinationService(),
            new DestinationPathBuilder(),
            configuration,
            new ScanProgress(),
            new SourceCleanupStatus(),
            journal);
    }

    private Movie CreateMovie(string title, int year, string imdbId)
    {
        var folderName = $"{title} ({year}) {{imdb-{imdbId}}}";
        var sourceFolder = Path.Combine(SourceRoot, "PG", folderName);
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(DestinationRoot);

        var sourceFile = Path.Combine(sourceFolder, $"{title}.mp4");
        File.WriteAllText(sourceFile, "movie");

        return new Movie
        {
            Title = title,
            Year = year,
            ImdbId = imdbId,
            OriginalFilePath = sourceFile,
            FileSizeBytes = new FileInfo(sourceFile).Length,
            FileName = Path.GetFileName(sourceFile),
            TargetPath = Path.Combine(DestinationRoot, folderName, $"{folderName}.mp4"),
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
