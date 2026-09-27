using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class OperationJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PIM-Journal-Tests",
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
    public void LiveCommit_RecordsMoveBeforeAndAfter_AndRemovedSourceFolder()
    {
        var movie = CreateMovie("Better Off Dead", 1985, "tt0088794");
        var service = CreateService(new JsonLinesOperationJournal(CreateConfiguration()));

        service.ExecuteChanges(new List<Movie> { movie }, dryRun: false, DestinationRoot);

        Assert.Equal("Committed", movie.Status);
        Assert.True(File.Exists(movie.TargetPath!));

        var entries = ReadSingleJournal("live-commit");
        var events = entries.Select(entry => entry.GetProperty("Event").GetString()).ToList();

        Assert.Equal("RunStarted", events.First());
        Assert.Equal("RunCompleted", events.Last());
        Assert.True(
            events.IndexOf("MoveStarting") < events.IndexOf("Moved"),
            "The intended move must be recorded before the move completes.");

        var moved = entries.Single(entry => entry.GetProperty("Event").GetString() == "Moved");
        Assert.Equal(movie.OriginalFilePath, moved.GetProperty("SourcePath").GetString());
        Assert.Equal(movie.TargetPath, moved.GetProperty("TargetPath").GetString());
        Assert.Equal("tt0088794", moved.GetProperty("ImdbId").GetString());

        Assert.Contains(
            entries,
            entry => entry.GetProperty("Event").GetString() == "SourceFolderRemoved" &&
                     entry.GetProperty("SourcePath").GetString() ==
                     Path.GetDirectoryName(movie.OriginalFilePath));
        Assert.Equal(service.LastJournalLocation, Directory.GetFiles(JournalDirectory).Single());
    }

    [Fact]
    public void DryRun_RecordsWouldMove_AndMovesNothing()
    {
        var movie = CreateMovie("Better Off Dead", 1985, "tt0088794");
        var service = CreateService(new JsonLinesOperationJournal(CreateConfiguration()));

        service.ExecuteChanges(new List<Movie> { movie }, dryRun: true, DestinationRoot);

        Assert.True(File.Exists(movie.OriginalFilePath));
        Assert.False(File.Exists(movie.TargetPath!));

        var events = ReadSingleJournal("dry-run")
            .Select(entry => entry.GetProperty("Event").GetString())
            .ToList();

        Assert.Contains("WouldMove", events);
        Assert.DoesNotContain("MoveStarting", events);
        Assert.DoesNotContain("Moved", events);
        Assert.DoesNotContain("SourceFolderRemoved", events);
    }

    [Fact]
    public void LiveCommit_WhenJournalCannotStart_MovesNothingAndReportsError()
    {
        var first = CreateMovie("Better Off Dead", 1985, "tt0088794");
        var second = CreateMovie("The Sure Thing", 1985, "tt0090103");
        var service = CreateService(new FailingJournal(failOnStart: true));

        service.ExecuteChanges(new List<Movie> { first, second }, dryRun: false, DestinationRoot);

        foreach (var movie in new[] { first, second })
        {
            Assert.True(File.Exists(movie.OriginalFilePath));
            Assert.False(File.Exists(movie.TargetPath!));
            Assert.False(movie.ApprovedForCommit);
            Assert.True(movie.HasError);
        }
    }

    [Fact]
    public void LiveCommit_WhenPreMoveEntryCannotBeWritten_StopsBeforeMovingAnyFile()
    {
        var first = CreateMovie("Better Off Dead", 1985, "tt0088794");
        var second = CreateMovie("The Sure Thing", 1985, "tt0090103");
        var service = CreateService(new FailingJournal(failOnStart: false));

        service.ExecuteChanges(new List<Movie> { first, second }, dryRun: false, DestinationRoot);

        foreach (var movie in new[] { first, second })
        {
            Assert.True(File.Exists(movie.OriginalFilePath));
            Assert.False(File.Exists(movie.TargetPath!));
            Assert.False(movie.ApprovedForCommit);
            Assert.NotEqual("Committed", movie.Status);
        }

        // Source folders must survive because nothing was moved out of them.
        Assert.True(Directory.Exists(Path.GetDirectoryName(first.OriginalFilePath)));
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

    private RenameService CreateService(IOperationJournal journal) =>
        new(
            new NoConflictDestinationService(),
            new DestinationPathBuilder(),
            CreateConfiguration(),
            new ScanProgress(),
            new SourceCleanupStatus(),
            journal);

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
            FileName = Path.GetFileName(sourceFile),
            TargetPath = Path.Combine(DestinationRoot, folderName, $"{folderName}.mp4"),
            ApprovedForCommit = true,
            Status = "Rename preview generated"
        };
    }

    private sealed class FailingJournal : IOperationJournal
    {
        private readonly bool _failOnStart;

        public FailingJournal(bool failOnStart) => _failOnStart = failOnStart;

        public IOperationJournalRun StartRun(bool dryRun, string destinationRoot, int itemCount)
        {
            if (_failOnStart)
                throw new IOException("Simulated journal failure.");

            return new FailingRun();
        }

        private sealed class FailingRun : IOperationJournalRun
        {
            public string Location => "simulated";

            public void Record(OperationJournalEntry entry) =>
                throw new IOException("Simulated journal write failure.");

            public void Dispose()
            {
            }
        }
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
