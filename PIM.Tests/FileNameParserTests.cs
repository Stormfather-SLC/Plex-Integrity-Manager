using PIM.Core.Models;
using PIM.Infrastructure.Parsing;
using Xunit;

namespace PIM.Tests;

public sealed class FileNameParserTests
{
    private readonly FileNameParser _parser = new();

    [Fact]
    public void Parse_UsesImdbIdAndYearFromParentFolder_WhenFileOmitsYear()
    {
        var movie = new Movie
        {
            FileName = "A Quiet Place 4K UHD.mp4",
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "PG-13",
                "A Quiet Place (2018) {imdb-tt6644200}")
        };

        _parser.Parse(movie);

        Assert.Equal("tt6644200", movie.ImdbId);
        Assert.Equal(2018, movie.Year);
        Assert.Equal("A Quiet Place", movie.Title);
    }

    [Fact]
    public void Parse_PrefersFilenameYear_OverParentFolderYear()
    {
        var movie = new Movie
        {
            FileName = "Dune 2021 2160p.mkv",
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "Dune (1984) {imdb-tt0087182}")
        };

        _parser.Parse(movie);

        Assert.Equal(2021, movie.Year);
        Assert.Equal("tt0087182", movie.ImdbId);
    }

    [Theory]
    [InlineData("Inception.2010.1080p.mkv", "Inception", 2010)]
    [InlineData("Interstellar.2014.720p.mkv", "Interstellar", 2014)]
    [InlineData("The.Dark.Knight.2008.mkv", "The Dark Knight", 2008)]
    public void Parse_BasicTitleYearFiles_ProducesOmdbLookupInput(
        string fileName,
        string expectedTitle,
        int expectedYear)
    {
        var movie = new Movie { FileName = fileName };

        _parser.Parse(movie);

        Assert.Equal(expectedTitle, movie.Title);
        Assert.Equal(expectedYear, movie.Year);
    }

    [Fact]
    public void Parse_TrailingArticleTitle_NormalizesForOmdbLookup()
    {
        var movie = new Movie
        {
            FileName = "Matrix Resurrections, The 2021 - Edited .mp4"
        };

        _parser.Parse(movie);

        Assert.Equal("The Matrix Resurrections", movie.Title);
        Assert.Equal(2021, movie.Year);
        Assert.Equal("Edited", movie.VersionTag);
    }

    [Fact]
    public void Parse_MultipleEditionMarkers_RemovesAllMarkersFromLookupTitle()
    {
        var movie = new Movie
        {
            FileName = "Gladiator 2000 - Edited - Extended.mp4"
        };

        _parser.Parse(movie);

        Assert.Equal("Gladiator", movie.Title);
        Assert.Equal(2000, movie.Year);
        Assert.True(movie.IsAlternateVersion);
        Assert.Equal("Edited + Extended Edition", movie.VersionTag);
    }
}
