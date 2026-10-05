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
    [InlineData("Btter.Of.Ded.1985.XviD.avi", "Btter Of Ded", 1985)]
    [InlineData("Heat.1995.DivX.avi", "Heat", 1995)]
    [InlineData("Dune.2021.2160p.WEB-DL.H.265.HEVC.10bit.DTS-HD.Atmos.mkv", "Dune", 2021)]
    [InlineData("Alien.1979.REMUX.1080p.BluRay.TrueHD.AC3.mkv", "Alien", 1979)]
    [InlineData("Up.2009.PROPER.REPACK.BRRip.BDRip.x264.mkv", "Up", 2009)]
    [InlineData("Arrival.2016.AMZN.HDTV.H264.1080i.mkv", "Arrival", 2016)]
    [InlineData("Charlottes.Web.2006.mkv", "Charlottes Web", 2006)]
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

    [Theory]
    [InlineData("Wonder Woman 1984 (2020) {imdb-tt7126948}.mkv", "Wonder Woman 1984", 2020, "tt7126948")]
    [InlineData("Blade Runner 2049 (2017) {imdb-tt1856101}.mkv", "Blade Runner 2049", 2017, "tt1856101")]
    [InlineData("2000 Mules (2022) {imdb-tt18924506}.mkv", "2000 Mules", 2022, "tt18924506")]
    [InlineData("1917 (2019) {imdb-tt8579674}.m4v", "1917", 2019, "tt8579674")]
    [InlineData("2001 A Space Odyssey (1968) {imdb-tt0062622}.mkv", "2001 A Space Odyssey", 1968, "tt0062622")]
    [InlineData("Godzilla vs Kong (2021) (2021) {imdb-tt24732200}.mkv", "Godzilla vs Kong (2021)", 2021, "tt24732200")]
    [InlineData("Uptown Girls(2003){imdb-tt0263757}.mp4", "Uptown Girls", 2003, "tt0263757")]
    [InlineData("Cinderella II_ Dreams Come True (2002) {imdb-tt0291082}.mkv", "Cinderella II_ Dreams Come True", 2002, "tt0291082")]
    [InlineData("Spider-Man (2002) {imdb-tt0145487}.mkv", "Spider-Man", 2002, "tt0145487")]
    [InlineData("The Proper Web HD (2010) {imdb-tt1234567}.mkv", "The Proper Web HD", 2010, "tt1234567")]
    public void Parse_PlexStyleName_TakesTheTitleAsWritten(
        string fileName,
        string expectedTitle,
        int expectedYear,
        string expectedImdbId)
    {
        var movie = new Movie { FileName = fileName };

        _parser.Parse(movie);

        Assert.Equal(expectedTitle, movie.Title);
        Assert.Equal(expectedYear, movie.Year);
        Assert.Equal(expectedImdbId, movie.ImdbId);
        Assert.Equal(new[] { expectedYear }, movie.CandidateYears);
        Assert.Null(movie.VersionTag);
        Assert.False(movie.IsAlternateVersion);
    }

    [Theory]
    [InlineData("Blade Runner 2049 (2017) {imdb-tt1856101} [EDITED].mp4", "Blade Runner 2049", 2017, "Edited")]
    [InlineData("Gladiator (2000) {edition-Extended Edition} {imdb-tt0172495}.mkv", "Gladiator", 2000, "Extended Edition")]
    [InlineData("Wonder Woman 1984 (2020) {edition-IMAX} {imdb-tt7126948}.mkv", "Wonder Woman 1984", 2020, "IMAX")]
    public void Parse_PlexStyleName_StillDetectsAnEditionOutsideTheTitle(
        string fileName,
        string expectedTitle,
        int expectedYear,
        string expectedEdition)
    {
        var movie = new Movie { FileName = fileName };

        _parser.Parse(movie);

        Assert.Equal(expectedTitle, movie.Title);
        Assert.Equal(expectedYear, movie.Year);
        Assert.Equal(expectedEdition, movie.VersionTag);
        Assert.True(movie.IsAlternateVersion);
    }

    [Fact]
    public void Parse_PlexStyleName_WithAnEditionMarkerInsideTheTitle_IsParsedAsBefore()
    {
        var movie = new Movie { FileName = "Gladiator Extended (2000) {imdb-tt0172495}.mkv" };

        _parser.Parse(movie);

        Assert.Equal("Gladiator", movie.Title);
        Assert.Equal(2000, movie.Year);
        Assert.Equal("Extended Edition", movie.VersionTag);
    }

    [Theory]
    // No IMDb tag in the file name: numbers may still be years, as before.
    [InlineData("Wonder.Woman.1984.2020.1080p.mkv", "Wonder Woman", 2020)]
    [InlineData("Night Crossing (1981).mkv", "Night Crossing", 1981)]
    // A year range is not a release year, so this is not the Plex form.
    [InlineData("Les Misérables (2018–2019) {imdb-tt5900600}.mkv", "Les Misérables (2018–2019)", 2019)]
    public void Parse_NameThatIsNotPlexStyle_IsParsedAsBefore(
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
