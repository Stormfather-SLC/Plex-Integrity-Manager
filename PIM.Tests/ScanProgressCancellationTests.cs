using PIM.Core.Models;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

public sealed class ScanProgressCancellationTests
{
    [Fact]
    public void NothingRunning_CannotBeCancelled()
    {
        var progress = new ScanProgress();

        Assert.False(progress.CanCancel);
        Assert.False(progress.RequestCancel());
        Assert.False(progress.CancelRequested);
    }

    [Fact]
    public void CancellableOperation_CanBeCancelledOnce_ThenEnds()
    {
        var progress = new ScanProgress();
        var token = progress.BeginCancellableOperation();

        Assert.True(progress.CanCancel);
        Assert.True(progress.RequestCancel());
        Assert.True(token.IsCancellationRequested);
        Assert.True(progress.CancelRequested);
        Assert.False(progress.CanCancel);
        Assert.False(progress.RequestCancel());

        progress.EndCancellableOperation();

        Assert.False(progress.CanCancel);
        Assert.False(progress.CancelRequested);
    }

    [Fact]
    public void NewOperation_StartsWithAFreshToken()
    {
        var progress = new ScanProgress();
        progress.BeginCancellableOperation();
        progress.RequestCancel();
        progress.EndCancellableOperation();

        var token = progress.BeginCancellableOperation();

        Assert.False(token.IsCancellationRequested);
        Assert.True(progress.CanCancel);
    }

    [Fact]
    public void MovieNeverLookedUp_ShowsAsNotIdentifiedEvenWithAnImdbTagInItsName()
    {
        var movie = new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666",
            FileName = "Inception (2010) {imdb-tt1375666}.mkv",
            OriginalFilePath = @"D:\Intake\Source\Inception (2010) {imdb-tt1375666}.mkv",
            TargetPath = @"D:\Intake\Destination\Inception (2010) {imdb-tt1375666}\Inception (2010) {imdb-tt1375666}.mkv",
            ApprovedForCommit = true
        };

        var row = MovieResultRow.Create(movie, @"D:\Intake\Source", @"D:\Intake\Destination");

        Assert.Equal(MovieResultState.Pending, row.State);
        Assert.Contains("Identify Movies", row.Summary);

        movie.MetadataFetched = true;
        movie.MetadataMatchOrigin = MetadataMatchOrigin.ImdbId;

        Assert.Equal(
            MovieResultState.Ready,
            MovieResultRow.Create(movie, @"D:\Intake\Source", @"D:\Intake\Destination").State);
    }
}
