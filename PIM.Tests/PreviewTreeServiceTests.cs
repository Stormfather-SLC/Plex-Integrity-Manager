using PIM.Core.Models;
using PIM.Web.Services;
using Xunit;

namespace PIM.Tests;

public sealed class PreviewTreeServiceTests
{
    [Fact]
    public void BuildTree_ReviewOnlyMovie_RemainsVisibleWithoutDestinationPath()
    {
        var movie = new Movie
        {
            OriginalFilePath = Path.Combine("Intake", "Unidentified Movie.mkv")
        };
        movie.RequireReview("Source identity requires human verification");

        var tree = new PreviewTreeService().BuildTree(
            new List<Movie> { movie },
            string.Empty);

        Assert.Equal("Destination not configured", tree.Name);
        var reviewSection = Assert.Single(tree.Children);
        Assert.Equal("Needs Review", reviewSection.Name);
        var reviewFile = Assert.Single(reviewSection.Children);
        Assert.Same(movie, reviewFile.Movie);
        Assert.Equal(movie.ReviewReason, reviewFile.Status);
    }

    [Fact]
    public void BuildTree_ErrorMovie_RemainsVisibleWithoutTargetPath()
    {
        var movie = new Movie
        {
            OriginalFilePath = Path.Combine("Intake", "Broken Movie.mkv"),
            ErrorMessage = "The source file could not be read."
        };

        var tree = new PreviewTreeService().BuildTree(
            new List<Movie> { movie },
            Path.Combine("Destination", "Movies"));

        var errorSection = Assert.Single(tree.Children);
        Assert.Equal("Errors", errorSection.Name);
        var errorFile = Assert.Single(errorSection.Children);
        Assert.Same(movie, errorFile.Movie);
        Assert.Equal(movie.ErrorMessage, errorFile.Status);
    }
}
