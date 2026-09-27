using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class RenameServiceSourceCleanupTests
{
    [Fact]
    public void DryRun_DoesNotMoveFileOrCleanSourceFolders()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var service = CreateService(fixture.SourceRoot);
            var movie = fixture.Movie;

            service.ExecuteChanges(
                new List<Movie> { movie },
                dryRun: true,
                fixture.DestinationRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.True(Directory.Exists(fixture.MovieSourceFolder));
            Assert.False(File.Exists(movie.TargetPath!));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_RemovesEmptyMovieFolder_ButPreservesTopLevelOrganizationFolder()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var service = CreateService(fixture.SourceRoot);
            var movie = fixture.Movie;

            service.ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(movie.TargetPath!));
            Assert.False(Directory.Exists(fixture.MovieSourceFolder));
            Assert.True(Directory.Exists(fixture.TopLevelSourceFolder));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_PreservesSourceFolder_WhenSidecarRemains()
    {
        var fixture = CreateFixture(includeSidecar: true);

        try
        {
            var service = CreateService(fixture.SourceRoot);
            var movie = fixture.Movie;

            service.ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(movie.TargetPath!));
            Assert.True(Directory.Exists(fixture.MovieSourceFolder));
            Assert.True(File.Exists(fixture.SidecarFile!));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LiveCommit_BlockedReviewOrError_DoesNotMoveFile(
        bool needsReview,
        bool hasError)
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var movie = fixture.Movie;
            movie.NeedsReview = needsReview;
            movie.ReviewReason = needsReview ? "Manual review required" : null;
            movie.ErrorMessage = hasError ? "Simulated error" : null;

            CreateService(fixture.SourceRoot).ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.False(File.Exists(movie.TargetPath!));
            Assert.False(movie.ApprovedForCommit);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_TargetOutsideDestinationRoot_DoesNotMoveFile()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var movie = fixture.Movie;
            movie.TargetPath = Path.Combine(
                fixture.Root,
                "OutsideDestination",
                "movie.mp4");

            CreateService(fixture.SourceRoot).ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.False(File.Exists(movie.TargetPath));
            Assert.True(movie.HasError);
            Assert.False(movie.ApprovedForCommit);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_ExistingTargetIsNeverOverwritten()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var movie = fixture.Movie;
            Directory.CreateDirectory(Path.GetDirectoryName(movie.TargetPath!)!);
            File.WriteAllText(movie.TargetPath!, "existing target");

            CreateService(fixture.SourceRoot).ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.Equal("existing target", File.ReadAllText(movie.TargetPath!));
            Assert.True(movie.HasDestinationConflict);
            Assert.True(movie.NeedsReview);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_TargetAppearingAfterDryRunIsCaught()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var service = CreateService(fixture.SourceRoot);
            var movie = fixture.Movie;

            service.ExecuteChanges(
                new List<Movie> { movie },
                dryRun: true,
                fixture.DestinationRoot);

            Directory.CreateDirectory(Path.GetDirectoryName(movie.TargetPath!)!);
            File.WriteAllText(movie.TargetPath!, "appeared after dry run");
            movie.ApprovedForCommit = true;
            movie.Status = "Approved by matching dry run";

            service.ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.Equal("appeared after dry run", File.ReadAllText(movie.TargetPath!));
            Assert.True(movie.HasDestinationConflict);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_MissingSourceIsHandledWithoutCreatingTarget()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            File.Delete(fixture.SourceFile);
            var movie = fixture.Movie;

            CreateService(fixture.SourceRoot).ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.False(File.Exists(movie.TargetPath!));
            Assert.Equal("Source File Missing", movie.Status);
            Assert.False(movie.ApprovedForCommit);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void LiveCommit_SourceAndTargetSameFile_IsBlockedWithoutDataLoss()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var movie = fixture.Movie;
            movie.TargetPath = fixture.SourceFile;

            CreateService(fixture.SourceRoot).ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.SourceRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.Equal("movie", File.ReadAllText(fixture.SourceFile));
            Assert.True(movie.HasDestinationConflict);
            Assert.True(movie.NeedsReview);
            Assert.False(movie.ApprovedForCommit);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Consolidation_DuplicateSkipNeverDeletesSourceMedia()
    {
        var fixture = CreateFixture(includeSidecar: false);

        try
        {
            var movie = fixture.Movie;
            movie.IsDuplicate = true;
            movie.KeepRecommended = false;
            movie.ApprovedForCommit = false;
            movie.Status = "Duplicate - Skip";

            CreateService(fixture.SourceRoot).ExecuteChanges(
                new List<Movie> { movie },
                dryRun: false,
                fixture.DestinationRoot);

            Assert.True(File.Exists(fixture.SourceFile));
            Assert.Equal("movie", File.ReadAllText(fixture.SourceFile));
            Assert.False(File.Exists(movie.TargetPath!));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static RenameService CreateService(string sourceRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PIM:ScanPath"] = sourceRoot,
                ["PIM:RemoveEmptySourceFolders"] = "true"
            })
            .Build();

        return new RenameService(
            new NoConflictDestinationService(),
            new DestinationPathBuilder(),
            configuration,
            new ScanProgress(),
            new SourceCleanupStatus());
    }

    private static Fixture CreateFixture(bool includeSidecar)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PIM-Cleanup-Tests",
            Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "Source");
        var topLevel = Path.Combine(sourceRoot, "PG");
        var movieFolder = Path.Combine(
            topLevel,
            "Better Off Dead (1985) {imdb-tt0088794}");
        var destinationRoot = Path.Combine(root, "Destination");

        Directory.CreateDirectory(movieFolder);
        Directory.CreateDirectory(destinationRoot);

        var sourceFile = Path.Combine(movieFolder, "Better Off Dead.mp4");
        File.WriteAllText(sourceFile, "movie");

        string? sidecarFile = null;
        if (includeSidecar)
        {
            sidecarFile = Path.Combine(movieFolder, "Better Off Dead.srt");
            File.WriteAllText(sidecarFile, "subtitle");
        }

        var targetFolder = Path.Combine(
            destinationRoot,
            "Better Off Dead (1985) {imdb-tt0088794}");
        var targetPath = Path.Combine(
            targetFolder,
            "Better Off Dead (1985) {imdb-tt0088794}.mp4");

        var movie = new Movie
        {
            Title = "Better Off Dead",
            Year = 1985,
            ImdbId = "tt0088794",
            OriginalFilePath = sourceFile,
            FileName = Path.GetFileName(sourceFile),
            TargetPath = targetPath,
            ApprovedForCommit = true,
            Status = "Rename preview generated"
        };

        return new Fixture(
            root,
            sourceRoot,
            topLevel,
            movieFolder,
            destinationRoot,
            sourceFile,
            sidecarFile,
            movie);
    }

    private sealed class NoConflictDestinationService : IDestinationConflictService
    {
        public void InvalidateCache()
        {
        }

        public void RecordDestinationEntry(string path)
        {
        }

        public DestinationConflictResult Check(
            Movie movie,
            string outputPath,
            bool refresh = false)
        {
            return DestinationConflictResult.NoConflict();
        }
    }

    private sealed record Fixture(
        string Root,
        string SourceRoot,
        string TopLevelSourceFolder,
        string MovieSourceFolder,
        string DestinationRoot,
        string SourceFile,
        string? SidecarFile,
        Movie Movie) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
