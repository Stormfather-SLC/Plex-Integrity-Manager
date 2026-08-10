using PIM.Core.Models;
using PIM.Web.Services;
using Xunit;

namespace PIM.Tests;

public sealed class PreviewTreeServiceTests
{
    [Fact]
    public void BuildTree_ReviewOnlyListWithoutDestination_ShowsNeedsReviewSection()
    {
        var movie = new Movie
        {
            FileName = "Unidentified Movie.mkv",
            OriginalFilePath = Path.Combine(
                "intake",
                "Unidentified Movie.mkv")
        };
        movie.RequireReview("IMDb ID could not be determined");

        var tree = new PreviewTreeService().BuildTree(
            new List<Movie> { movie },
            string.Empty);

        Assert.Equal("Destination not configured", tree.Name);
        Assert.Equal(1, tree.FileCount);

        var reviewSection = Assert.Single(tree.Children);
        Assert.Equal("Needs Review", reviewSection.Name);
        Assert.Equal("Needs Review", reviewSection.Status);

        var reviewFile = Assert.Single(reviewSection.Children);
        Assert.Equal("Unidentified Movie.mkv", reviewFile.Name);
        Assert.Same(movie, reviewFile.Movie);
        Assert.Equal("IMDb ID could not be determined", reviewFile.Status);
    }
}
