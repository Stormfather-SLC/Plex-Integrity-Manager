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
