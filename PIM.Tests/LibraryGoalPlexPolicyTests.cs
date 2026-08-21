using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class LibraryGoalPlexPolicyTests
{
    private const string SourceRoot = @"D:\PIM\_Test\_Movies";
    private const string InsidePlexPath =
        @"D:\PIM\_Test\_Movies\Edited Movies\Policy Movie\Policy Movie (2024) {imdb-tt1234567}.mkv";
    private const string OutsidePlexPath =
        @"G:\[PLEX]\Movies\Policy Movie\Policy Movie (2024) {imdb-tt1234567}.mkv";

    [Fact]
    public void LibraryGoal_DefaultsToConsolidation()
    {
        Assert.Equal(
            LibraryGoal.Consolidation,
            LibraryGoalSettings.Parse(null));
        Assert.Equal(
            LibraryGoal.Consolidation,
            LibraryGoalSettings.Parse("unsupported-value"));
        Assert.Equal(
            LibraryGoal.Consolidation,
            new DryRunPreviewResult().LibraryGoal);
    }

    [Fact]
    public void Consolidation_BlocksPlexMatchInsideSourceRoot()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.Consolidation);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPlexTrackedMigration);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal(InsidePlexPath, movie.ExistingPlexLibraryPath);
    }

    [Fact]
    public void Reorganization_AllowsPlexMatchInsideSourceRoot()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.ReorganizationMigration);

        Assert.False(movie.HasPlexLibraryConflict);
        Assert.True(movie.IsPlexTrackedMigration);
        Assert.False(movie.NeedsReview);
        Assert.True(movie.ApprovedForCommit);
        Assert.Equal(InsidePlexPath, movie.ExistingPlexLibraryPath);
        Assert.Contains(
            "Plex-tracked migration",
            movie.PlexTrackedMigrationReason);
    }

    [Fact]
    public void Reorganization_BlocksPlexMatchOutsideSourceRoot()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexMatch(OutsidePlexPath));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.ReorganizationMigration);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPlexTrackedMigration);
        Assert.True(movie.NeedsReview);
        Assert.Contains(
            "outside the selected source tree",
            movie.PlexLibraryConflictReason);
        Assert.Equal(OutsidePlexPath, movie.ExistingPlexLibraryPath);
    }

    [Fact]
    public void AllowedMigration_DoesNotClearDestinationConflict()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexMatch(InsidePlexPath),
            DestinationConflictResult.Conflict(
                DestinationConflictType.TargetFileAlreadyExists,
                "The target already exists.",
                @"X:\PIM\Destination\Existing.mkv"));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.ReorganizationMigration);

        Assert.True(movie.IsPlexTrackedMigration);
        Assert.False(movie.HasPlexLibraryConflict);
        Assert.True(movie.HasDestinationConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Contains("Destination conflict:", movie.ReviewReason);
    }

    [Fact]
    public void AllowedMigration_DoesNotClearMetadataReview()
    {
        var movie = CreateMovie();
        movie.MatchConfidence = 60;
        movie.RequireReview("Low confidence metadata match (60% confidence)");

        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.ReorganizationMigration);

        Assert.True(movie.IsPlexTrackedMigration);
        Assert.False(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Contains("Low confidence metadata match", movie.ReviewReason);
    }

    [Theory]
    [InlineData(LibraryGoal.Consolidation)]
    [InlineData(LibraryGoal.ReorganizationMigration)]
    public void DuplicateDecisions_RemainUnchangedAcrossLibraryGoals(
        LibraryGoal libraryGoal)
    {
        var smaller = CreateMovie();
        smaller.FileName = "Policy Movie smaller.mkv";
        smaller.FileSizeBytes = 100 * 1024 * 1024;

        var larger = CreateMovie();
        larger.FileName = "Policy Movie larger.mkv";
        larger.FileSizeBytes = 200 * 1024 * 1024;

        var movies = new List<Movie> { smaller, larger };
        new DuplicateService().Process(movies);

        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));
        detector.ApplyConflictDetection(
            movies,
            @"X:\PIM\Destination",
            SourceRoot,
            libraryGoal);

        Assert.True(smaller.IsDuplicate);
        Assert.False(smaller.KeepRecommended);
        Assert.False(smaller.ApprovedForCommit);
        Assert.True(larger.IsDuplicate);
        Assert.True(larger.KeepRecommended);
    }

    [Theory]
    [InlineData(LibraryGoal.Consolidation)]
    [InlineData(LibraryGoal.ReorganizationMigration)]
    public void EditionDecisions_RemainUnchangedAcrossLibraryGoals(
        LibraryGoal libraryGoal)
    {
        var standard = CreateMovie();
        standard.TargetPath =
            @"X:\PIM\Destination\Policy Movie standard.mkv";

        var edited = CreateMovie();
        edited.VersionTag = "Family Edit";
        edited.FileName = "Policy Movie Family Edit.mkv";
        edited.TargetPath =
            @"X:\PIM\Destination\Policy Movie Family Edit.mkv";

        var movies = new List<Movie> { standard, edited };
        new DuplicateService().Process(movies);

        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));
        detector.ApplyConflictDetection(
            movies,
            @"X:\PIM\Destination",
            SourceRoot,
            libraryGoal);

        Assert.True(standard.KeepRecommended);
        Assert.False(standard.IsAlternateVersion);
        Assert.Equal("Family Edit", edited.VersionTag);
        Assert.True(edited.IsAlternateVersion);
    }

    [Fact]
    public void PathComparison_IsCaseInsensitiveAndHandlesTrailingSeparators()
    {
        var relationship = PlexMatchPolicy.GetPathRelationship(
            @"D:\Movies\",
            @"d:\movies\Nested\Movie.mkv\");

        Assert.Equal(
            PlexPathRelationship.InsideSource,
            relationship);
    }

    [Fact]
    public void PathComparison_RequiresTrueDirectoryBoundary()
    {
        var relationship = PlexMatchPolicy.GetPathRelationship(
            @"D:\Movies",
            @"D:\Movies-Backup\Movie.mkv");

        Assert.Equal(
            PlexPathRelationship.OutsideSource,
            relationship);
    }

    [Fact]
    public void PathComparison_SupportsUncPaths()
    {
        var relationship = PlexMatchPolicy.GetPathRelationship(
            @"\\MediaServer\Movies\",
            @"\\mediaserver\movies\Nested\Movie.mkv");

        Assert.Equal(
            PlexPathRelationship.InsideSource,
            relationship);
    }

    [Fact]
    public void SwitchingGoals_ReplacesOnlyGoalDependentPlexState()
    {
        var movie = CreateMovie();
        var originalTitle = movie.Title;
        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));

        Evaluate(
            detector,
            movie,
            LibraryGoal.Consolidation);
        Assert.True(movie.HasPlexLibraryConflict);

        Evaluate(
            detector,
            movie,
            LibraryGoal.ReorganizationMigration);

        Assert.False(movie.HasPlexLibraryConflict);
        Assert.True(movie.IsPlexTrackedMigration);
        Assert.False(movie.NeedsReview);
        Assert.True(movie.ApprovedForCommit);
        Assert.DoesNotContain(
            "Plex library conflict:",
            movie.ReviewReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(movie.MetadataFetched);
        Assert.Equal(originalTitle, movie.Title);

        Evaluate(
            detector,
            movie,
            LibraryGoal.Consolidation);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPlexTrackedMigration);
        Assert.True(movie.NeedsReview);
    }

    [Fact]
    public void LivePreCommitRecheck_UsesCurrentLibraryGoal()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexMatch(InsidePlexPath));

        // The reviewed dry-run plan allowed an in-source migration.
        Evaluate(
            detector,
            movie,
            LibraryGoal.ReorganizationMigration);
        Assert.True(movie.IsPlexTrackedMigration);
        Assert.True(movie.ApprovedForCommit);

        // Simulate the required immediate live pre-commit recheck after the
        // saved goal was changed back to the safer Consolidation policy.
        Evaluate(
            detector,
            movie,
            LibraryGoal.Consolidation);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public void Reorganization_PlexLookupFailureRemainsFailClosed()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexLibraryConflictResult.Conflict(
                PlexLibraryConflictType.LibraryMismatch,
                "Plex library validation could not be completed."));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.ReorganizationMigration);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPlexTrackedMigration);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Contains(
            "could not be completed",
            movie.PlexLibraryConflictReason);
    }

    [Fact]
    public void Reorganization_IndeterminatePlexPathRemainsFailClosed()
    {
        var movie = CreateMovie();
        var detector = CreateDetector(
            PlexMatch("Plex library item without a filesystem path"));

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            LibraryGoal.ReorganizationMigration);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.False(movie.IsPlexTrackedMigration);
        Assert.Contains(
            "could not safely determine",
            movie.PlexLibraryConflictReason);
    }

    private static void Evaluate(
        MovieConflictDetectionService detector,
        Movie movie,
        LibraryGoal libraryGoal)
    {
        detector.ClearConflictState(new List<Movie> { movie });
        new DuplicateService().Process(new List<Movie> { movie });

        detector.ApplyConflictDetection(
            new List<Movie> { movie },
            @"X:\PIM\Destination",
            SourceRoot,
            libraryGoal);
    }

    private static MovieConflictDetectionService CreateDetector(
        PlexLibraryConflictResult plexResult,
        DestinationConflictResult? destinationResult = null)
    {
        return new MovieConflictDetectionService(
            new StubDestinationConflictService(
                destinationResult ?? DestinationConflictResult.NoConflict()),
            new StubPlexConflictService(plexResult));
    }

    private static Movie CreateMovie()
    {
        return new Movie
        {
            Title = "Policy Movie",
            Year = 2024,
            ImdbId = "tt1234567",
            FileName = "Policy Movie 2024.mkv",
            OriginalFilePath =
                @"D:\PIM\_Test\_Movies\Policy Movie 2024.mkv",
            TargetPath =
                @"X:\PIM\Destination\Policy Movie (2024) {imdb-tt1234567}.mkv",
            FileSizeBytes = 200 * 1024 * 1024,
            MetadataFetched = true,
            MetadataMatchedByImdbId = true,
            MatchConfidence = 100,
            ApprovedForCommit = true,
            Status = "Rename preview generated"
        };
    }

    private static PlexLibraryConflictResult PlexMatch(string existingPath)
    {
        return PlexLibraryConflictResult.Conflict(
            PlexLibraryConflictType.SameImdbIdDifferentPath,
            "Plex already contains the same IMDb ID at a different path.",
            existingPath);
    }

    private sealed class StubPlexConflictService : IPlexLibraryConflictService
    {
        private readonly PlexLibraryConflictResult _result;

        public StubPlexConflictService(PlexLibraryConflictResult result)
        {
            _result = result;
        }

        public PlexLibraryConflictResult Check(Movie movie) => _result;
    }

    private sealed class StubDestinationConflictService :
        IDestinationConflictService
    {
        private readonly DestinationConflictResult _result;

        public StubDestinationConflictService(
            DestinationConflictResult result)
        {
            _result = result;
        }

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
            return _result;
        }
    }
}
