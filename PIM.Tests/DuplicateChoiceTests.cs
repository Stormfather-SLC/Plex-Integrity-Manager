using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class DuplicateChoiceTests
{
    private const long OneMb = 1024 * 1024;

    [Fact]
    public void TiedCopies_BothNeedReviewUntilAHumanChooses()
    {
        var (first, second) = CreateTiedPair();
        var movies = new List<Movie> { first, second };

        new DuplicateService().Process(movies);

        Assert.All(movies, movie =>
        {
            Assert.True(movie.NeedsReview);
            Assert.True(movie.HasDuplicateTieReview);
            Assert.False(movie.ApprovedForCommit);
        });
    }

    [Fact]
    public void ChoosingACopy_ApprovesOnlyThatCopyAndSkipsTheOther()
    {
        var (first, second) = CreateTiedPair();
        var movies = new List<Movie> { first, second };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);

        // Choose the smaller copy to prove the human choice overrides size.
        Assert.True(duplicates.ChoosePreferredCopy(second, movies));
        duplicates.Process(movies);

        Assert.True(second.IsManuallyKept);
        Assert.True(second.KeepRecommended);
        Assert.True(second.ApprovedForCommit);
        Assert.False(second.NeedsReview);

        Assert.False(first.IsManuallyKept);
        Assert.True(first.IsDuplicate);
        Assert.False(first.KeepRecommended);
        Assert.False(first.ApprovedForCommit);
        Assert.False(first.NeedsReview);
    }

    [Fact]
    public void ChoosingACopy_KeepsEveryOtherReviewReason()
    {
        var (first, second) = CreateTiedPair();
        first.FileName = new string('x', 130) + ".mkv";
        var movies = new List<Movie> { first, second };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);

        Assert.True(duplicates.ChoosePreferredCopy(first, movies));
        duplicates.Process(movies);

        Assert.True(first.NeedsReview);
        Assert.Equal("Suspicious file name", first.ReviewReason);
        Assert.False(first.ApprovedForCommit);
        Assert.False(second.ApprovedForCommit);
    }

    [Fact]
    public void ChoosingACopy_DoesNotResolveATieInAnotherEdition()
    {
        var (standardA, standardB) = CreateTiedPair();
        var (extendedA, extendedB) = CreateTiedPair();
        extendedA.VersionTag = "Extended Edition";
        extendedB.VersionTag = "Extended Edition";
        var movies = new List<Movie> { standardA, standardB, extendedA, extendedB };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);

        Assert.True(duplicates.ChoosePreferredCopy(standardA, movies));
        duplicates.Process(movies);

        Assert.True(standardA.ApprovedForCommit);
        Assert.True(extendedA.HasDuplicateTieReview);
        Assert.True(extendedB.HasDuplicateTieReview);
        Assert.False(extendedA.ApprovedForCommit);
        Assert.False(extendedB.ApprovedForCommit);
    }

    [Fact]
    public void ChoosePreferredCopy_RefusesMoviesThatAreNotInATie()
    {
        var unique = CreateMovie("Unique.mkv", 700 * OneMb);
        unique.ImdbId = "tt7777777";
        var clearWinner = CreateMovie("Winner.mkv", 900 * OneMb);
        var clearLoser = CreateMovie("Loser.mkv", 300 * OneMb);
        var movies = new List<Movie> { unique, clearWinner, clearLoser };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);

        Assert.False(duplicates.ChoosePreferredCopy(unique, movies));
        Assert.False(duplicates.ChoosePreferredCopy(clearLoser, movies));
        Assert.All(movies, movie => Assert.False(movie.IsManuallyKept));
        Assert.True(clearWinner.ApprovedForCommit);
        Assert.False(clearLoser.ApprovedForCommit);
    }

    [Fact]
    public void ContradictoryManualChoices_FallBackToReview()
    {
        var (first, second) = CreateTiedPair();
        first.IsManuallyKept = true;
        second.IsManuallyKept = true;
        var movies = new List<Movie> { first, second };

        new DuplicateService().Process(movies);

        Assert.All(movies, movie =>
        {
            Assert.True(movie.HasDuplicateTieReview);
            Assert.False(movie.ApprovedForCommit);
        });
    }

    [Fact]
    public void TieThatNoLongerExists_IsClearedWhenDuplicatesAreRecalculated()
    {
        var (first, second) = CreateTiedPair();
        var movies = new List<Movie> { first, second };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);
        Assert.True(first.HasDuplicateTieReview);

        // The second file turns out to be a different movie (for example after
        // the user enters its real IMDb ID), so the first is no longer tied.
        second.ImdbId = "tt3030303";
        duplicates.Process(movies);

        Assert.False(first.HasDuplicateTieReview);
        Assert.False(first.NeedsReview);
        Assert.False(first.IsDuplicate);
        Assert.True(first.ApprovedForCommit);
        Assert.False(second.HasDuplicateTieReview);
        Assert.True(second.ApprovedForCommit);
    }

    [Fact]
    public void TieThatStillExists_IsKeptWhenDuplicatesAreRecalculated()
    {
        var (first, second) = CreateTiedPair();
        var movies = new List<Movie> { first, second };
        var duplicates = new DuplicateService();

        duplicates.Process(movies);
        duplicates.Process(movies);

        Assert.All(movies, movie =>
        {
            Assert.True(movie.HasDuplicateTieReview);
            Assert.False(movie.ApprovedForCommit);
        });
        Assert.Equal(
            "No clear best file for Standard Version",
            first.ReviewReason);
    }

    [Fact]
    public void RecalculatingATie_KeepsReviewReasonsFromOtherStages()
    {
        var (first, second) = CreateTiedPair();
        first.RequireReview("Plex library conflict: already in Plex");
        var movies = new List<Movie> { first, second };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);

        second.ImdbId = "tt3030303";
        duplicates.Process(movies);

        Assert.False(first.HasDuplicateTieReview);
        Assert.True(first.NeedsReview);
        Assert.Equal("Plex library conflict: already in Plex", first.ReviewReason);
        Assert.False(first.ApprovedForCommit);
    }

    [Fact]
    public void ManualChoice_ChangesThePlanFingerprint()
    {
        var (first, second) = CreateTiedPair();
        var movies = new List<Movie> { first, second };
        var profile = new DestinationProfile
        {
            DestinationRoot = Path.Combine(Path.GetTempPath(), "PIM-Duplicate-Choice")
        };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);
        var before = PlanFingerprintBuilder.Build(movies, profile, LibraryGoal.OrganizeNewMovies);

        duplicates.ChoosePreferredCopy(first, movies);
        duplicates.Process(movies);

        Assert.NotEqual(
            before,
            PlanFingerprintBuilder.Build(movies, profile, LibraryGoal.OrganizeNewMovies));
    }

    private static (Movie First, Movie Second) CreateTiedPair()
    {
        return (
            CreateMovie("Tied.Movie.2020.1080p.mkv", 4000 * OneMb),
            CreateMovie("Tied.Movie.2020.BluRay.mkv", 3990 * OneMb));
    }

    private static Movie CreateMovie(string fileName, long size)
    {
        return new Movie
        {
            Title = "Tied Movie",
            Year = 2020,
            ImdbId = "tt2020202",
            FileName = fileName,
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-Duplicate-Choice", fileName),
            FileSizeBytes = size,
            MetadataFetched = true,
            MetadataMatchedByImdbId = true,
            MatchConfidence = 100
        };
    }
}
