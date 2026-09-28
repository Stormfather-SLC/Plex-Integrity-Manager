using PIM.Core.Models;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

public sealed class MovieResultRowTests
{
    private const string SourceRoot = @"D:\Intake\Source";
    private const string DestinationRoot = @"D:\Intake\Destination";

    [Fact]
    public void Error_ShowsTheErrorMessage()
    {
        var movie = Identified();
        movie.ErrorMessage = "The source file could not be read.";

        var row = Row(movie);

        Assert.Equal(MovieResultState.Error, row.State);
        Assert.Equal("The source file could not be read.", row.Summary);
    }

    [Fact]
    public void ReviewWithASuggestion_IsADecisionShowingTheSuggestedMovie()
    {
        var movie = Unidentified("Gladiator", 2001);
        movie.SuggestedTitle = "Gladiator Eroticvs: The Lesbian Warriors";
        movie.SuggestedYear = 2001;
        movie.SuggestedImdbId = "tt0256056";
        movie.SetMetadataReview("Low confidence metadata match (21% confidence)", MetadataLookupFailureType.LowConfidence);

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.True(row.CanAcceptSuggestion);
        Assert.True(row.CanEnterImdbId);
        Assert.Equal("Possible match: Gladiator Eroticvs: The Lesbian Warriors (2001)", row.Summary);
    }

    [Fact]
    public void UnidentifiedWithoutASuggestion_IsADecisionViaTheImdbBox()
    {
        var movie = Unidentified("Btter Of Ded", 1985);
        movie.SetMetadataReview("IMDb ID could not be determined", MetadataLookupFailureType.MovieNotFound);

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.False(row.CanAcceptSuggestion);
        Assert.True(row.CanEnterImdbId);
        Assert.Equal("IMDb ID could not be determined", row.Summary);
    }

    [Fact]
    public void ReviewWithNoWayToResolveItOnThePage_IsBlocked()
    {
        var movie = Identified();
        movie.ApprovedForCommit = false;
        movie.HasDestinationConflict = true;
        movie.RequireReview("Destination conflict: The target file already exists.");

        var row = Row(movie);

        Assert.Equal(MovieResultState.Blocked, row.State);
        Assert.Equal("Destination conflict: The target file already exists.", row.Summary);
    }

    [Fact]
    public void PossiblePlexDuplicate_IsADecisionShowingThePlexCopy()
    {
        var movie = Identified();
        movie.ApprovedForCommit = false;
        movie.IsPossiblePlexDuplicate = true;
        movie.HasPlexLibraryConflict = true;
        movie.ExistingPlexResolution = "1080p";
        movie.ExistingPlexSizeBytes = 8_804_682_956;
        movie.RequireReview("Plex library conflict: Possible duplicate.");

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.True(row.CanAddPlexDuplicate);
        Assert.Equal("Possible duplicate: Plex already has this movie (1080p, 8.2 GB)", row.Summary);
    }

    [Fact]
    public void DuplicateTie_IsADecision()
    {
        var movie = Identified();
        movie.ApprovedForCommit = false;
        movie.RequireReview("No clear best file for Standard Version");

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.True(row.CanKeepThisCopy);
        Assert.StartsWith("Tied with another copy", row.Summary);
    }

    [Fact]
    public void ExtraReasons_AreCountedForTheDetailsHint()
    {
        var movie = Identified();
        movie.ApprovedForCommit = false;
        movie.RequireReview("No clear best file for Standard Version");
        movie.RequireReview("Destination conflict: Multiple incoming files resolve to the same target path.");

        var row = Row(movie);

        Assert.Equal(2, row.Reasons.Count);
        Assert.Equal(1, row.AdditionalReasonCount);
    }

    [Fact]
    public void ReadyMovie_ShowsItsShortDestination()
    {
        var movie = Identified();

        var row = Row(movie);

        Assert.Equal(MovieResultState.Ready, row.State);
        Assert.Equal("Ready to move", row.Summary);
        Assert.Equal(@"Inception (2010)\Inception.2010.mkv", row.SourceDisplay);
        Assert.Equal(
            @"Inception (2010) {imdb-tt1375666}\Inception (2010) {imdb-tt1375666}.mkv",
            row.TargetDisplay);
        Assert.Equal(0, row.AdditionalReasonCount);
    }

