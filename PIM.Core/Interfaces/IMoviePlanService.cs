using PIM.Core.Models;

namespace PIM.Core.Interfaces;

/// <summary>
/// Rebuilds the policy-dependent portion of an existing identified movie plan.
/// Metadata retrieval is intentionally outside this service so destination,
/// profile, and workflow changes can reuse completed identification safely.
/// </summary>
public interface IMoviePlanService
{
    void Rebuild(
        List<Movie> movies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal);
}
