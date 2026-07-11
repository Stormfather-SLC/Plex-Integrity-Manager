using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class DestinationPathBuilderTests
{
    private readonly DestinationPathBuilder _builder = new();

    [Fact]
    public void Build_RatingThenGenre_UsesConfiguredOrder()
    {
        var profile = CreateProfile(
            OrganizationLevelType.MpaRating,
            OrganizationLevelType.PrimaryGenre);
        var movie = CreateMovie();

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mkv");

        Assert.Equal(new[] { "PG", "Comedy" }, result.OrganizationSegments);
        Assert.EndsWith(
            Path.Combine(
                "PG",
                "Comedy",
                "Better Off Dead (1985) {imdb-tt0088794}",
                "Better Off Dead (1985) {imdb-tt0088794}.mkv"),
            result.FullFilePath);
    }

    [Fact]
    public void Build_GenreThenRating_ReversesTheFolderOrder()
    {
        var profile = CreateProfile(
            OrganizationLevelType.PrimaryGenre,
            OrganizationLevelType.MpaRating);
        var movie = CreateMovie();

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mp4");

        Assert.Equal(new[] { "Comedy", "PG" }, result.OrganizationSegments);
    }

    [Fact]
    public void Build_FlatProfile_CreatesMovieFolderDirectlyUnderRoot()
    {
        var profile = CreateProfile();
        var movie = CreateMovie();

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mp4");

        Assert.Empty(result.OrganizationSegments);
        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(profile.DestinationRoot),
                "Better Off Dead (1985) {imdb-tt0088794}",
                "Better Off Dead (1985) {imdb-tt0088794}.mp4"),
            result.FullFilePath);
    }

    [Fact]
    public void Build_CategoryThenAlphabeticalRange_UsesSortTitleForLeadingArticle()
    {
        var profile = CreateProfile();
        profile.OrganizationLevels.Add(new DestinationOrganizationLevel
        {
            Type = OrganizationLevelType.LibraryCategory,
            Value = "01-Kids & Family"
        });
        profile.OrganizationLevels.Add(
            DestinationOrganizationLevel.Create(
                OrganizationLevelType.AlphabeticalRange));

        var movie = CreateMovie();
        movie.Title = "The Absent Minded Professor";
        movie.Year = 1961;
        movie.ImdbId = "tt0054594";

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mp4");

        Assert.Equal(
            new[] { "01-Kids & Family", "A-C" },
            result.OrganizationSegments);
    }

    [Fact]
    public void Build_PreserveSourceFolders_UsesOnlyPathRelativeToSourceRoot()
    {
        var sourceRoot = CreateSourceRoot();
        var sourceDirectory = Path.Combine(
            sourceRoot,
            "01-Kids & Family",
            "A-C");

        var profile = CreateProfile(
            OrganizationLevelType.PreserveSourceFolders);
        var movie = CreateMovie();
        movie.DirectoryPath = sourceDirectory;
        movie.OriginalFilePath = Path.Combine(
            sourceDirectory,
            "Better.Off.Dead.1985.mp4");

        var result = _builder.Build(movie, profile, sourceRoot, ".mp4");

        Assert.Equal(
            new[] { "01-Kids & Family", "A-C" },
            result.OrganizationSegments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("NR")]
    [InlineData("Not Rated")]
    [InlineData("Unrated")]
    public void Build_MissingOrUnratedRating_UsesConfiguredFallback(string? rating)
    {
        var profile = CreateProfile(
            OrganizationLevelType.MpaRating);
        profile.OrganizationLevels[0].UnknownFolderName = "Unrated";

        var movie = CreateMovie();
        movie.MpaRating = rating;

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mp4");

        Assert.Equal("Unrated", Assert.Single(result.OrganizationSegments));
    }

    [Fact]
    public void Build_MissingGenre_UsesOtherFallback()
    {
        var profile = CreateProfile(
            OrganizationLevelType.PrimaryGenre);
        var movie = CreateMovie();
        movie.PrimaryGenre = null;
        movie.Genres.Clear();

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mp4");

        Assert.Equal("Other", Assert.Single(result.OrganizationSegments));
    }

    [Fact]
    public void Build_UnsafeLiteralFolder_RemainsInsideDestinationRoot()
    {
        var profile = CreateProfile();
        profile.OrganizationLevels.Add(new DestinationOrganizationLevel
        {
            Type = OrganizationLevelType.FixedFolder,
            Value = @"..\Outside:Folder"
        });

        var movie = CreateMovie();
        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mp4");
        var root = Path.GetFullPath(profile.DestinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.StartsWith(
            root + Path.DirectorySeparatorChar,
            result.FullFilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Outside:Folder", result.FullFilePath);
    }

    [Fact]
    public void Build_AlternateEdition_KeepsSharedMovieFolderAndEditionFileName()
    {
        var profile = CreateProfile();
        var movie = CreateMovie();
        movie.VersionTag = "Extended Edition";
        movie.IsAlternateVersion = true;

        var result = _builder.Build(movie, profile, CreateSourceRoot(), ".mkv");

        Assert.Equal(
            "Better Off Dead (1985) {imdb-tt0088794}",
            result.MovieFolderName);
        Assert.Equal(
            "Better Off Dead (1985) {edition-Extended Edition} {imdb-tt0088794}.mkv",
            result.FileName);
    }

    private static DestinationProfile CreateProfile(
        params OrganizationLevelType[] levelTypes)
    {
        var profile = new DestinationProfile
        {
            Name = "Test Profile",
            DestinationRoot = Path.Combine(
                Path.GetTempPath(),
                "PIM-Destination-Tests",
                Guid.NewGuid().ToString("N"))
        };

        foreach (var levelType in levelTypes)
        {
            profile.OrganizationLevels.Add(
                DestinationOrganizationLevel.Create(levelType));
        }

        return profile;
    }

    private static Movie CreateMovie()
    {
        var sourceRoot = CreateSourceRoot();

        return new Movie
        {
            Title = "Better Off Dead",
            Year = 1985,
            ImdbId = "tt0088794",
            MpaRating = "PG",
            Genres = new List<string> { "Comedy", "Romance" },
            PrimaryGenre = "Comedy",
            DirectoryPath = sourceRoot,
            OriginalFilePath = Path.Combine(
                sourceRoot,
                "Better.Off.Dead.1985.mp4"),
            FileName = "Better.Off.Dead.1985.mp4"
        };
    }

    private static string CreateSourceRoot()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "PIM-Source-Tests",
            Guid.NewGuid().ToString("N"));
    }
}
