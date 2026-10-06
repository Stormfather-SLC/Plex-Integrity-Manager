using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

public sealed class MetadataSuggestionService : IMetadataSuggestionService
{
    private readonly IMetadataService _metadata;
    private readonly IMoviePlanService _moviePlan;

    public MetadataSuggestionService(
        IMetadataService metadata,
        IMoviePlanService moviePlan)
    {
        _metadata = metadata;
        _moviePlan = moviePlan;
    }

    public async Task<bool> AcceptAsync(
        Movie movie,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal,
        bool rebuildPlan = true)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(allMovies);
        ArgumentNullException.ThrowIfNull(profile);

        if (!movie.CanAcceptMetadataSuggestion ||
            !allMovies.Any(candidate => candidate.Id == movie.Id))
        {
            return false;
        }

        var selectedTitle = movie.SuggestedTitle!;
        var selectedYear = movie.SuggestedYear!.Value;
        var selectedImdbId = movie.SuggestedImdbId!;

        // Accepting OMDb's wording replaces any earlier decision to keep the
        // file's own.
        movie.ClearMetadataReviewReasons();
        movie.FileIdentityKeptForImdbId = null;
        movie.Title = selectedTitle;
        movie.Year = selectedYear;
        movie.ImdbId = selectedImdbId;
        movie.MetadataFetched = false;
        movie.ApprovedForCommit = false;

        await _metadata.EnrichAsync(movie);

        if (rebuildPlan)
        {
            _moviePlan.Rebuild(
                allMovies,
                profile,
                sourceRoot,
                libraryGoal);
        }

        return true;
    }

    public async Task<bool> ApplyImdbIdAsync(
        Movie movie,
        string imdbId,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(allMovies);
        ArgumentNullException.ThrowIfNull(profile);

        if (!movie.CanEnterImdbId ||
            !ImdbIdInput.TryNormalize(imdbId, out var normalizedImdbId) ||
            !allMovies.Any(candidate => candidate.Id == movie.Id))
        {
            return false;
        }

        // Keep the parsed title and year so enrichment validates the supplied
        // ID against them instead of trusting it blindly. A newly entered ID
        // is always validated afresh, whatever was decided for an earlier one.
        movie.ClearMetadataReviewReasons();
        movie.FileIdentityKeptForImdbId = null;
        movie.ImdbId = normalizedImdbId;
        movie.MetadataFetched = false;
        movie.MetadataMatchedByImdbId = false;
        movie.ApprovedForCommit = false;

        await _metadata.EnrichAsync(movie);

        _moviePlan.Rebuild(
            allMovies,
            profile,
            sourceRoot,
            libraryGoal);

        return true;
    }

    public async Task<bool> KeepFileIdentityAsync(
        Movie movie,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal,
        bool rebuildPlan = true)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(allMovies);
        ArgumentNullException.ThrowIfNull(profile);

        if (!movie.CanKeepFileIdentity ||
            !allMovies.Any(candidate => candidate.Id == movie.Id))
        {
            return false;
        }

        // The file's own title and year stay. OMDb's is used only for a part
        // the file name did not provide (a title that could not be read).
        var keptTitle = movie.FileIdentityTitle;
        var keptYear = movie.FileIdentityYear;

        movie.ClearMetadataReviewReasons();
        movie.Title = keptTitle;
        movie.Year = keptYear;
        movie.FileIdentityKeptForImdbId = movie.ImdbId;
        movie.MetadataFetched = false;
        movie.ApprovedForCommit = false;

        // Looked up again so OMDb's rating and genre for this ID are recorded;
        // the lookup honours the decision instead of raising the same conflict.
        await _metadata.EnrichAsync(movie);

        if (rebuildPlan)
        {
            _moviePlan.Rebuild(
                allMovies,
                profile,
                sourceRoot,
                libraryGoal);
        }

        return true;
    }

    public async Task<bool> UndoKeepFileIdentityAsync(
        Movie movie,
        List<Movie> allMovies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal)
    {
        ArgumentNullException.ThrowIfNull(movie);
        ArgumentNullException.ThrowIfNull(allMovies);
        ArgumentNullException.ThrowIfNull(profile);

        if (!movie.IsFileIdentityKept ||
            !allMovies.Any(candidate => candidate.Id == movie.Id))
        {
            return false;
        }

        // Without the decision the lookup compares OMDb's wording with the
        // file's again, so the difference returns to the owner.
        movie.ClearMetadataReviewReasons();
        movie.FileIdentityKeptForImdbId = null;
        movie.MetadataFetched = false;
        movie.ApprovedForCommit = false;

        await _metadata.EnrichAsync(movie);

        _moviePlan.Rebuild(
            allMovies,
            profile,
            sourceRoot,
            libraryGoal);

        return true;
    }
}
