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
        movie.MatchConfidence = 21;
        movie.SetMetadataReview("Low confidence metadata match (21% confidence)", MetadataLookupFailureType.LowConfidence);

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.True(row.CanAcceptSuggestion);
        Assert.True(row.IsLowConfidenceSuggestion);
        Assert.True(row.CanEnterImdbId);
        Assert.Equal(
            "Possible match: Gladiator Eroticvs: The Lesbian Warriors (2001), 21% match",
            row.Summary);
    }

    [Fact]
    public void ConfidentSuggestion_IsNotFlaggedAsLowConfidence()
    {
        var movie = Unidentified("Back to the Future", 1986);
        movie.SuggestedTitle = "Back to the Future";
        movie.SuggestedYear = 1985;
        movie.SuggestedImdbId = "tt0088763";
        movie.MatchConfidence = 90;
        movie.SetMetadataReview("Possible OMDb match: Back to the Future (1985)", MetadataLookupFailureType.FuzzyCandidateYearConflict);

        var row = Row(movie);

        Assert.True(row.CanAcceptSuggestion);
        Assert.False(row.IsLowConfidenceSuggestion);
        Assert.Equal("Possible match: Back to the Future (1985), 90% match; file says 1986", row.Summary);

        // Accepting an identity is always confirmed, even at high confidence.
        Assert.Equal(
            "Accept \"Back to the Future (1985)\", tt0088763 as the identity of Back to the Future.1986.mkv? The file says 1986.",
            row.AcceptConfirmation);
    }

    [Fact]
    public void LowConfidenceConfirmation_StatesTheScore()
    {
        var movie = Unidentified("Gladiator", 2001);
        movie.SuggestedTitle = "Gladiator Eroticvs: The Lesbian Warriors";
        movie.SuggestedYear = 2001;
        movie.SuggestedImdbId = "tt0256056";
        movie.MatchConfidence = 21;
        movie.SetMetadataReview("Low confidence metadata match (21% confidence)", MetadataLookupFailureType.LowConfidence);

        Assert.StartsWith("This is only a 21% match.", Row(movie).AcceptConfirmation);
    }

    [Fact]
    public void NoSuggestionToAccept_HasNoConfirmation()
    {
        Assert.Null(Row(PossiblePlexDuplicate()).AcceptConfirmation);
    }

    [Fact]
    public void IdentifiedPossibleDuplicate_DoesNotOfferAcceptMatch()
    {
        // OMDb also stores a confirmed identity in the suggestion fields.
        var movie = PossiblePlexDuplicate();
        movie.SuggestedTitle = movie.Title;
        movie.SuggestedYear = movie.Year;
        movie.SuggestedImdbId = movie.ImdbId;

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.True(row.CanAddPlexDuplicate);
        Assert.False(row.CanAcceptSuggestion);
        Assert.False(row.CanEnterImdbId);
        Assert.StartsWith("Possible duplicate", row.Summary);
    }

    [Fact]
    public void UnconfirmedIdentity_ComesBeforeADuplicateDecision()
    {
        var movie = PossiblePlexDuplicate();
        movie.Title = "Pulp Ficton";
        movie.SuggestedTitle = "Pulp Fiction";
        movie.SuggestedYear = 1994;
        movie.SuggestedImdbId = "tt0110912";
        movie.SetMetadataReview(
            "The provided IMDb ID does not match the movie title provided.",
            MetadataLookupFailureType.ImdbIdentityConflict);

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.True(row.IsIdentityUnconfirmed);
        Assert.True(row.CanAcceptSuggestion);
        Assert.False(row.CanAddPlexDuplicate);
        Assert.StartsWith("Possible match: Pulp Fiction (1994)", row.Summary);
    }

    [Fact]
    public void BadgeClasses_WorkWithTheBundledBootstrapVersion()
    {
        var rows = new[]
        {
            Row(Identified()),
            Row(PossiblePlexDuplicate()),
            Row(new Movie { OriginalFilePath = Path.Combine(SourceRoot, "x.mkv"), ErrorMessage = "x" })
        };

        Assert.All(rows, row =>
        {
            Assert.DoesNotContain("text-bg-", row.BadgeClass);
            Assert.StartsWith("bg-", row.BadgeClass);
        });
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

    [Theory]
    [InlineData(MetadataLookupFailureType.NetworkFailure)]
    [InlineData(MetadataLookupFailureType.HttpFailure)]
    [InlineData(MetadataLookupFailureType.Timeout)]
    [InlineData(MetadataLookupFailureType.RequestLimitReached)]
    [InlineData(MetadataLookupFailureType.InvalidApiKey)]
    public void LookupThatGotNoAnswer_SaysHowToTryAgain(MetadataLookupFailureType failure)
    {
        var movie = Unidentified("Some Film", 2004);
        movie.SetMetadataReview("OMDb lookup failed: no answer", failure);

        var row = Row(movie);

        Assert.Equal(MovieResultState.Decide, row.State);
        Assert.Equal(
            "OMDb lookup failed: no answer; run Identify Movies to try again",
            row.Summary);
    }

    [Theory]
    [InlineData(MetadataLookupFailureType.MovieNotFound)]
    [InlineData(MetadataLookupFailureType.LowConfidence)]
    [InlineData(MetadataLookupFailureType.MissingRequiredFields)]
    public void LookupThatWasAnswered_DoesNotSuggestTryingAgain(MetadataLookupFailureType failure)
    {
        var movie = Unidentified("Some Film", 2004);
        movie.SetMetadataReview("IMDb ID could not be determined", failure);

        Assert.Equal("IMDb ID could not be determined", Row(movie).Summary);
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

    private static Movie PossiblePlexDuplicate()
    {
        var movie = Identified();
        movie.ApprovedForCommit = false;
        movie.IsPossiblePlexDuplicate = true;
        movie.HasPlexLibraryConflict = true;
        movie.ExistingPlexResolution = "1080p";
        movie.ExistingPlexSizeBytes = 7_200_000_000;
        movie.RequireReview("Plex library conflict: Possible duplicate.");
        return movie;
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
