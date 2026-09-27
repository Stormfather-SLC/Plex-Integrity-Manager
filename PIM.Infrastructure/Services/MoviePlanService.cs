using Microsoft.Extensions.Logging;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

public sealed class MoviePlanService : IMoviePlanService
{
    private readonly IDuplicateService _duplicates;
    private readonly IRenameService _rename;
    private readonly IMovieConflictDetectionService _conflictDetection;
    private readonly ILogger<MoviePlanService>? _logger;

    public MoviePlanService(
        IDuplicateService duplicates,
        IRenameService rename,
        IMovieConflictDetectionService conflictDetection,
        ILogger<MoviePlanService>? logger = null)
    {
        _duplicates = duplicates;
        _rename = rename;
        _conflictDetection = conflictDetection;
        _logger = logger;
    }

    public void Rebuild(
        List<Movie> movies,
        DestinationProfile profile,
        string sourceRoot,
        LibraryGoal libraryGoal)
    {
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(profile);

        _logger?.LogInformation(
            "Rebuilding movie plan for workflow {Workflow}, profile {ProfileName} revision {ProfileRevision}, and {MovieCount} movie(s).",
            LibraryGoalSettings.GetDisplayName(libraryGoal),
            profile.Name,
            profile.Revision,
            movies.Count);

        _conflictDetection.ClearConflictState(movies);
        _duplicates.Process(movies);
        _rename.GeneratePreview(movies, profile, sourceRoot);
        _conflictDetection.ApplyConflictDetection(
            movies,
            profile.DestinationRoot,
            sourceRoot,
            libraryGoal);

        foreach (var movie in movies)
            movie.PlannedLibraryGoal = libraryGoal;
    }
}