    [Fact]
    public void AcceptedPlexDuplicate_IsReadyAndCanBeUndone()
    {
        var movie = Identified();
        movie.IsPossiblePlexDuplicate = true;
        movie.ExistingPlexLibraryPath = @"G:\Plex\Inception.mkv";
        movie.PlexDuplicateAcceptedPath = @"G:\Plex\Inception.mkv";

        var row = Row(movie);

        Assert.Equal(MovieResultState.Ready, row.State);
        Assert.True(row.CanUndoPlexDuplicate);
        Assert.Contains("alongside the existing Plex copy", row.Summary);
    }

    [Fact]
    public void UnchosenDuplicateCopy_IsSkipped()
    {
        var movie = Identified();
        movie.ApprovedForCommit = false;
        movie.IsDuplicate = true;

        var row = Row(movie);

        Assert.Equal(MovieResultState.Skipped, row.State);
        Assert.Contains("not deleted", row.Summary);
    }

    [Fact]
    public void ScannedButNotIdentified_IsPending()
    {
        var movie = new Movie
        {
            FileName = "Inception.2010.mkv",
            OriginalFilePath = Path.Combine(SourceRoot, "Inception.2010.mkv"),
            Title = "Inception",
            Year = 2010,
            Status = "Discovered"
        };

        var row = Row(movie);

        Assert.Equal(MovieResultState.Pending, row.State);
        Assert.Contains("Identify Movies", row.Summary);
    }

    [Fact]
    public void MissingTitle_FallsBackToTheFileName()
    {
        var movie = Unidentified(null, 1986);
        movie.FileName = "_ (1986) {imdb-tt900000007}.mkv";
        movie.SetMetadataReview(
            "The provided IMDb ID could not be found, and no title was available for recovery.",
            MetadataLookupFailureType.MovieNotFound);

        var row = Row(movie);

        Assert.Equal("_ (1986) {imdb-tt900000007}.mkv", row.DisplayTitle);
    }

    [Theory]
    [InlineData(@"D:\Intake\Source", @"D:\Intake\Source\A\movie.mkv", @"A\movie.mkv")]
    [InlineData(@"D:\Intake\Source\", @"d:\intake\source\movie.mkv", "movie.mkv")]
    [InlineData(@"D:\Intake\Source", @"D:\Intake\SourceOther\movie.mkv", @"D:\Intake\SourceOther\movie.mkv")]
    [InlineData(null, @"D:\Intake\Source\movie.mkv", @"D:\Intake\Source\movie.mkv")]
    [InlineData(@"D:\Intake\Source", null, null)]
    public void RelativeTo_ShortensOnlyPathsInsideTheRoot(string? root, string? path, string? expected)
    {
        Assert.Equal(expected, MovieResultRow.RelativeTo(root, path));
    }

    [Fact]
    public void Rows_ListDecisionsFirst()
    {
        var ready = Row(Identified());
        var blocked = Identified();
        blocked.ApprovedForCommit = false;
        blocked.RequireReview("Destination conflict: exists.");
        var decide = Unidentified("Btter Of Ded", 1985);
        decide.SetMetadataReview("IMDb ID could not be determined", MetadataLookupFailureType.MovieNotFound);

        var ordered = new[] { ready, Row(blocked), Row(decide) }
            .OrderBy(row => row.SortOrder)
            .Select(row => row.State)
            .ToList();

        Assert.Equal(
            new[] { MovieResultState.Decide, MovieResultState.Blocked, MovieResultState.Ready },
            ordered);
    }

    private static MovieResultRow Row(Movie movie) =>
        MovieResultRow.Create(movie, SourceRoot, DestinationRoot);

    private static Movie Identified()
    {
        return new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666",
            FileName = "Inception.2010.mkv",
            OriginalFilePath = Path.Combine(SourceRoot, "Inception (2010)", "Inception.2010.mkv"),
            TargetPath = Path.Combine(
                DestinationRoot,
                "Inception (2010) {imdb-tt1375666}",
                "Inception (2010) {imdb-tt1375666}.mkv"),
            FileSizeBytes = 2_000_000_000,
            MetadataFetched = true,
            MetadataMatchedByImdbId = true,
            MatchConfidence = 100,
            ApprovedForCommit = true
        };
    }

    private static Movie Unidentified(string? title, int? year)
    {
        return new Movie
        {
            Title = title,
            Year = year,
            FileName = $"{title}.{year}.mkv",
            OriginalFilePath = Path.Combine(SourceRoot, $"{title}.{year}.mkv")
        };
    }
}
