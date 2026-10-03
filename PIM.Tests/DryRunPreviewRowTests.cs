using PIM.Core.Models;
using PIM.Infrastructure.Services;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

public sealed class DryRunPreviewRowTests
{
    private const string SourceRoot = @"D:\Intake\Source";
    private const string DestinationRoot = @"D:\Intake\Destination";

    [Fact]
    public void Move_ShowsTheShortDestination()
    {
        var row = Row(Ready());

        Assert.Equal("move", row.Category);
        Assert.Equal("Move", row.BadgeLabel);
        Assert.Equal("Inception (2010)", row.DisplayTitle);
        Assert.Equal(@"Inception.2010.mkv", row.SourceDisplay);
        Assert.Equal(@"Inception (2010) {imdb-tt1375666}\Inception (2010) {imdb-tt1375666}.mkv", row.TargetDisplay);
        Assert.Equal("Ready", row.Note);
        Assert.Null(row.MovieDetailsId);
    }

    [Fact]
    public void AcceptedPlexDuplicate_SaysItIsYourDecision()
    {
        var movie = Ready();
        movie.IsPossiblePlexDuplicate = true;
        movie.ExistingPlexLibraryPath = @"G:\Plex\Inception.mkv";
        movie.PlexDuplicateAcceptedPath = @"G:\Plex\Inception.mkv";

        Assert.Equal("Added alongside the existing Plex copy (your decision)", Row(movie).Note);
    }

    [Fact]
    public void Review_ShowsTheFirstReasonAndLinksToMovieResults()
    {
        var movie = Ready();
        movie.ApprovedForCommit = false;
        movie.RequireReview("Plex library conflict: Possible duplicate.");
        movie.RequireReview("Destination conflict: exists.");

        var row = Row(movie);

        Assert.Equal("review", row.Category);
        Assert.Equal("Review", row.BadgeLabel);
        Assert.Equal("Plex library conflict: Possible duplicate.", row.Note);
        Assert.Equal(1, row.AdditionalReasonCount);
        Assert.Null(row.TargetDisplay);
        Assert.Equal($"details-{movie.Id:N}", row.MovieDetailsId);
    }

    [Fact]
    public void Error_ShowsTheErrorAndLinksToMovieResults()
    {
        var movie = Ready();
        movie.ErrorMessage = "The source file could not be read.";

        var row = Row(movie);

        Assert.Equal("error", row.Category);
        Assert.Equal("The source file could not be read.", row.Note);
        Assert.NotNull(row.MovieDetailsId);
    }

    [Fact]
    public void UnchosenDuplicate_IsLeftInPlace()
    {
        var movie = Ready();
        movie.ApprovedForCommit = false;
        movie.IsDuplicate = true;

        var row = Row(movie);

        Assert.Equal("duplicate", row.Category);
        Assert.Equal("Skip", row.BadgeLabel);
        Assert.Contains("not deleted", row.Note);
    }

    [Fact]
    public void PreviewSavedBeforeMovieIdsExisted_HasNoLink()
    {
        var item = new DryRunPreviewItem
        {
            Action = "Needs Review",
            ReviewReason = "IMDb ID could not be determined",
            FileName = "Btter.Of.Ded.1985.avi",
            OriginalFilePath = Path.Combine(SourceRoot, "Btter.Of.Ded.1985.avi")
        };

        var row = DryRunPreviewRow.Create(item, SourceRoot, DestinationRoot);

        Assert.Null(row.MovieDetailsId);
        Assert.Equal("Btter.Of.Ded.1985.avi", row.DisplayTitle);
    }

    [Fact]
    public void BadgeClasses_WorkWithTheBundledBootstrapVersion()
    {
        Assert.All(
            new[] { Row(Ready()) },
            row => Assert.DoesNotContain("text-bg-", row.BadgeClass));
    }

    private static DryRunPreviewRow Row(Movie movie)
    {
        var item = new DryRunPreviewService()
            .BuildPreview(new[] { movie })
            .Items
            .Single();

        Assert.Equal(movie.Id, item.MovieId);

        return DryRunPreviewRow.Create(item, SourceRoot, DestinationRoot);
    }

    private static Movie Ready()
    {
        return new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666",
            FileName = "Inception.2010.mkv",
            OriginalFilePath = Path.Combine(SourceRoot, "Inception.2010.mkv"),
            TargetPath = Path.Combine(
                DestinationRoot,
                "Inception (2010) {imdb-tt1375666}",
                "Inception (2010) {imdb-tt1375666}.mkv"),
            ApprovedForCommit = true,
            Status = "Rename preview generated"
        };
    }
}
