using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class SuspiciousFileNameTests
{
    private static readonly string LongFileName =
        "Long.Movie.2020." + new string('x', 110) + ".mkv";

    [Fact]
    public void LongFileName_NeedsReviewUntilConfirmed()
    {
        var movie = CreateMovie(LongFileName);
        var movies = new List<Movie> { movie };
        var duplicates = new DuplicateService();

        duplicates.Process(movies);
        Assert.True(movie.HasSuspiciousFileNameReview);
        Assert.False(movie.ApprovedForCommit);

        movie.FileNameConfirmed = true;
        duplicates.Process(movies);

        Assert.False(movie.HasSuspiciousFileNameReview);
        Assert.False(movie.NeedsReview);
        Assert.True(movie.ApprovedForCommit);
    }

    [Fact]
    public void ConfirmingTheName_KeepsEveryOtherReviewReason()
    {
        var movie = CreateMovie(LongFileName);
        movie.RequireReview("Plex library conflict: already in Plex");
        var movies = new List<Movie> { movie };
        var duplicates = new DuplicateService();
        duplicates.Process(movies);

        movie.FileNameConfirmed = true;
        duplicates.Process(movies);

        Assert.False(movie.HasSuspiciousFileNameReview);
        Assert.True(movie.NeedsReview);
        Assert.Equal("Plex library conflict: already in Plex", movie.ReviewReason);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public void ConfirmingTheName_DoesNotOverrideLowConfidenceIdentity()
    {
        var movie = CreateMovie(LongFileName);
        movie.MetadataMatchedByImdbId = false;
        movie.MatchConfidence = 60;
        movie.FileNameConfirmed = true;

        new DuplicateService().Process(new List<Movie> { movie });

        Assert.False(movie.HasSuspiciousFileNameReview);
        Assert.True(movie.NeedsReview);
        Assert.StartsWith("Low confidence metadata match", movie.ReviewReason);
        Assert.False(movie.ApprovedForCommit);
    }

    [Fact]
    public void NormalLengthName_IsNeverFlagged()
    {
        var movie = CreateMovie("Normal.Movie.2020.1080p.mkv");

        new DuplicateService().Process(new List<Movie> { movie });

        Assert.False(movie.HasSuspiciousFileNameReview);
        Assert.True(movie.ApprovedForCommit);
    }

    [Fact]
    public void ConfirmingTheName_ChangesThePlanFingerprint()
    {
        var movie = CreateMovie(LongFileName);
        var profile = new DestinationProfile
        {
            DestinationRoot = Path.Combine(Path.GetTempPath(), "PIM-Suspicious-Name")
        };
        var before = PlanFingerprintBuilder.Build(new[] { movie }, profile, LibraryGoal.OrganizeNewMovies);

        movie.FileNameConfirmed = true;

        Assert.NotEqual(
            before,
            PlanFingerprintBuilder.Build(new[] { movie }, profile, LibraryGoal.OrganizeNewMovies));
    }

    private static Movie CreateMovie(string fileName)
    {
        return new Movie
        {
            Title = "Long Movie",
            Year = 2020,
            ImdbId = "tt5050505",
            FileName = fileName,
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-Suspicious-Name", fileName),
            FileSizeBytes = 1000,
            MetadataFetched = true,
            MetadataMatchedByImdbId = true,
            MatchConfidence = 100
        };
    }
}
