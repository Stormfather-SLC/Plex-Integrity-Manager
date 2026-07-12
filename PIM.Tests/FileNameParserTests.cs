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
}
