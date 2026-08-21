using Microsoft.Extensions.Logging;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services
{
    public class MovieConflictDetectionService : IMovieConflictDetectionService
    {
        private const string StandardEditionKey = "<standard>";

        private readonly IDestinationConflictService _destinationConflictService;
        private readonly IPlexLibraryConflictService _plexLibraryConflictService;
        private readonly ILogger<MovieConflictDetectionService>? _logger;

        public MovieConflictDetectionService(
            IDestinationConflictService destinationConflictService,
            IPlexLibraryConflictService plexLibraryConflictService,
            ILogger<MovieConflictDetectionService>? logger = null)
        {
            _destinationConflictService = destinationConflictService;
            _plexLibraryConflictService = plexLibraryConflictService;
            _logger = logger;
        }

        public void ClearConflictState(List<Movie> movies)
        {
            foreach (var movie in movies)
            {
                ClearDestinationConflictState(movie);
                ClearPlexConflictState(movie);
            }
        }

        public void ClearDestinationConflictState(List<Movie> movies)
        {
            _destinationConflictService.InvalidateCache();

            foreach (var movie in movies)
                ClearDestinationConflictState(movie);
        }

        public void ClearPlexConflictState(List<Movie> movies)
        {
            foreach (var movie in movies)
                ClearPlexConflictState(movie);
        }

        public void ApplyConflictDetection(
            List<Movie> movies,
            string outputPath,
            string? sourceRoot = null,
            LibraryGoal libraryGoal = LibraryGoal.Consolidation)
        {
            // Remember prior conflict rows before clearing their transient state.
            // This allows a commit-time recheck to evaluate the same files again.
            var previousConflictIds = movies
                .Where(movie => IsConflictReviewReason(movie.ReviewReason))
                .Select(movie => movie.Id)
                .ToHashSet();

            ClearConflictState(movies);
            _destinationConflictService.InvalidateCache();
            var normalizedOutputPath = NormalizePath(outputPath);

            var candidates = movies
                .Where(movie => IsConflictCandidate(movie, previousConflictIds))
                .ToList();

            ApplyIncomingTargetPathCollisions(candidates);
            ApplyIncomingSameEditionCollisions(candidates);

            foreach (var movie in candidates)
            {
                if (!movie.HasDestinationConflict)
                {
                    var destinationResult = _destinationConflictService.Check(
                        movie,
                        normalizedOutputPath);

                    if (destinationResult.HasConflict)
                    {
                        SetDestinationConflict(
                            movie,
                            destinationResult.Message,
                            destinationResult.ExistingPath);
                    }
                }

                // Plex is an independent read-only safety boundary. Evaluate it
                // even when an incoming or destination-filesystem conflict was
                // already found so neither reason hides the other.
                var plexResult = _plexLibraryConflictService.Check(movie);
                var plexPolicy = plexResult.HasConflict
                    ? PlexMatchPolicy.Evaluate(
                        libraryGoal,
                        sourceRoot,
                        movie,
                        plexResult)
                    : null;

                _logger?.LogInformation(
                    "Plex result for {Title} ({Year}), IMDb {ImdbId}, target {TargetPath}, goal {LibraryGoal}: HasConflict={HasConflict}, Type={ConflictType}, TrackedMigration={TrackedMigration}, ExistingPath={ExistingPath}.",
                    movie.Title,
                    movie.Year,
                    movie.ImdbId ?? "<none>",
                    movie.TargetPath ?? "<none>",
                    libraryGoal,
                    plexResult.HasConflict,
                    plexResult.ConflictType,
                    plexPolicy?.IsTrackedMigration ?? false,
                    plexResult.ExistingPath ?? "<none>");

                if (plexPolicy?.IsTrackedMigration == true)
                {
                    movie.IsPlexTrackedMigration = true;
                    movie.PlexTrackedMigrationReason = plexPolicy.Message;
                    movie.ExistingPlexLibraryPath = plexResult.ExistingPath;
                }
                else if (plexResult.HasConflict)
                {
                    movie.HasPlexLibraryConflict = true;
                    movie.PlexLibraryConflictReason = plexPolicy?.Message ??
                                                      plexResult.Message;
                    movie.ExistingPlexLibraryPath = plexResult.ExistingPath;
                }

                if (movie.HasDestinationConflict || movie.HasPlexLibraryConflict)
                {
                    MarkMovieForConflictReview(movie);

                    _logger?.LogInformation(
                        "Conflict state applied to {Title} ({Year}): DestinationConflict={DestinationConflict}, PlexConflict={PlexConflict}, NeedsReview={NeedsReview}, ApprovedForCommit={ApprovedForCommit}, Status={Status}.",
                        movie.Title,
                        movie.Year,
                        movie.HasDestinationConflict,
                        movie.HasPlexLibraryConflict,
                        movie.NeedsReview,
                        movie.ApprovedForCommit,
                        movie.Status);
                }
            }
        }

        private static void ApplyIncomingTargetPathCollisions(
            List<Movie> candidates)
        {
            var collisionGroups = candidates
                .GroupBy(
                    movie => NormalizePath(movie.TargetPath!),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var group in collisionGroups)
            {
                var groupMovies = group.ToList();

                foreach (var movie in groupMovies)
                {
                    var otherMovie = groupMovies
                        .First(other => other.Id != movie.Id);

                    SetDestinationConflict(
                        movie,
                        "Multiple incoming files resolve to the same target path.",
                        otherMovie.OriginalFilePath);

                    MarkMovieForConflictReview(movie);
                }
            }
        }

        private static void ApplyIncomingSameEditionCollisions(
            List<Movie> candidates)
        {
            var collisionGroups = candidates
                .Where(movie =>
                    !movie.HasDestinationConflict &&
                    !string.IsNullOrWhiteSpace(movie.ImdbId))
                .GroupBy(
                    BuildMovieEditionIdentityKey,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var group in collisionGroups)
            {
                var groupMovies = group.ToList();

                foreach (var movie in groupMovies)
                {
                    var otherMovie = groupMovies
                        .First(other => other.Id != movie.Id);

                    SetDestinationConflict(
                        movie,
                        "Multiple incoming files represent the same IMDb ID and edition.",
                        otherMovie.OriginalFilePath);

                    MarkMovieForConflictReview(movie);
                }
            }
        }

        private static bool IsConflictCandidate(
            Movie movie,
            HashSet<Guid> previousConflictIds)
        {
            if (movie.HasError || string.IsNullOrWhiteSpace(movie.TargetPath))
                return false;

            if (movie.ApprovedForCommit)
                return true;

            // Review blocks commit but must not hide other safety findings.
            // Continue read-only conflict checks so the reviewer sees every
            // known reason that the item cannot be approved.
            if (movie.NeedsReview)
                return true;

            // DuplicateService deliberately sends equally ranked copies to review.
            // They still need proposed-path comparison so PIM can report the more
            // precise incoming collision instead of only "No clear best file".
            if (IsAmbiguousDuplicateReview(movie.ReviewReason))
                return true;

            // Re-evaluate prior conflict rows during dry-run/commit checks.
            return previousConflictIds.Contains(movie.Id);
        }

        private static bool IsAmbiguousDuplicateReview(string? reviewReason)
        {
            return !string.IsNullOrWhiteSpace(reviewReason) &&
                   reviewReason.StartsWith(
                       "No clear best file for ",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void SetDestinationConflict(
            Movie movie,
            string? message,
            string? existingPath)
        {
            movie.HasDestinationConflict = true;
            movie.DestinationConflictReason = string.IsNullOrWhiteSpace(message)
                ? "A destination conflict was detected."
                : message;
            movie.ExistingDestinationPath = existingPath;
        }

        private static void ClearDestinationConflictState(Movie movie)
        {
            movie.HasDestinationConflict = false;
            movie.DestinationConflictReason = null;
            movie.ExistingDestinationPath = null;

            RemoveConflictReviewReasons(
                movie,
                IsDestinationConflictReviewReason);
        }

        private static void ClearPlexConflictState(Movie movie)
        {
            movie.HasPlexLibraryConflict = false;
            movie.PlexLibraryConflictReason = null;
            movie.ExistingPlexLibraryPath = null;
            movie.IsPlexTrackedMigration = false;
            movie.PlexTrackedMigrationReason = null;

            RemoveConflictReviewReasons(
                movie,
                IsPlexConflictReviewReason);
        }

        private static void RemoveConflictReviewReasons(
            Movie movie,
            Func<string, bool> shouldRemove)
        {
            if (string.IsNullOrWhiteSpace(movie.ReviewReason))
                return;

            var remainingReasons = movie.ReviewReason.Split(
                    " | ",
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Where(reason => !shouldRemove(reason))
                .ToList();

            movie.ReviewReason = remainingReasons.Count == 0
                ? null
                : string.Join(" | ", remainingReasons);
            movie.NeedsReview = remainingReasons.Count > 0;

            if (string.Equals(
                    movie.Status,
                    "Needs Review - Conflict Detected",
                    StringComparison.OrdinalIgnoreCase))
            {
                movie.Status = movie.ReviewReason ?? "Pending";
            }
        }

        private static void MarkMovieForConflictReview(Movie movie)
        {
            var reasons = new List<string>();

            if (movie.HasDestinationConflict)
            {
                reasons.Add($"Destination conflict: {movie.DestinationConflictReason}");
            }

            if (movie.HasPlexLibraryConflict)
            {
                reasons.Add($"Plex library conflict: {movie.PlexLibraryConflictReason}");
            }

            movie.RequireReview(string.Join(" | ", reasons));
            movie.Status = "Needs Review - Conflict Detected";
        }

        private static bool IsConflictReviewReason(string? reviewReason)
        {
            if (string.IsNullOrWhiteSpace(reviewReason))
                return false;

            return reviewReason.Contains(
                       "Destination conflict:",
                       StringComparison.OrdinalIgnoreCase) ||
                   reviewReason.Contains(
                       "Plex library conflict:",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDestinationConflictReviewReason(string reason)
        {
            return reason.StartsWith(
                "Destination conflict:",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlexConflictReviewReason(string reason)
        {
            return reason.StartsWith(
                "Plex library conflict:",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMovieEditionIdentityKey(Movie movie)
        {
            var imdbId = movie.ImdbId?
                .Trim()
                .ToUpperInvariant() ?? string.Empty;

            return $"{imdbId}|{NormalizeEdition(movie.VersionTag)}";
        }

        private static string NormalizeEdition(string? edition)
        {
            if (string.IsNullOrWhiteSpace(edition) ||
                edition.Trim().Equals(
                    "Alternate Version",
                    StringComparison.OrdinalIgnoreCase))
            {
                return StandardEditionKey;
            }

            return edition.Trim().ToUpperInvariant();
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}
