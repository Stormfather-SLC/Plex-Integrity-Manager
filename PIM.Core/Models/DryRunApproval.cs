namespace PIM.Core.Models;

public sealed record DryRunApproval(
    Guid ProfileId,
    int ProfileRevision,
    LibraryGoal LibraryGoal,
    string PlanFingerprint,
    DateTime CreatedUtc)
{
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
