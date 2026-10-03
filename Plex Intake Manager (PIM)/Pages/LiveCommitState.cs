using PIM.Core.Models;

namespace PIM.Web.Pages;

/// <summary>
/// Whether the live-commit button is available, and why. The server still
/// re-checks the dry-run approval when a live commit is posted; this only
/// makes the required order of steps visible on the page.
/// </summary>
public sealed record LiveCommitState(
    bool Enabled,
    string Message,
    int FilesToMove,
    DateTime? ApprovedUtc,
    DateTime? ExpiresUtc)
{
    public static LiveCommitState Evaluate(
        IReadOnlyCollection<Movie> movies,
        DryRunApproval? approval,
        DestinationProfile profile,
        LibraryGoal libraryGoal,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(profile);

        if (movies.Count == 0)
            return Disabled("Scan and identify movies, then run a dry run.");

        // The store already drops approvals older than DryRunApproval.MaxAge.
        if (approval == null || approval.IsExpired(utcNow))
        {
            return Disabled(
                "Run a dry run first. A dry run approves the live commit for " +
                $"{DryRunApproval.MaxAge.TotalMinutes:0} minutes, and any change in between cancels it.");
        }

        var currentPlan = PlanFingerprintBuilder.Build(movies, profile, libraryGoal);

        if (!approval.Matches(profile, libraryGoal, currentPlan))
            return Disabled("Things changed since the last dry run. Run the dry run again.");

        var filesToMove = movies.Count(movie =>
            movie.ApprovedForCommit &&
            !movie.NeedsReview &&
            !movie.HasError);

        if (filesToMove == 0)
            return Disabled("The last dry run found nothing to move.");

        var expires = approval.CreatedUtc + DryRunApproval.MaxAge;
        var minutesAgo = Math.Max(0, (int)Math.Floor((utcNow - approval.CreatedUtc).TotalMinutes));
        var minutesLeft = Math.Max(1, (int)Math.Ceiling((expires - utcNow).TotalMinutes));

        return new LiveCommitState(
            true,
            $"Dry run approved {(minutesAgo == 0 ? "just now" : $"{minutesAgo} minute{(minutesAgo == 1 ? "" : "s")} ago")}: " +
            $"{filesToMove} file{(filesToMove == 1 ? "" : "s")} ready to move. " +
            $"Live commit available for {minutesLeft} more minute{(minutesLeft == 1 ? "" : "s")}.",
            filesToMove,
            approval.CreatedUtc,
            expires);
    }

    private static LiveCommitState Disabled(string message) =>
        new(false, message, 0, null, null);
}
