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
}
