using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class PlexConflictIntegrationTests
{
    [Fact]
    public void ProfileGeneratedTarget_PlexSameEditionConflict_BlocksCommit()
    {
        var profile = CreateProfile(OrganizationLevelType.MpaRating);
        var movie = CreateMovie(
            "The Hunt for Red October",
            1990,
            "tt0099810",
            "PG",
            versionTag: null);

        ApplyProfileTarget(movie, profile);

        var plex = new StubPlexConflictService(
            PlexLibraryConflictResult.Conflict(
                PlexLibraryConflictType.SameImdbIdDifferentPath,
                "Plex already contains the same IMDb ID and edition at a different path.",
                @"G:\[PLEX]\Movies\PG\The Hunt for Red October (1990) {imdb-tt0099810}\The Hunt for Red October (1990) {imdb-tt0099810}.mp4"));

        var detector = new MovieConflictDetectionService(
            new NoConflictDestinationService(),
            plex);

        detector.ApplyConflictDetection(new List<Movie> { movie }, profile.DestinationRoot);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Equal("Needs Review - Conflict Detected", movie.Status);
        Assert.Contains("Plex library conflict:", movie.ReviewReason);
        Assert.Contains("PG", movie.TargetPath!);
    }

    [Fact]
    public void ProfileGeneratedAlternateEdition_PlexAlternateConflict_BlocksCommit()
    {
        var profile = CreateProfile(OrganizationLevelType.MpaRating);
        var movie = CreateMovie(
            "The Goonies",
            1985,
            "tt0089218",
            "PG",
            "Family Edit");

        movie.IsAlternateVersion = true;
        ApplyProfileTarget(movie, profile);

        var plex = new StubPlexConflictService(
            PlexLibraryConflictResult.Conflict(
                PlexLibraryConflictType.ExistingAlternateVersion,
                "Plex already contains this IMDb ID with a different edition.",
                @"G:\[PLEX]\Movies\PG\The Goonies (1985) {imdb-tt0089218}\The Goonies (1985) {imdb-tt0089218}.mp4"));

        var detector = new MovieConflictDetectionService(
            new NoConflictDestinationService(),
            plex);

        detector.ApplyConflictDetection(new List<Movie> { movie }, profile.DestinationRoot);

        Assert.True(movie.HasPlexLibraryConflict);
        Assert.True(movie.NeedsReview);
        Assert.False(movie.ApprovedForCommit);
        Assert.Contains("{edition-Family Edit}", movie.TargetPath!);
    }

    [Fact]
    public void ProfileGeneratedTarget_NoConflict_RemainsApproved()
    {
        var profile = CreateProfile(
            OrganizationLevelType.MpaRating,
            OrganizationLevelType.PrimaryGenre);
        var movie = CreateMovie(
            "True Lies",
            1994,
            "tt0111503",
            "R",
            versionTag: null);
        movie.PrimaryGenre = "Action";
        movie.Genres = new List<string> { "Action", "Comedy" };

        ApplyProfileTarget(movie, profile);

        var detector = new MovieConflictDetectionService(
            new NoConflictDestinationService(),
            new StubPlexConflictService(PlexLibraryConflictResult.NoConflict()));

        detector.ApplyConflictDetection(new List<Movie> { movie }, profile.DestinationRoot);

        Assert.False(movie.HasPlexLibraryConflict);
        Assert.False(movie.HasDestinationConflict);
        Assert.False(movie.NeedsReview);
        Assert.True(movie.ApprovedForCommit);
        Assert.Contains(Path.Combine("R", "Action"), movie.TargetPath!);
    }

    private static DestinationProfile CreateProfile(
        params OrganizationLevelType[] levels)
    {
        var profile = new DestinationProfile
        {
            Name = "Integration Test",
            DestinationRoot = Path.Combine(
                Path.GetTempPath(),
                "PIM-Integration-Destination",
                Guid.NewGuid().ToString("N"))
        };

        foreach (var level in levels)
            profile.OrganizationLevels.Add(DestinationOrganizationLevel.Create(level));

        return profile;
    }

    private static Movie CreateMovie(
        string title,
        int year,
        string imdbId,
        string rating,
        string? versionTag)
    {
        return new Movie
        {
            Title = title,
            Year = year,
            ImdbId = imdbId,
            MpaRating = rating,
            VersionTag = versionTag,
            OriginalFilePath = Path.Combine(
                Path.GetTempPath(),
                "PIM-Integration-Source",
                Guid.NewGuid().ToString("N") + ".mp4"),
            FileName = title + ".mp4",
            ApprovedForCommit = true,
            Status = "Rename preview generated"
        };
    }

    private static void ApplyProfileTarget(Movie movie, DestinationProfile profile)
    {
        var builder = new DestinationPathBuilder();
        var sourceRoot = Path.Combine(Path.GetTempPath(), "PIM-Integration-Source");
        var result = builder.Build(movie, profile, sourceRoot, ".mp4");

        movie.NormalizedFolderName = result.MovieFolderName;
        movie.NormalizedFileName = result.FileName;
        movie.TargetPath = result.FullFilePath;
        movie.DestinationProfileId = profile.Id;
        movie.DestinationProfileRevision = profile.Revision;
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
}
