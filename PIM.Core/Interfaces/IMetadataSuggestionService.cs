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
}
