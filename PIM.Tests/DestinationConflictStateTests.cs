using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class DestinationConflictStateTests
{
    [Fact]
    public void SwitchingToEmptyDestination_RemovesPriorDestinationConflict()
    {
        using var scenario = new DestinationSwitchScenario();

        scenario.EvaluateAtDestinationA();
        Assert.True(scenario.Movie.HasDestinationConflict);

        scenario.SwitchToDestinationB();

        Assert.False(scenario.Movie.HasDestinationConflict);
        Assert.Null(scenario.Movie.DestinationConflictReason);
        Assert.Null(scenario.Movie.ExistingDestinationPath);
        Assert.DoesNotContain(
            "Destination conflict:",
            scenario.Movie.ReviewReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchingToEmptyDestination_PreservesPlexConflict()
    {
        using var scenario = new DestinationSwitchScenario();

        scenario.EvaluateAtDestinationA();
        scenario.Detector.ClearDestinationConflictState(
            new List<Movie> { scenario.Movie });

        Assert.False(scenario.Movie.HasDestinationConflict);
        Assert.True(scenario.Movie.HasPlexLibraryConflict);
        Assert.True(scenario.Movie.NeedsReview);
        Assert.False(scenario.Movie.ApprovedForCommit);
        Assert.Contains(
            "Plex library conflict:",
            scenario.Movie.ReviewReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        scenario.SwitchToDestinationB();

        Assert.False(scenario.Movie.HasDestinationConflict);
        Assert.True(scenario.Movie.HasPlexLibraryConflict);
        Assert.True(scenario.Movie.NeedsReview);
        Assert.False(scenario.Movie.ApprovedForCommit);
    }

    [Fact]
    public void SwitchingBackToOriginalDestination_ReappliesDestinationConflict()
    {
        using var scenario = new DestinationSwitchScenario();

        scenario.EvaluateAtDestinationA();
        scenario.SwitchToDestinationB();
        Assert.False(scenario.Movie.HasDestinationConflict);

        scenario.SwitchBackToDestinationA();

        Assert.True(scenario.Movie.HasDestinationConflict);
        Assert.NotNull(scenario.Movie.DestinationConflictReason);
        Assert.Equal(
            scenario.ExistingDestinationMoviePath,
            scenario.Movie.ExistingDestinationPath);
        Assert.True(scenario.Movie.HasPlexLibraryConflict);
    }

    private sealed class DestinationSwitchScenario : IDisposable
    {
        private readonly string _testRoot;
        private readonly DestinationProfile _profile;
        private readonly DuplicateService _duplicateService = new();
        private readonly DestinationPathBuilder _pathBuilder = new();

        public DestinationSwitchScenario()
        {
            _testRoot = Path.Combine(
                Path.GetTempPath(),
                $"PIM-DestinationSwitchTests-{Guid.NewGuid():N}");
            SourceRoot = Directory.CreateDirectory(
                Path.Combine(_testRoot, "Source")).FullName;
            DestinationA = Directory.CreateDirectory(
                Path.Combine(_testRoot, "Destination A")).FullName;
            DestinationB = Directory.CreateDirectory(
                Path.Combine(_testRoot, "Destination B")).FullName;

            Movie = new Movie
            {
                Title = "Stateful Movie",
                Year = 2024,
                ImdbId = "tt1234567",
                FileName = "Stateful Movie 2024.mkv",
                OriginalFilePath = Path.Combine(
                    SourceRoot,
                    "Stateful Movie 2024.mkv"),
                MetadataFetched = true
            };

            var existingDirectory = Directory.CreateDirectory(Path.Combine(
                DestinationA,
                "Already Present"));
            ExistingDestinationMoviePath = Path.Combine(
                existingDirectory.FullName,
                "Stateful Movie (2024) {imdb-tt1234567}.mkv");
            File.WriteAllBytes(
                ExistingDestinationMoviePath,
                Array.Empty<byte>());

            _profile = new DestinationProfile
            {
                Name = "Destination Switch Test",
                DestinationRoot = DestinationA
            };

            var progress = new ScanProgress();
            Detector = new MovieConflictDetectionService(
                new DestinationConflictService(progress),
                new AlwaysConflictPlexService());
        }

        public string SourceRoot { get; }

        public string DestinationA { get; }

        public string DestinationB { get; }

        public string ExistingDestinationMoviePath { get; }

        public Movie Movie { get; }

        public MovieConflictDetectionService Detector { get; }

        public void EvaluateAtDestinationA()
        {
            Evaluate(DestinationA);
        }

        public void SwitchToDestinationB()
        {
            Detector.ClearDestinationConflictState(
                new List<Movie> { Movie });
            Evaluate(DestinationB);
        }

        public void SwitchBackToDestinationA()
        {
            Detector.ClearDestinationConflictState(
                new List<Movie> { Movie });
            Evaluate(DestinationA);
        }

        public void Dispose()
        {
            if (!Directory.Exists(_testRoot))
                return;

            var fullPath = Path.GetFullPath(_testRoot);
            var expectedParent = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(
                    expectedParent,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal) ||
                !Path.GetFileName(fullPath).StartsWith(
                    "PIM-DestinationSwitchTests-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to clean unexpected test path '{fullPath}'.");
            }

            Directory.Delete(fullPath, recursive: true);
        }

        private void Evaluate(string destinationRoot)
        {
            _profile.DestinationRoot = destinationRoot;
            _profile.Revision++;

            Detector.ClearConflictState(new List<Movie> { Movie });
            _duplicateService.Process(new List<Movie> { Movie });

            var destination = _pathBuilder.Build(
                Movie,
                _profile,
                SourceRoot,
                ".mkv");
            Movie.TargetPath = destination.FullFilePath;
            Movie.DestinationProfileId = _profile.Id;
            Movie.DestinationProfileRevision = _profile.Revision;

            Detector.ApplyConflictDetection(
                new List<Movie> { Movie },
                destinationRoot);
        }
    }

    private sealed class AlwaysConflictPlexService : IPlexLibraryConflictService
    {
        public PlexLibraryConflictResult Check(Movie movie)
        {
            return PlexLibraryConflictResult.Conflict(
                PlexLibraryConflictType.SameImdbIdDifferentPath,
                "Plex already contains this movie at a different path.",
                Path.Combine(
                    Path.GetTempPath(),
                    "PIM-Plex-Library",
                    "Stateful Movie (2024) {imdb-tt1234567}.mkv"));
        }
    }
}
