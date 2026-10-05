using PIM.Core.Models;

namespace PIM.Core.Interfaces;

public interface IMetadataSuggestionService
{
    Task<bool> AcceptAsync(
        Movie movie,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal);

    /// <summary>
    /// Looks up a user-supplied IMDb ID for a movie whose identity is still
    /// unresolved, then rebuilds the plan. The ID is validated against the
    /// title and year parsed from the file; when they disagree the movie stays
    /// in review with the ID's real identity offered as a suggestion.
    /// Returns false, changing nothing, when the movie is not eligible.
    /// </summary>
    Task<bool> ApplyImdbIdAsync(
        Movie movie,
        string imdbId,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal);

    /// <summary>
    /// Records the owner's decision to keep the title and year from the file
    /// name for a movie whose IMDb ID OMDb found under different wording, then
    /// looks the ID up again (for rating and genre) and rebuilds the plan. The
    /// IMDb ID is unchanged. Returns false, changing nothing, when the movie
    /// is not eligible.
    /// </summary>
    Task<bool> KeepFileIdentityAsync(
        Movie movie,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal);

    /// <summary>
    /// Withdraws that decision: the ID is looked up again without it, so the
    /// difference from OMDb returns to the owner as a decision. Returns false,
    /// changing nothing, when there is no such decision to undo.
    /// </summary>
    Task<bool> UndoKeepFileIdentityAsync(
        Movie movie,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal);
}
