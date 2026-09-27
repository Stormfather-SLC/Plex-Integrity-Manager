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
        LibraryGoal libraryGoal)
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

        movie.ClearMetadataReviewReasons();
        movie.Title = selectedTitle;
        movie.Year = selectedYear;
        movie.ImdbId = selectedImdbId;
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
