namespace PIM.Core.Models;

public sealed record DryRunApproval(
    Guid ProfileId,
    int ProfileRevision,
    LibraryGoal LibraryGoal,
    string PlanFingerprint,
    DateTime CreatedUtc)
{
    /// <summary>
    /// How long a dry run authorizes a live commit. Matches the previous
    /// in-memory cache lifetime so persisting approvals does not lengthen it.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    public bool IsExpired(DateTime utcNow) =>
        CreatedUtc > utcNow || utcNow - CreatedUtc >= MaxAge;

    public bool Matches(
        DestinationProfile profile,
        LibraryGoal libraryGoal,
        string currentPlanFingerprint)
    {
        return ProfileId == profile.Id &&
               ProfileRevision == profile.Revision &&
               LibraryGoal == libraryGoal &&
               string.Equals(
                   PlanFingerprint,
                   currentPlanFingerprint,
                   StringComparison.Ordinal);
    }
}
