using PIM.Core.Models;
using PIM.Web.Pages;
using Xunit;

namespace PIM.Tests;

public sealed class LiveCommitStateTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoMovies_IsDisabled()
    {
        var profile = CreateProfile();

        var state = LiveCommitState.Evaluate(
            new List<Movie>(),
            null,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.False(state.Enabled);
        Assert.Equal(0, state.FilesToMove);
        Assert.Contains("Scan and identify", state.Message);
    }

    [Fact]
    public void NoDryRun_IsDisabled()
    {
        var profile = CreateProfile();
        var movies = new List<Movie> { CreateReadyMovie(profile) };

        var state = LiveCommitState.Evaluate(
            movies,
            null,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.False(state.Enabled);
        Assert.Null(state.ExpiresUtc);
        Assert.Contains("Run a dry run first", state.Message);
    }

    [Fact]
    public void ExpiredDryRun_IsDisabled()
    {
        var profile = CreateProfile();
        var movies = new List<Movie> { CreateReadyMovie(profile) };
        var approval = Approve(movies, profile, Now - DryRunApproval.MaxAge);

        var state = LiveCommitState.Evaluate(
            movies,
            approval,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.False(state.Enabled);
        Assert.Contains("Run a dry run first", state.Message);
    }

    [Fact]
    public void PlanChangedSinceDryRun_IsDisabled()
    {
        var profile = CreateProfile();
        var movie = CreateReadyMovie(profile);
        var movies = new List<Movie> { movie };
        var approval = Approve(movies, profile, Now.AddMinutes(-5));

        movie.TargetPath = Path.Combine(profile.DestinationRoot, "Somewhere Else.mkv");

        var state = LiveCommitState.Evaluate(
            movies,
            approval,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.False(state.Enabled);
        Assert.Contains("Things changed since the last dry run", state.Message);
    }

    [Fact]
    public void WorkflowChangedSinceDryRun_IsDisabled()
    {
        var profile = CreateProfile();
        var movies = new List<Movie> { CreateReadyMovie(profile) };
        var approval = Approve(movies, profile, Now.AddMinutes(-5));

        var state = LiveCommitState.Evaluate(
            movies,
            approval,
            profile,
            LibraryGoal.ReorganizationMigration,
            Now);

        Assert.False(state.Enabled);
    }

    [Fact]
    public void DryRunWithNothingToMove_IsDisabled()
    {
        var profile = CreateProfile();
        var movie = CreateReadyMovie(profile);
        movie.RequireReview("Needs a person to decide");
        var movies = new List<Movie> { movie };
        var approval = Approve(movies, profile, Now.AddMinutes(-1));

        var state = LiveCommitState.Evaluate(
            movies,
            approval,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.False(state.Enabled);
        Assert.Equal(0, state.FilesToMove);
        Assert.Contains("nothing to move", state.Message);
    }

    [Fact]
    public void MatchingDryRun_IsEnabled_CountsOnlyMovableFiles_AndExpiresWithTheApproval()
    {
        var profile = CreateProfile();
        var ready = CreateReadyMovie(profile, "Ready.Movie.2024.mkv");
        var alsoReady = CreateReadyMovie(profile, "Also.Ready.2024.mkv");
        var review = CreateReadyMovie(profile, "Review.Movie.2024.mkv");
        review.RequireReview("Needs a person to decide");
        var movies = new List<Movie> { ready, alsoReady, review };
        var approvedAt = Now.AddMinutes(-12);
        var approval = Approve(movies, profile, approvedAt);

        var state = LiveCommitState.Evaluate(
            movies,
            approval,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.True(state.Enabled);
        Assert.Equal(2, state.FilesToMove);
        Assert.Equal(approvedAt, state.ApprovedUtc);
        Assert.Equal(approvedAt + DryRunApproval.MaxAge, state.ExpiresUtc);
        Assert.Equal(
            "Dry run approved 12 minutes ago: 2 files ready to move. " +
            "Live commit available for 18 more minutes.",
            state.Message);
    }

    [Fact]
    public void MatchingDryRun_JustNow_UsesSingularWording()
    {
        var profile = CreateProfile();
        var movies = new List<Movie> { CreateReadyMovie(profile) };
        var approval = Approve(movies, profile, Now.AddSeconds(-10));

        var state = LiveCommitState.Evaluate(
            movies,
            approval,
            profile,
            LibraryGoal.OrganizeNewMovies,
            Now);

        Assert.True(state.Enabled);
        Assert.StartsWith("Dry run approved just now: 1 file ready to move.", state.Message);
        Assert.EndsWith("available for 30 more minutes.", state.Message);
    }

    private static DryRunApproval Approve(
        List<Movie> movies,
        DestinationProfile profile,
        DateTime createdUtc)
    {
        return new DryRunApproval(
            profile.Id,
            profile.Revision,
            LibraryGoal.OrganizeNewMovies,
            PlanFingerprintBuilder.Build(movies, profile, LibraryGoal.OrganizeNewMovies),
            createdUtc);
    }

    private static DestinationProfile CreateProfile()
    {
        return new DestinationProfile
        {
            Name = "Live Button Profile",
            DestinationRoot = Path.Combine(Path.GetTempPath(), "PIM-LiveButton-Unused")
        };
    }

    private static Movie CreateReadyMovie(
        DestinationProfile profile,
        string fileName = "Ready.Movie.2024.mkv")
    {
        return new Movie
        {
            Title = Path.GetFileNameWithoutExtension(fileName),
            Year = 2024,
            ImdbId = "tt1234567",
            FileName = fileName,
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "PIM-LiveButton-Source", fileName),
            MetadataFetched = true,
            MatchConfidence = 100,
            TargetPath = Path.Combine(profile.DestinationRoot, fileName),
            DestinationProfileId = profile.Id,
            DestinationProfileRevision = profile.Revision,
            PlannedLibraryGoal = LibraryGoal.OrganizeNewMovies,
            ApprovedForCommit = true
        };
    }
}
